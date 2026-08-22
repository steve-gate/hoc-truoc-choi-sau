$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) { throw "Open CMD as Administrator, then run NANG_CAP_V7_5_4.bat." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (!(Test-Path $dotnet)) { $dotnet = "dotnet" }

$stageRoot = Join-Path $root ".build-v754"
$stageApp = Join-Path $stageRoot "App"
$stageService = Join-Path $stageRoot "Service"
$publishApp = Join-Path $root "publish\App"
$publishService = Join-Path $root "publish\Service"
$serviceName = "FocusLockGuard"

Write-Host ""
Write-Host "==> FocusLock V7.5.4 - build App + Service" -ForegroundColor Cyan
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $stageApp,$stageService -Force | Out-Null

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stageApp
if ($LASTEXITCODE -ne 0) { throw "App build failed: $LASTEXITCODE" }

& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stageService
if ($LASTEXITCODE -ne 0) { throw "Service build failed: $LASTEXITCODE" }

$stageAppExe = Join-Path $stageApp "FocusLock.exe"
$stageServiceExe = Join-Path $stageService "FocusLock.Service.exe"
if (!(Test-Path $stageAppExe) -or !(Test-Path $stageServiceExe)) { throw "Build output incomplete." }
$appHash = (Get-FileHash $stageAppExe -Algorithm SHA256).Hash

Write-Host "==> Stop UI/tray + Guard" -ForegroundColor Cyan
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20)) } catch {}
}
Start-Sleep -Milliseconds 800

function Replace-Folder([string]$source,[string]$target) {
    $last = $null
    for ($i=0; $i -lt 20; $i++) {
        try {
            if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction Stop }
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item (Join-Path $source "*") $target -Recurse -Force -ErrorAction Stop
            return
        } catch {
            $last = $_
            Start-Sleep -Milliseconds 400
        }
    }
    throw "Could not replace $target. Last error: $($last.Exception.Message)"
}

Write-Host "==> Replace App + Service" -ForegroundColor Cyan
Replace-Folder $stageApp $publishApp
Replace-Folder $stageService $publishService

$targetAppExe = Join-Path $publishApp "FocusLock.exe"
$targetServiceExe = Join-Path $publishService "FocusLock.Service.exe"
if ((Get-FileHash $targetAppExe -Algorithm SHA256).Hash -ne $appHash) { throw "App hash mismatch." }
$ver = (Get-Item $targetAppExe).VersionInfo.FileVersion
if ($ver -notlike "7.5.4*") { throw "Wrong UI version: $ver" }

Write-Host "==> Start Guard" -ForegroundColor Cyan
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe config $serviceName binPath= ('"' + $targetServiceExe + '"') start= auto obj= LocalSystem | Out-Null
} else {
    New-Service -Name $serviceName -BinaryPathName ('"' + $targetServiceExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
Start-Service $serviceName
$svc = Get-Service $serviceName
try { $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) } catch {}
$svc.Refresh()
if ($svc.Status -ne "Running") { throw "Guard not Running." }

function Test-Pipe {
    $p=$null
    try {
        $p=New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect(1200)
        return $p.IsConnected
    } catch { return $false }
    finally { if ($null -ne $p) { $p.Dispose() } }
}
$ok=$false
for($i=0;$i -lt 15;$i++){ if(Test-Pipe){$ok=$true;break}; Start-Sleep -Milliseconds 400 }
if(-not $ok){ throw "Guard pipe not reachable." }

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.5.4 INSTALLED" -ForegroundColor Green
Write-Host "Web Focus + Web Entertainment = desktop heartbeat" -ForegroundColor Green
Write-Host "Bubble = non-activating" -ForegroundColor Green
Write-Host "Version: $ver" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green

Start-Process $targetAppExe
exit 0
