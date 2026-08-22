$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw "Open Command Prompt as Administrator, then run SUA_XAML_V7_5_2_2.bat."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = "dotnet" }

$stage = Join-Path $root ".build-v7522\App"
$target = Join-Path $root "publish\App"
$stageExe = Join-Path $stage "FocusLock.exe"
$targetExe = Join-Path $target "FocusLock.exe"

Write-Host ""
Write-Host "==> FocusLock V7.5.2.2 - rebuilding ONLY the UI app" -ForegroundColor Cyan

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $stage

if ($LASTEXITCODE -ne 0) {
    throw "FocusLock.App build failed with exit code $LASTEXITCODE."
}

if (!(Test-Path -LiteralPath $stageExe -PathType Leaf)) {
    throw "Build succeeded but staged FocusLock.exe is missing."
}

$stageHash = (Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash

Write-Host "==> Stopping old FocusLock UI, including tray instance" -ForegroundColor Cyan
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Milliseconds 500

Write-Host "==> Replacing publish\App only" -ForegroundColor Cyan
$last = $null
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
        }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item (Join-Path $stage "*") $target -Recurse -Force -ErrorAction Stop
        $last = $null
        break
    }
    catch {
        $last = $_
        Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
}
if ($null -ne $last) {
    throw "Could not replace publish\App. Last error: $($last.Exception.Message)"
}

if (!(Test-Path -LiteralPath $targetExe -PathType Leaf)) {
    throw "Deployed FocusLock.exe is missing."
}

$targetHash = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
if ($targetHash -ne $stageHash) {
    throw "Deployed FocusLock.exe hash does not match the new build."
}

$ver = (Get-Item -LiteralPath $targetExe).VersionInfo.FileVersion
if ($ver -notlike "7.5.2.2*") {
    throw "Wrong deployed UI version: $ver (expected 7.5.2.2)."
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK UI V7.5.2.2 INSTALLED" -ForegroundColor Green
Write-Host "StaticResource PrimaryBrush -> AccentBrush FIXED" -ForegroundColor Green
Write-Host "Version: $ver" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Write-Host ""

Start-Process $targetExe
exit 0
