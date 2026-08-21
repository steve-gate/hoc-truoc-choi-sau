#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

# CODE-FOLDER MODE
# Run this script from the built publish folder. Nothing is copied to Program Files.
# Runtime data is stored in .\Data beside App/Service/NativeHost/BrowserExtension.
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceDir = Join-Path $sourceRoot "Service"
$appDir = Join-Path $sourceRoot "App"
$nativeHostDir = Join-Path $sourceRoot "NativeHost"
$extensionDir = Join-Path $sourceRoot "BrowserExtension"
$dataDir = Join-Path $sourceRoot "Data"

$serviceExe = Join-Path $serviceDir "FocusLock.Service.exe"
$appExe = Join-Path $appDir "FocusLock.exe"
$nativeHostExe = Join-Path $nativeHostDir "FocusLock.NativeHost.exe"
$nativeManifest = Join-Path $nativeHostDir "com.focuslock.browserbridge.json"
$serviceName = "FocusLockGuard"
$extensionId = "njmmdgnpjlfkhcngkfbbliondpnfalnb"

# Old V5 locations, used only for one-time migration/cleanup.
$oldInstallDir = Join-Path $env:ProgramFiles "FocusLock"
$oldDataDir = Join-Path $env:ProgramData "FocusLock"

if (!(Test-Path $serviceExe)) { throw "Không tìm thấy $serviceExe. Hãy chạy build-release.ps1 trước." }
if (!(Test-Path $appExe)) { throw "Không tìm thấy $appExe. Hãy chạy build-release.ps1 trước." }
if (!(Test-Path $nativeHostExe)) { throw "Không tìm thấy $nativeHostExe. Hãy chạy build-release.ps1 trước." }
if (!(Test-Path $extensionDir)) { throw "Không tìm thấy BrowserExtension. Hãy chạy build-release.ps1 trước." }

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Create local Data folder. If an older ProgramData state exists and local Data is empty,
# migrate it so balance, rules, keys and statistics are preserved.
New-Item $dataDir -ItemType Directory -Force | Out-Null
$localHasState = Test-Path (Join-Path $dataDir "state.v2.json")
if (!$localHasState -and (Test-Path $oldDataDir)) {
    Write-Host "Đang chuyển dữ liệu cũ từ $oldDataDir -> $dataDir" -ForegroundColor Cyan
    Get-ChildItem $oldDataDir -Force -ErrorAction SilentlyContinue | Copy-Item -Destination $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Ensure LocalSystem can execute/read the runtime even when the code folder is on D:/E:.
& icacls.exe $sourceRoot /grant "*S-1-5-18:(OI)(CI)RX" /T /C | Out-Null

# Protect Data similarly to the old ProgramData layout. The service (SYSTEM) and Administrators can write it.
# This keeps the data inside your code folder while retaining the anti-tamper design as much as possible.
& icacls.exe $dataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C | Out-Null

# Native Messaging manifest points directly to this code/publish folder.
$nativeManifestObject = @{
    name = "com.focuslock.browserbridge"
    description = "FocusLock V5 Browser Bridge"
    path = $nativeHostExe
    type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
$nativeManifestJson = $nativeManifestObject | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($nativeManifest, $nativeManifestJson, (New-Object System.Text.UTF8Encoding($false)))

$nativeRegistryKeys = @(
    "HKLM:\Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge",
    "HKLM:\Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge",
    "HKLM:\Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge",
    "HKLM:\Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge"
)
foreach ($key in $nativeRegistryKeys) {
    New-Item $key -Force -Value $nativeManifest | Out-Null
}

# Register the Windows Service directly from this folder; no Program Files copy.
New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
& sc.exe description $serviceName "FocusLock V5 code-folder enforcement service. Runtime files and data stay in the selected FocusLock folder." | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null
Start-Service -Name $serviceName

# Start UI from this folder at user login.
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-Item $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name "FocusLock" -Value "`"$appExe`"" -PropertyType String -Force | Out-Null

# Remove the old Program Files copy after migration, to reclaim C: space.
# ProgramData is removed only after a state file is confirmed in local Data.
try {
    $sameInstall = [System.IO.Path]::GetFullPath($oldInstallDir).TrimEnd('\') -eq [System.IO.Path]::GetFullPath($sourceRoot).TrimEnd('\')
} catch { $sameInstall = $false }
if (!$sameInstall -and (Test-Path $oldInstallDir)) {
    Remove-Item $oldInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
$localStateOk = Test-Path (Join-Path $dataDir "state.v2.json")
$localSecretOk = Test-Path (Join-Path $dataDir "guard.secret")
if ($localStateOk -and $localSecretOk -and (Test-Path $oldDataDir)) {
    Remove-Item $oldDataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "" 
Write-Host "FocusLock V5 CODE-FOLDER đã được cài." -ForegroundColor Green
Write-Host "Root:         $sourceRoot"
Write-Host "Service:      $serviceExe"
Write-Host "App:          $appExe"
Write-Host "Native Host:  $nativeHostExe"
Write-Host "Extension:    $extensionDir"
Write-Host "Data:         $dataDir"
Write-Host "Extension ID: $extensionId"
Write-Host ""
Write-Host "Ổ C: không còn bản copy Program Files/ProgramData của FocusLock (nếu migration thành công)." -ForegroundColor Green
Write-Host "Windows vẫn giữ vài registry/service entries rất nhỏ." -ForegroundColor DarkGray
Write-Host ""
Write-Host "BƯỚC CUỐI - Load unpacked extension:" -ForegroundColor Yellow
Write-Host "  Chrome: chrome://extensions -> Developer mode -> Load unpacked -> $extensionDir"
Write-Host "  Edge:   edge://extensions   -> Developer mode -> Load unpacked -> $extensionDir"
Write-Host ""
Write-Host "LƯU Ý: Sau khi cài, không di chuyển/đổi tên/xóa thư mục này. Nếu muốn chuyển sang ổ khác, gỡ service trước rồi cài lại từ vị trí mới." -ForegroundColor Yellow
Start-Process $appExe
