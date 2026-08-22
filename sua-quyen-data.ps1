$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "Open Command Prompt as Administrator, then run SUA_QUYEN_DATA.bat."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dataDir = Join-Path $root "publish\Data"
$serviceName = "FocusLockGuard"
$pipeName = "FocusLock.Guard.V5"

if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

Write-Host ""
Write-Host "==> Stopping FocusLockGuard" -ForegroundColor Cyan
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(15)) } catch {}
}
Start-Sleep -Milliseconds 700

Write-Host "==> Repairing Data permissions" -ForegroundColor Cyan

# Clear attributes temporarily; this avoids a damaged read-only attribute from
# blocking repair. guard.secret may later be marked Hidden/System by the service.
Get-ChildItem -LiteralPath $dataDir -Force -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object {
        try {
            if (-not $_.PSIsContainer) {
                $_.IsReadOnly = $false
            }
        } catch {}
    }

# Reset child ACLs first to remove stale explicit DENY / non-inheriting ACLs.
& icacls.exe $dataDir /reset /T /C /Q | Out-Null

# The FocusLock code folder can live on D:/E:/etc.  SYSTEM must be able to
# traverse the whole published tree and fully control Data.
$publishRoot = Join-Path $root "publish"
& icacls.exe $publishRoot /grant "*S-1-5-18:(OI)(CI)RX" /T /C /Q | Out-Null

# Remove inheritance from Data after reset, then set explicit authoritative ACLs.
# SID form is language-independent:
#   S-1-5-18      = LocalSystem
#   S-1-5-32-544  = Built-in Administrators
& icacls.exe $dataDir /inheritance:r /T /C /Q | Out-Null
& icacls.exe $dataDir /grant:r `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    /T /C /Q | Out-Null

# Validate that SYSTEM actually appears in the ACL.
$aclText = (& icacls.exe $dataDir) -join "`n"
if ($aclText -notmatch 'S-1-5-18|SYSTEM') {
    throw "SYSTEM permission was not applied to publish\Data."
}

Write-Host "==> Starting FocusLockGuard" -ForegroundColor Cyan
Start-Service -Name $serviceName -ErrorAction Stop
$svc = Get-Service -Name $serviceName -ErrorAction Stop
try { $svc.WaitForStatus("Running", [TimeSpan]::FromSeconds(20)) } catch {}
$svc.Refresh()

if ($svc.Status -ne "Running") {
    throw "FocusLockGuard did not reach Running state."
}

function Test-Pipe([int]$timeoutMs = 1200) {
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut
        )
        $pipe.Connect($timeoutMs)
        return $pipe.IsConnected
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $pipe) { $pipe.Dispose() }
    }
}

Write-Host "==> Waiting for Guard Named Pipe" -ForegroundColor Cyan
$pipeOk = $false
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Pipe 1000) {
        $pipeOk = $true
        break
    }
    Start-Sleep -Milliseconds 400
}

if (-not $pipeOk) {
    $svc.Refresh()
    Write-Host ""
    Write-Host "Guard status: $($svc.Status)" -ForegroundColor Yellow
    Write-Host "Data ACL:" -ForegroundColor Yellow
    & icacls.exe $dataDir

    $events = Get-WinEvent -FilterHashtable @{
        LogName='Application'
        StartTime=(Get-Date).AddMinutes(-5)
    } -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProviderName -match 'FocusLock|\.NET Runtime|Application Error' -or
            $_.Message -match 'FocusLock'
        } |
        Select-Object -First 8

    if ($events) {
        Write-Host ""
        Write-Host "Recent FocusLock events:" -ForegroundColor Yellow
        foreach ($e in $events) {
            Write-Host "[$($e.TimeCreated)] $($e.ProviderName) ID=$($e.Id)"
            Write-Host (($e.Message -replace "`r","") -replace "`n"," | ")
        }
    }

    throw "Data ACL was repaired, but Guard Named Pipe is still not reachable."
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - DATA PERMISSIONS + GUARD + NAMED PIPE ARE WORKING" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""

$appExe = Join-Path $root "publish\App\FocusLock.exe"
if (Test-Path $appExe) {
    Start-Process $appExe
}
