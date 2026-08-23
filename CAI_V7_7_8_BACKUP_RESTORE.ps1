$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    $p=New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if(-not (Test-Admin)){ throw "Mo CMD bang Run as administrator." }

$packageRoot=Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot=(Get-Location).Path
$payload=Join-Path $packageRoot "payload"

if(!(Test-Path (Join-Path $projectRoot "FocusLock.sln"))){
    throw "Hay chay BAT khi CMD dang dung tai thu muc goc project FocusLock."
}

$stamp=Get-Date -Format "yyyyMMdd-HHmmss"
$sourceBackup=Join-Path $projectRoot ".source-backups\V7.7.8.1-$stamp"
$runtimeBackup=Join-Path $projectRoot ".runtime-backups\V7.7.8.1-$stamp"
$stage=Join-Path $projectRoot ".build-v778"
$appStage=Join-Path $stage "App"
$svcStage=Join-Path $stage "Service"

$relativeFiles=@(
"FocusLock.Shared\Models\AppState.cs",
"FocusLock.Shared\Models\BlockProfile.cs",
"FocusLock.Shared\Models\BrowserRule.cs",
"FocusLock.Shared\Models\ControlPolicy.cs",
"FocusLock.Shared\Models\TrackedApp.cs",
"FocusLock.Shared\Models\UserSettings.cs",
"FocusLock.Shared\Protocol\BrowserDecision.cs",
"FocusLock.Shared\Protocol\ServiceSnapshot.cs",
"FocusLock.Shared\Protocol\PipeRequest.cs",
"FocusLock.Shared\Utilities\BrowserRuleUrlHelper.cs",
"FocusLock.Shared\Utilities\SettingsChallengeComparer.cs",
"FocusLock.Shared\Utilities\FocusSessionRewardCalculator.cs",
"FocusLock.Shared\FocusLock.Shared.csproj",
"FocusLock.Service\Services\FocusAuthorityEngine.cs",
"FocusLock.Service\Services\SecureStateStore.cs",
"FocusLock.Service\FocusLock.Service.csproj",
"FocusLock.App\MainWindow.xaml",
"FocusLock.App\MainWindow.xaml.cs",
"FocusLock.App\BubbleWindow.xaml",
"FocusLock.App\BubbleWindow.xaml.cs",
"FocusLock.App\ProfileCenterWindow.xaml",
"FocusLock.App\ProfileCenterWindow.xaml.cs",
"FocusLock.App\ProfileEditorWindow.xaml",
"FocusLock.App\ProfileEditorWindow.xaml.cs",
"FocusLock.App\FocusLock.App.csproj"
)

function Restore-Source {
    foreach($rel in $relativeFiles){
        $bak=Join-Path $sourceBackup $rel
        $dst=Join-Path $projectRoot $rel
        if(Test-Path $bak){
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            Copy-Item $bak $dst -Force
        } elseif(Test-Path $dst) {
            Remove-Item $dst -Force -ErrorAction SilentlyContinue
        }
    }
}

function Replace-Dir([string]$src,[string]$dst){
    if(Test-Path $dst){Remove-Item $dst -Recurse -Force}
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Copy-Item (Join-Path $src "*") $dst -Recurse -Force
}

Write-Host ""
Write-Host "==> V7.7.8.1 - Backup / Restore compile hotfix" -ForegroundColor Cyan
Write-Host "Source backup: $sourceBackup" -ForegroundColor DarkGray

# Backup current source, then apply the cumulative tested payload.
foreach($rel in $relativeFiles){
    $current=Join-Path $projectRoot $rel
    $bak=Join-Path $sourceBackup $rel

    if(Test-Path $current){
        New-Item -ItemType Directory -Path (Split-Path -Parent $bak) -Force | Out-Null
        Copy-Item $current $bak -Force
    }

    $incoming=Join-Path $payload $rel
    if(!(Test-Path $incoming)){throw "Payload thieu: $rel"}
    New-Item -ItemType Directory -Path (Split-Path -Parent $current) -Force | Out-Null
    Copy-Item $incoming $current -Force
}

$dotnet=Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
if(!(Test-Path $dotnet)){ $dotnet="dotnet" }

if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
New-Item -ItemType Directory -Path $appStage,$svcStage -Force | Out-Null

try {
    Write-Host "==> Build App - runtime cu van dang chay" -ForegroundColor Cyan
    & $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appStage
    if($LASTEXITCODE -ne 0){throw "App build failed: $LASTEXITCODE"}

    Write-Host "==> Build Service - runtime cu van dang chay" -ForegroundColor Cyan
    & $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $svcStage
    if($LASTEXITCODE -ne 0){throw "Service build failed: $LASTEXITCODE"}
}
catch {
    Write-Host "[ROLLBACK] Build loi -> khoi phuc source cu. Runtime cu chua bi thay." -ForegroundColor Yellow
    Restore-Source
    throw
}

# Both builds passed. Back up current runtime before replacement.
$publish=Join-Path $projectRoot "publish"
New-Item -ItemType Directory -Path $runtimeBackup -Force | Out-Null
if(Test-Path (Join-Path $publish "App")){
    Copy-Item (Join-Path $publish "App") (Join-Path $runtimeBackup "App") -Recurse -Force
}
if(Test-Path (Join-Path $publish "Service")){
    Copy-Item (Join-Path $publish "Service") (Join-Path $runtimeBackup "Service") -Recurse -Force
}

try {
    Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $svc=Get-Service FocusLockGuard -ErrorAction SilentlyContinue
    if($svc -and $svc.Status -ne "Stopped"){
        Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue
        try{$svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(15))}catch{}
    }
    Start-Sleep -Milliseconds 700

    Replace-Dir $appStage (Join-Path $publish "App")
    Replace-Dir $svcStage (Join-Path $publish "Service")

    # Guard runs as LocalSystem. Keep existing Data but explicitly preserve SYSTEM
    # write access so guard.secret/state files cannot fail with Access denied.
    $dataDir=Join-Path $publish "Data"
    if(Test-Path $dataDir){
        & icacls.exe $dataDir /grant '*S-1-5-18:(OI)(CI)F' /T /C | Out-Null
        if($LASTEXITCODE -ne 0){throw "Khong cap duoc quyen SYSTEM cho publish\Data."}
    }

    $svcExe=Join-Path $publish "Service\FocusLock.Service.exe"
    if(Get-Service FocusLockGuard -ErrorAction SilentlyContinue){
        & sc.exe config FocusLockGuard binPath= ('"' + $svcExe + '"') start= auto obj= LocalSystem | Out-Null
    }else{
        New-Service -Name FocusLockGuard -BinaryPathName ('"' + $svcExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
    }

    Start-Service FocusLockGuard
    $svcCheck=Get-Service FocusLockGuard
    try{$svcCheck.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
    $svcCheck.Refresh()
    if($svcCheck.Status -ne "Running"){throw "FocusLockGuard khong Running."}
    Start-Sleep -Seconds 2
    $svcCheck.Refresh()
    if($svcCheck.Status -ne "Running"){throw "FocusLockGuard khoi dong roi dung lai. Kiem tra Event Viewer/log Guard."}

    $appExe=Join-Path $publish "App\FocusLock.exe"
    $ver=(Get-Item $appExe).VersionInfo.FileVersion
    if($ver -notlike "7.7.8*"){throw "Sai App version: $ver"}

    Start-Process $appExe
}
catch {
    Write-Host "[ROLLBACK] Deploy loi -> phuc hoi runtime truoc V7.7.8." -ForegroundColor Yellow

    Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue

    if(Test-Path (Join-Path $runtimeBackup "App")){
        Replace-Dir (Join-Path $runtimeBackup "App") (Join-Path $publish "App")
    }
    if(Test-Path (Join-Path $runtimeBackup "Service")){
        Replace-Dir (Join-Path $runtimeBackup "Service") (Join-Path $publish "Service")
    }

    $oldSvcExe=Join-Path $publish "Service\FocusLock.Service.exe"
    if(Test-Path $oldSvcExe){
        & sc.exe config FocusLockGuard binPath= ('"' + $oldSvcExe + '"') start= auto obj= LocalSystem | Out-Null
        Start-Service FocusLockGuard -ErrorAction SilentlyContinue
    }

    $oldAppExe=Join-Path $publish "App\FocusLock.exe"
    if(Test-Path $oldAppExe){Start-Process $oldAppExe}
    throw
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - V7.7.8 BACKUP / RESTORE INSTALLED" -ForegroundColor Green
Write-Host "Create full .focuslockbackup: READY" -ForegroundColor Green
Write-Host "Restore validation + automatic safety backup: READY" -ForegroundColor Green
Write-Host "Restore blocked during protections/sessions/cooldown: READY" -ForegroundColor Green
Write-Host "Cooldown + Profile reward formulas + V7.7.1-V7.7.7: KEPT" -ForegroundColor Green
Write-Host "NativeHost / publish\Data: KEPT" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
