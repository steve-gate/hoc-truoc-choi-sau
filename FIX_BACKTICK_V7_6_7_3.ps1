$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    $p=New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if(-not (Test-Admin)){ throw "Mo CMD bang Run as administrator." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$mainCs = Join-Path $root "FocusLock.App\MainWindow.xaml.cs"
$appProj = Join-Path $root "FocusLock.App\FocusLock.App.csproj"
$mainXaml = Join-Path $root "FocusLock.App\MainWindow.xaml"
$sharedComparer = Join-Path $root "FocusLock.Shared\Utilities\SettingsChallengeComparer.cs"

foreach($p in @($mainCs,$appProj,$mainXaml,$sharedComparer)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){
        throw "Thieu file can thiet: $p"
    }
}

Write-Host ""
Write-Host "==> Fixing literal backtick r/n syntax error" -ForegroundColor Cyan

$text = [IO.File]::ReadAllText($mainCs)

# Repair the exact bad text inserted by the previous PowerShell regex replacement:
# literal characters: `r`n
$literal = '`r`n'
$countBefore = ([regex]::Matches($text, [regex]::Escape($literal))).Count
if($countBefore -gt 0){
    $text = $text.Replace($literal, [Environment]::NewLine)
    Write-Host "[OK] Repaired $countBefore literal backtick newline marker(s)." -ForegroundColor Green
}else{
    Write-Host "[INFO] No literal `r`n marker found; continuing with validation." -ForegroundColor Yellow
}

# Also guarantee the previous two compile fixes remain.
if($text -notmatch '(?m)^using System\.Windows\.Documents;\s*$'){
    $text = "using System.Windows.Documents;`r`n" + $text
    Write-Host "[OK] Added System.Windows.Documents." -ForegroundColor Green
}

# Run has no Opacity property. Remove only that initializer if it somehow remains.
$text = [regex]::Replace($text, '(?m)^\s*Opacity\s*=\s*0\.55\s*,?\s*$', '')

[IO.File]::WriteAllText($mainCs, $text, (New-Object Text.UTF8Encoding($true)))

# C# source should contain no backticks at all.
$check = [IO.File]::ReadAllText($mainCs)
if($check.Contains('`')){
    $lines = $check -split "`r?`n"
    $bad = @()
    for($i=0; $i -lt $lines.Length; $i++){
        if($lines[$i].Contains('`')){
            $bad += ("line {0}: {1}" -f ($i+1), $lines[$i])
        }
    }
    throw ("Backtick van con trong MainWindow.xaml.cs:`r`n" + ($bad -join "`r`n"))
}

Write-Host "[OK] MainWindow.xaml.cs contains no invalid backtick characters." -ForegroundColor Green

# Version bump for deployed binary verification.
$proj = [IO.File]::ReadAllText($appProj)
$proj = [regex]::Replace($proj,'<Version>[^<]+</Version>','<Version>7.6.7.3</Version>',1)
$proj = [regex]::Replace($proj,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>7.6.7.3</AssemblyVersion>',1)
$proj = [regex]::Replace($proj,'<FileVersion>[^<]+</FileVersion>','<FileVersion>7.6.7.3</FileVersion>',1)
$proj = [regex]::Replace($proj,'<InformationalVersion>[^<]+</InformationalVersion>','<InformationalVersion>7.6.7.3</InformationalVersion>',1)
[IO.File]::WriteAllText($appProj,$proj,(New-Object Text.UTF8Encoding($true)))

$xaml = [IO.File]::ReadAllText($mainXaml)
$xaml = $xaml.Replace('Text="V7.6.7.2"','Text="V7.6.7.3"')
$xaml = $xaml.Replace('Text="V7.6.7.1"','Text="V7.6.7.3"')
$xaml = $xaml.Replace('Text="V7.6.7"','Text="V7.6.7.3"')
[IO.File]::WriteAllText($mainXaml,$xaml,(New-Object Text.UTF8Encoding($true)))

$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if(!(Test-Path $dotnet)){ $dotnet = "dotnet" }

$stage = Join-Path $root ".build-v7673"
$appStage = Join-Path $stage "App"
$svcStage = Join-Path $stage "Service"
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $appStage,$svcStage -Force | Out-Null

Write-Host ""
Write-Host "==> Building App" -ForegroundColor Cyan
& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $appStage
if($LASTEXITCODE -ne 0){ throw "App build failed: $LASTEXITCODE" }

Write-Host ""
Write-Host "==> Building Service" -ForegroundColor Cyan
& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $svcStage
if($LASTEXITCODE -ne 0){ throw "Service build failed: $LASTEXITCODE" }

Write-Host ""
Write-Host "==> Replacing runtime" -ForegroundColor Cyan
Get-Process FocusLock -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

$svc=Get-Service FocusLockGuard -ErrorAction SilentlyContinue
if($svc -and $svc.Status -ne "Stopped"){
    Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue
    try{$svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(15))}catch{}
}
Start-Sleep -Milliseconds 700

function Replace-Dir([string]$src,[string]$dst){
    if(Test-Path $dst){ Remove-Item $dst -Recurse -Force }
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Copy-Item (Join-Path $src "*") $dst -Recurse -Force
}

$publish = Join-Path $root "publish"
Replace-Dir $appStage (Join-Path $publish "App")
Replace-Dir $svcStage (Join-Path $publish "Service")

$svcExe = Join-Path $publish "Service\FocusLock.Service.exe"
if(Get-Service FocusLockGuard -ErrorAction SilentlyContinue){
    & sc.exe config FocusLockGuard binPath= ('"' + $svcExe + '"') start= auto obj= LocalSystem | Out-Null
}else{
    New-Service -Name FocusLockGuard -BinaryPathName ('"' + $svcExe + '"') `
        -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}

Start-Service FocusLockGuard
$s=Get-Service FocusLockGuard
try{$s.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
$s.Refresh()
if($s.Status -ne "Running"){ throw "FocusLockGuard khong Running." }

$appExe = Join-Path $publish "App\FocusLock.exe"
$ver = (Get-Item $appExe).VersionInfo.FileVersion
if($ver -notlike "7.6.7.3*"){ throw "Sai App version sau deploy: $ver" }

Start-Process $appExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.6.7.3 INSTALLED" -ForegroundColor Green
Write-Host "Literal backtick syntax error: FIXED" -ForegroundColor Green
Write-Host "Run namespace + no Opacity: VERIFIED" -ForegroundColor Green
Write-Host "Browser Core / NativeHost / publish\Data: UNCHANGED" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
