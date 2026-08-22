$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Administrator)) { throw "Administrator permission is required." }

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceDir = Join-Path $sourceRoot "Service"
$appDir = Join-Path $sourceRoot "App"
$nativeHostDir = Join-Path $sourceRoot "NativeHost"

# Newer FocusLock builds deploy the browser Native Host side-by-side so Chrome/Edge
# cannot block upgrades by keeping the previous runtime DLLs open.
$nativePointer = Join-Path $sourceRoot "nativehost.current"
if (Test-Path -LiteralPath $nativePointer -PathType Leaf) {
    try {
        $slotName = ([System.IO.File]::ReadAllText($nativePointer)).Trim()
        if (![string]::IsNullOrWhiteSpace($slotName)) {
            $slotCandidate = Join-Path $sourceRoot $slotName
            if (Test-Path -LiteralPath (Join-Path $slotCandidate "FocusLock.NativeHost.exe") -PathType Leaf) {
                $nativeHostDir = $slotCandidate
            }
        }
    } catch { }
}

$extensionDir = Join-Path $sourceRoot "BrowserExtension"
$dataDir = Join-Path $sourceRoot "Data"

$serviceExe = Join-Path $serviceDir "FocusLock.Service.exe"
$appExe = Join-Path $appDir "FocusLock.exe"
$nativeHostExe = Join-Path $nativeHostDir "FocusLock.NativeHost.exe"
$nativeManifest = Join-Path $nativeHostDir "com.focuslock.browserbridge.json"
$serviceName = "FocusLockGuard"
$extensionId = "njmmdgnpjlfkhcngkfbbliondpnfalnb"

$oldInstallDir = Join-Path $env:ProgramFiles "FocusLock"
$oldDataDir = Join-Path $env:ProgramData "FocusLock"

foreach ($item in @(
    @($serviceExe, "FocusLock.Service.exe"),
    @($appExe, "FocusLock.exe"),
    @($nativeHostExe, "FocusLock.NativeHost.exe")
)) {
    if (!(Test-Path -LiteralPath $item[0] -PathType Leaf)) { throw "$($item[1]) is missing: $($item[0])" }
}
if (!(Test-Path -LiteralPath $extensionDir -PathType Container)) { throw "BrowserExtension folder is missing: $extensionDir" }

# Stop old processes before replacing service configuration.
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# Local Data folder and one-time migration from old ProgramData layout.
New-Item -Path $dataDir -ItemType Directory -Force | Out-Null
$localHasState = Test-Path -LiteralPath (Join-Path $dataDir "state.v2.json")
if (!$localHasState -and (Test-Path -LiteralPath $oldDataDir)) {
    Write-Host "Migrating old FocusLock data to: $dataDir" -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $oldDataDir -Force -ErrorAction SilentlyContinue | Copy-Item -Destination $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

# SYSTEM needs read/execute access to binaries on D:/E:/etc.
& icacls.exe $sourceRoot /grant "*S-1-5-18:(OI)(CI)RX" /T /C /Q | Out-Null

# Repair stale ACLs from earlier portable/code-folder builds before protecting Data.
# /reset removes explicit stale DENY/non-inheriting rules on existing files such as guard.secret.
& icacls.exe $dataDir /reset /T /C /Q | Out-Null
& icacls.exe $dataDir /inheritance:r /T /C /Q | Out-Null
& icacls.exe $dataDir /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C /Q | Out-Null

# Native Messaging manifest points directly to this publish folder.
$nativeManifestObject = @{
    name = "com.focuslock.browserbridge"
    description = "FocusLock Browser Bridge"
    path = $nativeHostExe
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
$nativeManifestJson = $nativeManifestObject | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($nativeManifest, $nativeManifestJson, (New-Object System.Text.UTF8Encoding($false)))

# Register Native Messaging for the CURRENT WINDOWS USER.
# HKCU is sufficient for Chrome/Edge and avoids machine-wide registry ACL problems.
$nativeRegistryPaths = @(
    "Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge",
    "Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge"
)

$browserBridgeRegistered = $false
foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $baseKey = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        foreach ($relativePath in $nativeRegistryPaths) {
            $subKey = $null
            try {
                $subKey = $baseKey.CreateSubKey($relativePath, $true)
                if ($null -eq $subKey) { throw "Could not create HKCU registry key: $relativePath" }
                $subKey.SetValue("", $nativeManifest, [Microsoft.Win32.RegistryValueKind]::String)
                $browserBridgeRegistered = $true
            }
            finally {
                if ($null -ne $subKey) { $subKey.Dispose() }
            }
        }
    }
    catch {
        Write-Warning "Browser Bridge registry view $view could not be registered: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $baseKey) { $baseKey.Dispose() }
    }
}

if ($browserBridgeRegistered) {
    Write-Host "Browser Bridge registry: OK (HKCU)" -ForegroundColor Green
}
else {
    Write-Warning "Browser Bridge registry was not registered. Core FocusLock installation will continue."
}

# Update the existing service in place. This avoids the common "marked for deletion" race.
if ($existing) {
    & sc.exe config $serviceName binPath= ('"' + $serviceExe + '"') start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not update Windows Service configuration." }
}
else {
    New-Service -Name $serviceName -BinaryPathName ('"' + $serviceExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}

& sc.exe description $serviceName "FocusLock enforcement service. Files and data stay in the selected FocusLock folder." | Out-Null
& sc.exe config $serviceName start= auto | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

# Allow a normal authenticated user to QUERY/INTERROGATE/START this service only.
# No STOP, DELETE, CHANGE_CONFIG or WRITE_DAC rights are granted. This lets the
# FocusLock UI self-heal the Guard without running the UI as Administrator.
try {
    $sdLines = @(& sc.exe sdshow $serviceName 2>$null)
    $sd = ($sdLines | Where-Object { $_ -match '^D:' } | Select-Object -First 1)
    $startAce = '(A;;LCSWRPLO;;;AU)'
    if (![string]::IsNullOrWhiteSpace($sd) -and !$sd.Contains($startAce)) {
        $saclIndex = $sd.IndexOf('S:')
        if ($saclIndex -ge 0) {
            $newSd = $sd.Substring(0, $saclIndex) + $startAce + $sd.Substring($saclIndex)
        }
        else {
            $newSd = $sd + $startAce
        }
        & sc.exe sdset $serviceName $newSd | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not grant normal-user START permission to FocusLockGuard." }
    }
}
catch {
    Write-Warning "Could not update FocusLockGuard service permissions: $($_.Exception.Message)"
}

# Backup recovery at Windows logon. Useful when FocusLock lives on D:/E: and the
# secondary drive becomes ready slightly after the Service Control Manager boot pass.
try {
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\sc.exe') -Argument "start $serviceName"
    $triggers = @(
        (New-ScheduledTaskTrigger -AtStartup)
        (New-ScheduledTaskTrigger -AtLogOn)
    )
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName 'FocusLock Guard Recovery' -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null
}
catch {
    Write-Warning "Could not create Guard recovery task; Windows Service Automatic startup will still be used. $($_.Exception.Message)"
}

# Start now and verify the SCM actually reports Running.
$svc = Get-Service -Name $serviceName -ErrorAction Stop
if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    Start-Service -Name $serviceName -ErrorAction Stop
}
$svc = Get-Service -Name $serviceName -ErrorAction Stop
try { $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15)) } catch { }
$svc.Refresh()
if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    throw "FocusLockGuard was installed but did not reach Running state."
}

# Verify the real IPC endpoint, not just service status. Restart once if Windows
# reported Running before the Named Pipe listener was ready.
function Test-GuardPipe {
    param([int]$TimeoutMs = 1500)
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($TimeoutMs)
        return $pipe.IsConnected
    }
    catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}

$pipeOk = $false
for ($i = 0; $i -lt 8; $i++) {
    if (Test-GuardPipe 1200) { $pipeOk = $true; break }
    Start-Sleep -Milliseconds 500
}
if (!$pipeOk) {
    Write-Host "Guard is Running but pipe is not ready; restarting Guard once..." -ForegroundColor Yellow
    Restart-Service -Name $serviceName -Force -ErrorAction Stop
    Start-Sleep -Seconds 2
    for ($i = 0; $i -lt 8; $i++) {
        if (Test-GuardPipe 1200) { $pipeOk = $true; break }
        Start-Sleep -Milliseconds 500
    }
}
if (!$pipeOk) {
    throw "FocusLockGuard is Running but its Named Pipe is not reachable. Check Windows Event Viewer -> Application for FocusLock.Service errors."
}
Write-Host "FocusLock Guard: Running + pipe OK" -ForegroundColor Green

# Start UI for this Windows user at login.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name "FocusLock" -Value ('"' + $appExe + '"') -PropertyType String -Force | Out-Null

# Reclaim old C: copies only after migration is safe.
try {
    $sameInstall = [System.IO.Path]::GetFullPath($oldInstallDir).TrimEnd('\') -eq [System.IO.Path]::GetFullPath($sourceRoot).TrimEnd('\')
} catch { $sameInstall = $false }
if (!$sameInstall -and (Test-Path -LiteralPath $oldInstallDir)) {
    Remove-Item -LiteralPath $oldInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
$localStateOk = Test-Path -LiteralPath (Join-Path $dataDir "state.v2.json")
$localSecretOk = Test-Path -LiteralPath (Join-Path $dataDir "guard.secret")
if ($localStateOk -and $localSecretOk -and (Test-Path -LiteralPath $oldDataDir)) {
    Remove-Item -LiteralPath $oldDataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "INSTALL OK" -ForegroundColor Green
Write-Host "App:       $appExe"
Write-Host "Service:   $serviceExe"
Write-Host "Data:      $dataDir"
Write-Host "Extension: $extensionDir"

Start-Process -FilePath $appExe

# Explicit success for callers in the same PowerShell host.
$global:LASTEXITCODE = 0
