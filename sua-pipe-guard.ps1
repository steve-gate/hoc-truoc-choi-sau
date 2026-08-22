$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "Open Command Prompt as Administrator, then run SUA_PIPE_GUARD.bat."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnetLocal = Join-Path $root ".tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path $dotnetLocal) { $dotnetLocal } else { "dotnet" }

$stage = Join-Path $root ".build-pipefix\Service"
$target = Join-Path $root "publish\Service"
$serviceExe = Join-Path $target "FocusLock.Service.exe"
$serviceName = "FocusLockGuard"

Write-Host ""
Write-Host "==> Building ONLY FocusLock.Service" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $stage

if ($LASTEXITCODE -ne 0) {
    throw "FocusLock.Service build failed."
}

$stagedExe = Join-Path $stage "FocusLock.Service.exe"
if (-not (Test-Path $stagedExe -PathType Leaf)) {
    throw "Build finished but FocusLock.Service.exe is missing."
}

Write-Host "==> Stopping old Guard" -ForegroundColor Cyan
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(15)) } catch {}
}
Start-Sleep -Milliseconds 800

# If the service process is still alive, terminate only the PID owned by this service.
$svcInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($svcInfo -and $svcInfo.ProcessId -gt 0) {
    Stop-Process -Id $svcInfo.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

Write-Host "==> Replacing Service runtime" -ForegroundColor Cyan
for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        break
    } catch {
        if ($attempt -eq 10) { throw }
        Start-Sleep -Milliseconds 500
    }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $stage "*") $target -Recurse -Force

# Ensure LocalSystem runs the Guard even if an older installation used another account.
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe config $serviceName binPath= ('"' + $serviceExe + '"') start= auto obj= LocalSystem | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not reconfigure FocusLockGuard." }
} else {
    New-Service -Name $serviceName `
        -BinaryPathName ('"' + $serviceExe + '"') `
        -DisplayName "FocusLock Guard" `
        -StartupType Automatic | Out-Null
}

& sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Write-Host "==> Starting Guard" -ForegroundColor Cyan
Start-Service -Name $serviceName
$svc = Get-Service -Name $serviceName
try { $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) } catch {}
$svc.Refresh()

if ($svc.Status -ne "Running") {
    throw "FocusLockGuard did not reach Running state."
}

function Test-Pipe {
    param([int]$TimeoutMs = 1500)

    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', 'FocusLock.Guard.V5',
            [System.IO.Pipes.PipeDirection]::InOut
        )
        $pipe.Connect($TimeoutMs)
        if ($pipe.IsConnected) { return "OK" }
        return "NOT_CONNECTED"
    }
    catch [System.UnauthorizedAccessException] {
        return "ACCESS_DENIED"
    }
    catch [System.TimeoutException] {
        return "TIMEOUT"
    }
    catch {
        return "ERROR: " + $_.Exception.Message
    }
    finally {
        if ($null -ne $pipe) { $pipe.Dispose() }
    }
}

Write-Host "==> Testing Named Pipe" -ForegroundColor Cyan
$result = "TIMEOUT"
for ($i = 0; $i -lt 15; $i++) {
    $result = Test-Pipe 1500
    if ($result -eq "OK") { break }
    Start-Sleep -Milliseconds 500
}

if ($result -ne "OK") {
    Write-Host ""
    Write-Host "PIPE TEST FAILED: $result" -ForegroundColor Red
    $pipeLog = Join-Path $root "publish\Logs\service-pipe.log"
    if (Test-Path $pipeLog) {
        Write-Host ""
        Write-Host "Last service-pipe.log lines:" -ForegroundColor Yellow
        Get-Content $pipeLog -Tail 30
    }
    throw "Guard is Running but Named Pipe test failed: $result"
}

Write-Host ""
Write-Host "=============================================" -ForegroundColor Green
Write-Host "FOCUSLOCK GUARD FIXED: SERVICE + PIPE OK" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Green
Write-Host ""

$appExe = Join-Path $root "publish\App\FocusLock.exe"
if (Test-Path $appExe) { Start-Process $appExe }
