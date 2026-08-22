$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "Open Command Prompt as Administrator, then run HOAN_TAT_V7_5_2.bat."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root "publish"
$appExe = Join-Path $publish "App\FocusLock.exe"
$installer = Join-Path $publish "install-v5.ps1"

if (!(Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "publish\App\FocusLock.exe is missing."
}

$version = (Get-Item -LiteralPath $appExe).VersionInfo.FileVersion
Write-Host "Detected FocusLock.exe version: $version" -ForegroundColor Cyan
if ($version -notlike "7.5.2*") {
    throw "Current runtime is not V7.5.2. Do not use this no-rebuild completion script."
}

if (!(Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "publish\install-v5.ps1 is missing."
}

Write-Host ""
Write-Host "==> Completing Service + Browser Bridge installation (NO REBUILD)" -ForegroundColor Cyan
& $installer

# Verify service and pipe independently after installer returns.
$svc = Get-Service -Name "FocusLockGuard" -ErrorAction Stop
$svc.Refresh()
if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    throw "FocusLockGuard is not Running after install."
}

function Test-Pipe([int]$timeoutMs = 1500) {
    $p = $null
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect($timeoutMs)
        return $p.IsConnected
    } catch { return $false }
    finally { if ($null -ne $p) { $p.Dispose() } }
}

$pipeOk = $false
for ($i=0; $i -lt 12; $i++) {
    if (Test-Pipe 1200) { $pipeOk = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $pipeOk) {
    throw "FocusLockGuard is Running but Named Pipe is not reachable."
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.5.2 INSTALL COMPLETED" -ForegroundColor Green
Write-Host "Version: $version" -ForegroundColor Green
Write-Host "Guard: Running + Pipe OK" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Reload FocusLock Browser Bridge once in chrome://extensions or edge://extensions." -ForegroundColor Yellow

$global:LASTEXITCODE = 0
