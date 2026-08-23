$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id=[Security.Principal.WindowsIdentity]::GetCurrent()
    $p=New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if(-not (Test-Admin)){ throw "Mo CMD bang Run as administrator." }

$root=Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$payload=Join-Path $root "FocusLock-V7.6.3-HYBRID-SOURCE.zip"
if(!(Test-Path $payload)){ throw "Thieu FocusLock-V7.6.3-HYBRID-SOURCE.zip" }

$dotnet=Join-Path $root ".tools\dotnet\dotnet.exe"
if(!(Test-Path $dotnet)){ $dotnet="dotnet" }

Write-Host ""
Write-Host "FOCUSLOCK V7.6.3 HYBRID" -ForegroundColor Cyan
Write-Host "GIU: Profile / Settings Protection / Tray / Bubble / Schedule / Allowance" -ForegroundColor Green
Write-Host "KHOI PHUC: heartbeat + browser core kieu V7.0" -ForegroundColor Yellow
Write-Host "GIU NGUYEN: publish\Data + .tools" -ForegroundColor Green
Write-Host ""

Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process FocusLock.NativeHost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$svc=Get-Service FocusLockGuard -ErrorAction SilentlyContinue
if($svc -and $svc.Status -ne "Stopped"){
  Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue
  try{$svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(15))}catch{}
}
Start-Sleep -Milliseconds 700

$data=Join-Path $root "publish\Data"
$dataExists=Test-Path $data

foreach($name in @("FocusLock.App","FocusLock.Shared","FocusLock.Service","FocusLock.NativeHost","BrowserExtension")){
  $p=Join-Path $root $name
  if(Test-Path $p){ Remove-Item $p -Recurse -Force }
}
Get-ChildItem $root -Directory -Filter ".build-*" -ErrorAction SilentlyContinue |
  ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Expand-Archive -LiteralPath $payload -DestinationPath $root -Force
if($dataExists -and !(Test-Path $data)){ throw "SAFETY STOP: publish\Data bi mat." }

$stage=Join-Path $root ".build-v763"
$appStage=Join-Path $stage "App"
$svcStage=Join-Path $stage "Service"
$nhStage=Join-Path $stage "NativeHost"
New-Item -ItemType Directory -Path $appStage,$svcStage,$nhStage -Force | Out-Null

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appStage
if($LASTEXITCODE -ne 0){ throw "App build failed: $LASTEXITCODE" }

& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $svcStage
if($LASTEXITCODE -ne 0){ throw "Service build failed: $LASTEXITCODE" }

& $dotnet publish ".\FocusLock.NativeHost\FocusLock.NativeHost.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $nhStage
if($LASTEXITCODE -ne 0){ throw "NativeHost build failed: $LASTEXITCODE" }

$publish=Join-Path $root "publish"
$appTarget=Join-Path $publish "App"
$svcTarget=Join-Path $publish "Service"

function Replace-Dir([string]$src,[string]$dst){
  if(Test-Path $dst){ Remove-Item $dst -Recurse -Force }
  New-Item -ItemType Directory -Path $dst -Force | Out-Null
  Copy-Item (Join-Path $src "*") $dst -Recurse -Force
}
Replace-Dir $appStage $appTarget
Replace-Dir $svcStage $svcTarget

$extTarget=Join-Path $publish "BrowserExtension"
if(Test-Path $extTarget){ Remove-Item $extTarget -Recurse -Force }
Copy-Item (Join-Path $root "BrowserExtension") $extTarget -Recurse -Force

$slotName="NativeHost-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-v763"
$slot=Join-Path $publish $slotName
New-Item -ItemType Directory -Path $slot -Force | Out-Null
Copy-Item (Join-Path $nhStage "*") $slot -Recurse -Force
[IO.File]::WriteAllText((Join-Path $publish "nativehost.current"),$slotName,(New-Object Text.UTF8Encoding($false)))

$hostName="com.focuslock.browserbridge"
$extensionId="njmmdgnpjlfkhcngkfbbliondpnfalnb"
$hostExe=Join-Path $slot "FocusLock.NativeHost.exe"
$hostManifest=Join-Path $slot "com.focuslock.browserbridge.json"
$obj=@{
  name=$hostName
  description="FocusLock Browser Bridge"
  path=$hostExe
  type="stdio"
  allowed_origins=@("chrome-extension://$extensionId/")
} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($hostManifest,$obj,(New-Object Text.UTF8Encoding($false)))

foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
  $rk=$null
  try{
    $rk=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
    foreach($rp in @(
      "Software\Google\Chrome\NativeMessagingHosts\$hostName",
      "Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    )){
      $k=$rk.CreateSubKey($rp,$true)
      try{$k.SetValue("",$hostManifest,[Microsoft.Win32.RegistryValueKind]::String)}
      finally{$k.Dispose()}
    }
  }finally{if($rk){$rk.Dispose()}}
}

$svcExe=Join-Path $svcTarget "FocusLock.Service.exe"
if(Get-Service FocusLockGuard -ErrorAction SilentlyContinue){
  & sc.exe config FocusLockGuard binPath= ('"' + $svcExe + '"') start= auto obj= LocalSystem | Out-Null
}else{
  New-Service -Name FocusLockGuard -BinaryPathName ('"' + $svcExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
Start-Service FocusLockGuard
$s=Get-Service FocusLockGuard
try{$s.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
$s.Refresh()
if($s.Status -ne "Running"){ throw "FocusLockGuard khong Running." }

$appExe=Join-Path $appTarget "FocusLock.exe"
$ver=(Get-Item $appExe).VersionInfo.FileVersion
if($ver -notlike "7.6.3*"){ throw "Sai App version: $ver" }

Start-Process $appExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - V7.6.3 HYBRID INSTALLED" -ForegroundColor Green
Write-Host "NEW FEATURES: KEPT" -ForegroundColor Green
Write-Host "OLD V7.0 HEARTBEAT + WEB CORE: RESTORED" -ForegroundColor Green
Write-Host "publish\Data: KEPT" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Reload FocusLock Browser Bridge mot lan." -ForegroundColor Yellow
exit 0
