# FocusLock FINAL FIX3.1 UI/bootstrap registration repair
# ASCII-only / Windows PowerShell 5.1 compatible.
# Does NOT modify FocusLock data or Guard binaries.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-CodeRoot {
    $cursor = Get-Item -LiteralPath $PSScriptRoot
    for ($i = 0; $i -lt 10 -and $null -ne $cursor; $i++) {
        if (Test-Path -LiteralPath (Join-Path $cursor.FullName 'FocusLock.sln') -PathType Leaf) { return $cursor.FullName }
        $cursor = $cursor.Parent
    }
    throw 'FocusLock.sln was not found above this script.'
}
function Test-GuardPipe([int]$TimeoutMs = 1200) {
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($TimeoutMs)
        return $pipe.IsConnected
    } catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}
function Normalize-ExePath([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return '' }
    $v = [Environment]::ExpandEnvironmentVariables($raw.Trim())
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -gt 1) { return $v.Substring(1, $end - 1) }
    }
    $idx = $v.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase)
    if ($idx -ge 0) { return $v.Substring(0, $idx + 4).Trim() }
    return $v
}

$codeRoot = Find-CodeRoot
$runtime = Join-Path $codeRoot 'FocusLock-OneDir-V7.8.0.2-FINAL-FIX3'
$appExe = Join-Path $runtime 'FocusLock.exe'
$nativeExe = Join-Path $runtime 'NativeHost\FocusLock.NativeHost.exe'
$manifest = Join-Path $runtime 'NativeHost\com.focuslock.browserbridge.json'
$serviceExe = Join-Path $runtime 'Service\FocusLock.Service.exe'
$extensionId = 'njmmdgnpjlfkhcngkfbbliondpnfalnb'
$watchdog = 'FocusLock Protected Window Watchdog'

Write-Host 'FocusLock FINAL FIX3.1 - UI/bootstrap repair' -ForegroundColor Cyan
Write-Host ('Code root: ' + $codeRoot)
Write-Host ('Runtime:   ' + $runtime)
Write-Host ''

foreach ($p in @($appExe, $nativeExe, $serviceExe)) {
    if (!(Test-Path -LiteralPath $p -PathType Leaf)) { throw ('Required file missing: ' + $p) }
}

# Guard must already be the verified FIX3 Guard. Do not reconfigure it here.
$svc = Get-CimInstance Win32_Service -Filter "Name='FocusLockGuard'" -ErrorAction Stop
$currentService = Normalize-ExePath ([string]$svc.PathName)
if (![string]::Equals([IO.Path]::GetFullPath($currentService), [IO.Path]::GetFullPath($serviceExe), [StringComparison]::OrdinalIgnoreCase)) {
    throw ('Guard path is not FINAL-FIX3. Current: ' + $currentService)
}
if (!(Test-GuardPipe 1500)) { throw 'Guard pipe is not reachable. This UI-only repair will not change the Guard.' }
Write-Host 'Guard: FINAL-FIX3 + pipe reachable' -ForegroundColor Green

# Prevent the minute watchdog from racing with the repair.
$watchdogWasEnabled = $false
try {
    $task = Get-ScheduledTask -TaskName $watchdog -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        $watchdogWasEnabled = ($task.State -ne 'Disabled')
        Disable-ScheduledTask -TaskName $watchdog -ErrorAction SilentlyContinue | Out-Null
    }
} catch { }

try {
    Write-Host 'Stopping any stuck FocusLock UI process...'
    Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700

    Write-Host 'Registering Browser Bridge for FINAL-FIX3...'
    $manifestObject = @{
        name = 'com.focuslock.browserbridge'
        description = 'FocusLock Browser Bridge'
        path = $nativeExe
        type = 'stdio'
        allowed_origins = @('chrome-extension://' + $extensionId + '/')
    }
    $json = $manifestObject | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($manifest, $json, (New-Object Text.UTF8Encoding($false)))

    $paths = @(
        'Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge',
        'Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge'
    )
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            foreach ($rel in $paths) {
                $key = $base.CreateSubKey($rel, $true)
                try { $key.SetValue('', $manifest, [Microsoft.Win32.RegistryValueKind]::String) }
                finally { $key.Dispose() }
            }
        } finally { $base.Dispose() }
    }

    Write-Host 'Registering OneDir marker and startup path...'
    $productKey = 'HKCU:\Software\FocusLock'
    New-Item -Path $productKey -Force | Out-Null
    New-ItemProperty -Path $productKey -Name 'OneDirVersion' -Value '7.8.0.2' -PropertyType String -Force | Out-Null

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'FocusLock' -Value ('"' + $appExe + '"') -PropertyType String -Force | Out-Null

    # Verify what OneDirBootstrapper checks.
    $chrome = (Get-ItemProperty -Path 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge' -ErrorAction Stop).'(default)'
    if ([string]::IsNullOrWhiteSpace([string]$chrome)) {
        $chrome = (Get-Item -Path 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge').GetValue('')
    }
    $edgeKey = Get-Item -Path 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge' -ErrorAction Stop
    $edge = $edgeKey.GetValue('')
    $chromeKey = Get-Item -Path 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge' -ErrorAction Stop
    $chrome = $chromeKey.GetValue('')
    $version = (Get-ItemProperty -Path $productKey -Name OneDirVersion -ErrorAction Stop).OneDirVersion
    if (![string]::Equals([string]$chrome, $manifest, [StringComparison]::OrdinalIgnoreCase)) { throw 'Chrome NativeHost registration verification failed.' }
    if (![string]::Equals([string]$edge, $manifest, [StringComparison]::OrdinalIgnoreCase)) { throw 'Edge NativeHost registration verification failed.' }
    if ([string]$version -ne '7.8.0.2') { throw 'OneDirVersion marker verification failed.' }

    Write-Host ''
    Write-Host 'FIX3.1 UI BOOTSTRAP REPAIR SUCCESS' -ForegroundColor Green
    Write-Host 'Guard/Data: UNTOUCHED'
    Write-Host ('App: ' + $appExe)
    Write-Host ('NativeHost manifest: ' + $manifest)
    Write-Host ''
    Write-Host 'Close this Administrator window, then double-click FocusLock.exe normally.' -ForegroundColor Yellow
}
finally {
    if ($watchdogWasEnabled) {
        try { Enable-ScheduledTask -TaskName $watchdog -ErrorAction SilentlyContinue | Out-Null } catch { }
    }
}
