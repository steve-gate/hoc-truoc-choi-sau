$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) {
    throw "Open Command Prompt as Administrator, then run HOAN_TAT_V7_KHONG_BUILD.bat."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$publish = Join-Path $root "publish"
$stageNative = Join-Path $root ".build-v62\NativeHost"
$serviceName = "FocusLockGuard"
$extensionId = "njmmdgnpjlfkhcngkfbbliondpnfalnb"
$hostName = "com.focuslock.browserbridge"

function Remove-BridgeRegistration {
    $paths = @(
        "Software\Google\Chrome\NativeMessagingHosts\$hostName",
        "Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    )
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                [Microsoft.Win32.RegistryHive]::CurrentUser, $view)
            foreach ($p in $paths) {
                try { $base.DeleteSubKeyTree($p, $false) } catch { }
            }
        } finally {
            if ($null -ne $base) { $base.Dispose() }
        }
    }
}

function Register-Bridge([string]$manifestPath) {
    $paths = @(
        "Software\Google\Chrome\NativeMessagingHosts\$hostName",
        "Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    )
    $ok = $false
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                [Microsoft.Win32.RegistryHive]::CurrentUser, $view)
            foreach ($p in $paths) {
                $k = $null
                try {
                    $k = $base.CreateSubKey($p, $true)
                    if ($null -ne $k) {
                        $k.SetValue("", $manifestPath, [Microsoft.Win32.RegistryValueKind]::String)
                        $ok = $true
                    }
                } finally {
                    if ($null -ne $k) { $k.Dispose() }
                }
            }
        } finally {
            if ($null -ne $base) { $base.Dispose() }
        }
    }
    if (-not $ok) { throw "Could not register Browser Bridge in HKCU." }
}

function Stop-NativeHost {
    Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Ensure-StagedNativeHost {
    $exe = Join-Path $stageNative "FocusLock.NativeHost.exe"
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return }

    Write-Host "==> Staged NativeHost is missing; building ONLY NativeHost" -ForegroundColor Cyan
    $dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = "dotnet" }

    New-Item -ItemType Directory -Path $stageNative -Force | Out-Null
    & $dotnet publish ".\FocusLock.NativeHost\FocusLock.NativeHost.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o $stageNative
    if ($LASTEXITCODE -ne 0) { throw "NativeHost-only build failed." }
}

Write-Host ""
Write-Host "==> Switching Browser Bridge to a NEW NativeHost folder" -ForegroundColor Cyan
Remove-BridgeRegistration
Stop-NativeHost
Ensure-StagedNativeHost

# Never touch the old locked NativeHost directory again.
$slotName = "NativeHost-" + (Get-Date -Format "yyyyMMdd-HHmmss")
$slot = Join-Path $publish $slotName
if (Test-Path -LiteralPath $slot) { throw "Unexpected slot already exists: $slot" }
New-Item -ItemType Directory -Path $slot -Force | Out-Null
Copy-Item (Join-Path $stageNative "*") $slot -Recurse -Force

$hostExe = Join-Path $slot "FocusLock.NativeHost.exe"
if (!(Test-Path -LiteralPath $hostExe -PathType Leaf)) {
    throw "New NativeHost slot is missing FocusLock.NativeHost.exe."
}

$manifest = Join-Path $slot "com.focuslock.browserbridge.json"
$manifestObject = @{
    name = $hostName
    description = "FocusLock Browser Bridge"
    path = $hostExe
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
$json = $manifestObject | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText(
    (Join-Path $publish "nativehost.current"),
    $slotName,
    (New-Object System.Text.UTF8Encoding($false))
)
Register-Bridge $manifest
Write-Host "Browser Bridge: OK -> $slotName" -ForegroundColor Green

Write-Host "==> Updating Browser Extension" -ForegroundColor Cyan
$sourceExt = Join-Path $root "BrowserExtension"
$publishExt = Join-Path $publish "BrowserExtension"
if (!(Test-Path -LiteralPath $sourceExt -PathType Container)) {
    throw "BrowserExtension source folder is missing."
}
if (Test-Path -LiteralPath $publishExt) {
    try { Remove-Item -LiteralPath $publishExt -Recurse -Force -ErrorAction Stop } catch {
        # Extension files are not executed by FocusLock; overwrite in-place if Explorer/AV holds a file.
        Write-Host "BrowserExtension folder could not be removed; updating in place." -ForegroundColor Yellow
    }
}
New-Item -ItemType Directory -Path $publishExt -Force | Out-Null
Copy-Item (Join-Path $sourceExt "*") $publishExt -Recurse -Force

# Install the pointer-aware installer for future runs.
$patchedInstaller = Join-Path $root "install-v5.ps1"
if (Test-Path -LiteralPath $patchedInstaller) {
    Copy-Item -LiteralPath $patchedInstaller -Destination (Join-Path $publish "install-v5.ps1") -Force
}

Write-Host "==> Repairing Data ACL and starting Guard" -ForegroundColor Cyan
$dataDir = Join-Path $publish "Data"
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
& icacls.exe $publish /grant "*S-1-5-18:(OI)(CI)RX" /T /C /Q | Out-Null
& icacls.exe $dataDir /reset /T /C /Q | Out-Null
& icacls.exe $dataDir /inheritance:r /T /C /Q | Out-Null
& icacls.exe $dataDir /grant:r `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    /T /C /Q | Out-Null

$serviceExe = Join-Path $publish "Service\FocusLock.Service.exe"
if (!(Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
    throw "FocusLock.Service.exe is missing."
}

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    & sc.exe config $serviceName binPath= ('"' + $serviceExe + '"') start= auto obj= LocalSystem | Out-Null
} else {
    New-Service -Name $serviceName -BinaryPathName ('"' + $serviceExe + '"') `
        -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
& sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

try { Stop-Service $serviceName -Force -ErrorAction SilentlyContinue } catch { }
Start-Sleep -Milliseconds 500
Start-Service $serviceName

function Test-Pipe([int]$timeoutMs = 1000) {
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
for ($i=0; $i -lt 20; $i++) {
    if (Test-Pipe 1000) { $pipeOk = $true; break }
    Start-Sleep -Milliseconds 400
}
if (-not $pipeOk) {
    throw "FocusLockGuard started but Named Pipe is not reachable."
}
Write-Host "FocusLock Guard + Named Pipe: OK" -ForegroundColor Green

$appExe = Join-Path $publish "App\FocusLock.exe"
if (Test-Path -LiteralPath $appExe) {
    Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Start-Process $appExe
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "FOCUSLOCK V7 COMPLETED - SIDE-BY-SIDE NATIVEHOST OK" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Now reload FocusLock Browser Bridge once in chrome://extensions or edge://extensions." -ForegroundColor Yellow
