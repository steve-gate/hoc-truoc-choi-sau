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

$stage=Join-Path $root ".build-v766\App"
$target=Join-Path $root "publish\App"
if(Test-Path $stage){ Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host ""
Write-Host "==> FocusLock V7.6.6 - live compare typing challenge" -ForegroundColor Cyan

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stage
if($LASTEXITCODE -ne 0){ throw "App build failed: $LASTEXITCODE" }

$stageExe=Join-Path $stage "FocusLock.exe"
if(!(Test-Path $stageExe)){ throw "Thieu staged FocusLock.exe." }
$hash=(Get-FileHash $stageExe -Algorithm SHA256).Hash

Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if(Test-Path $target){ Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $stage "*") $target -Recurse -Force

$targetExe=Join-Path $target "FocusLock.exe"
if((Get-FileHash $targetExe -Algorithm SHA256).Hash -ne $hash){ throw "App hash mismatch." }

$ver=(Get-Item $targetExe).VersionInfo.FileVersion
if($ver -notlike "7.6.6*"){ throw "Sai App version: $ver" }

Start-Process $targetExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.6.6 INSTALLED" -ForegroundColor Green
Write-Host "Typing challenge: LIVE CORRECT/WRONG COMPARISON" -ForegroundColor Green
Write-Host "Service / Browser Core / NativeHost / Data: UNCHANGED" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
