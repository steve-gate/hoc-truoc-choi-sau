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
$dotnet=Join-Path $root ".tools\dotnet\dotnet.exe"
if(!(Test-Path $dotnet)){ $dotnet="dotnet" }

$stage=Join-Path $root ".build-v767"
$appStage=Join-Path $stage "App"
$svcStage=Join-Path $stage "Service"
if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
New-Item -ItemType Directory -Path $appStage,$svcStage -Force | Out-Null

Write-Host ""
Write-Host "==> FocusLock V7.6.7 - unified challenge comparer" -ForegroundColor Cyan

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appStage
if($LASTEXITCODE -ne 0){throw "App build failed: $LASTEXITCODE"}

& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $svcStage
if($LASTEXITCODE -ne 0){throw "Service build failed: $LASTEXITCODE"}

Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$svc=Get-Service FocusLockGuard -ErrorAction SilentlyContinue
if($svc -and $svc.Status -ne "Stopped"){
  Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue
  try{$svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(15))}catch{}
}
Start-Sleep -Milliseconds 700

function Replace-Dir([string]$src,[string]$dst){
  if(Test-Path $dst){Remove-Item $dst -Recurse -Force}
  New-Item -ItemType Directory -Path $dst -Force | Out-Null
  Copy-Item (Join-Path $src "*") $dst -Recurse -Force
}

$publish=Join-Path $root "publish"
Replace-Dir $appStage (Join-Path $publish "App")
Replace-Dir $svcStage (Join-Path $publish "Service")

$svcExe=Join-Path $publish "Service\FocusLock.Service.exe"
if(Get-Service FocusLockGuard -ErrorAction SilentlyContinue){
  & sc.exe config FocusLockGuard binPath= ('"' + $svcExe + '"') start= auto obj= LocalSystem | Out-Null
}else{
  New-Service -Name FocusLockGuard -BinaryPathName ('"' + $svcExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
Start-Service FocusLockGuard
$s=Get-Service FocusLockGuard
try{$s.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
$s.Refresh()
if($s.Status -ne "Running"){throw "FocusLockGuard khong Running."}

$appExe=Join-Path $publish "App\FocusLock.exe"
$ver=(Get-Item $appExe).VersionInfo.FileVersion
if($ver -notlike "7.6.7*"){throw "Sai App version: $ver"}

Start-Process $appExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.6.7 INSTALLED" -ForegroundColor Green
Write-Host "APP + GUARD: SAME SHARED COMPARER" -ForegroundColor Green
Write-Host "Typing preview: GREEN/RED/GRAY" -ForegroundColor Green
Write-Host "Browser Core / NativeHost / Data: UNCHANGED" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
