$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) { throw "Open CMD as Administrator, then run NANG_CAP_V7_5_6.bat." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (!(Test-Path $dotnet)) { $dotnet = "dotnet" }

$stageRoot = Join-Path $root ".build-v756"
$stageApp = Join-Path $stageRoot "App"
$stageService = Join-Path $stageRoot "Service"
$stageNative = Join-Path $stageRoot "NativeHost"

Write-Host ""
Write-Host "==> FocusLock V7.5.6 - building App + Service + NativeHost" -ForegroundColor Cyan
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $stageApp,$stageService,$stageNative -Force | Out-Null

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stageApp
if ($LASTEXITCODE -ne 0) { throw "App build failed: $LASTEXITCODE" }

& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stageService
if ($LASTEXITCODE -ne 0) { throw "Service build failed: $LASTEXITCODE" }

& $dotnet publish ".\FocusLock.NativeHost\FocusLock.NativeHost.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stageNative
if ($LASTEXITCODE -ne 0) { throw "NativeHost build failed: $LASTEXITCODE" }

$publish = Join-Path $root "publish"
$publishApp = Join-Path $publish "App"
$publishService = Join-Path $publish "Service"
$serviceName = "FocusLockGuard"
$hostName = "com.focuslock.browserbridge"
$extensionId = "njmmdgnpjlfkhcngkfbbliondpnfalnb"

Write-Host "==> Stop UI/tray + Guard" -ForegroundColor Cyan
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    try { $svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(20)) } catch {}
}
Start-Sleep -Milliseconds 800

function Replace-Folder([string]$source,[string]$target) {
    $last=$null
    for($i=0;$i -lt 20;$i++){
        try{
            if(Test-Path $target){Remove-Item $target -Recurse -Force -ErrorAction Stop}
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item (Join-Path $source "*") $target -Recurse -Force -ErrorAction Stop
            return
        } catch {
            $last=$_
            Start-Sleep -Milliseconds 400
        }
    }
    throw "Could not replace $target. Last error: $($last.Exception.Message)"
}

Write-Host "==> Replace App + Service" -ForegroundColor Cyan
Replace-Folder $stageApp $publishApp
Replace-Folder $stageService $publishService

Write-Host "==> Deploy NEW side-by-side NativeHost" -ForegroundColor Cyan
$slotName = "NativeHost-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-v756"
$slot = Join-Path $publish $slotName
New-Item -ItemType Directory -Path $slot -Force | Out-Null
Copy-Item (Join-Path $stageNative "*") $slot -Recurse -Force

$hostExe = Join-Path $slot "FocusLock.NativeHost.exe"
if (!(Test-Path $hostExe)) { throw "New NativeHost exe missing." }

$manifestPath = Join-Path $slot "com.focuslock.browserbridge.json"
$manifest = @{
  name = $hostName
  description = "FocusLock Browser Bridge"
  path = $hostExe
  type = "stdio"
  allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath,$manifest,(New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText((Join-Path $publish "nativehost.current"),$slotName,(New-Object System.Text.UTF8Encoding($false)))

foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
  $base=$null
  try{
    $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
    foreach($p in @(
      "Software\Google\Chrome\NativeMessagingHosts\$hostName",
      "Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    )){
      $k=$base.CreateSubKey($p,$true)
      try{$k.SetValue("",$manifestPath,[Microsoft.Win32.RegistryValueKind]::String)}
      finally{$k.Dispose()}
    }
  } finally { if($base){$base.Dispose()} }
}

# Kill only old native-host processes after registry points to new slot.
Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "==> Start Guard" -ForegroundColor Cyan
$targetServiceExe = Join-Path $publishService "FocusLock.Service.exe"
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
  & sc.exe config $serviceName binPath= ('"' + $targetServiceExe + '"') start= auto obj= LocalSystem | Out-Null
} else {
  New-Service -Name $serviceName -BinaryPathName ('"' + $targetServiceExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
Start-Service $serviceName
$svc=Get-Service $serviceName
try{$svc.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
$svc.Refresh()
if($svc.Status -ne "Running"){throw "Guard not Running."}

$targetAppExe = Join-Path $publishApp "FocusLock.exe"
$ver=(Get-Item $targetAppExe).VersionInfo.FileVersion
if($ver -notlike "7.5.6*"){throw "Wrong App version: $ver"}

Start-Process $targetAppExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.5.6 INSTALLED" -ForegroundColor Green
Write-Host "Website foreground is now verified by NativeHost Win32" -ForegroundColor Green
Write-Host "NativeHost slot: $slotName" -ForegroundColor Green
Write-Host "App version: $ver" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Chrome/Edge extension will reconnect automatically after the old NativeHost is killed." -ForegroundColor Yellow
exit 0
