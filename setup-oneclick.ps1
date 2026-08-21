# FocusLock V5 One-Click bootstrapper
# Tu tai .NET SDK 10 vao .tools trong CHINH thu muc code, build va cai dat.
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$toolsRoot = Join-Path $root ".tools"
$dotnetRoot = Join-Path $toolsRoot "dotnet"
$dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
$installer = Join-Path $toolsRoot "dotnet-install.ps1"
$localTemp = Join-Path $toolsRoot "temp"
$nugetRoot = Join-Path $toolsRoot "nuget"
$cliHome = Join-Path $toolsRoot "dotnet-home"
$publishRoot = Join-Path $root "publish"

function Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Ensure-Dir([string]$path) {
    if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}

function Assert-File([string]$path, [string]$label) {
    if (!(Test-Path $path)) { throw "$label khong duoc tao: $path" }
}

Push-Location $root
try {
    foreach ($dir in @($toolsRoot, $localTemp, $nugetRoot, $cliHome)) { Ensure-Dir $dir }

    # Giu cache/build artifacts tren cung o dia voi source thay vi o C:.
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_CLI_HOME = $cliHome
    $env:NUGET_PACKAGES = $nugetRoot
    $env:TEMP = $localTemp
    $env:TMP = $localTemp
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:PATH = "$dotnetRoot;$env:PATH"

    if (!(Test-Path $dotnetExe)) {
        Step "May chua co SDK cuc bo - dang tai .NET SDK 10 vao .tools\dotnet"
        Write-Host "    Vi tri: $dotnetRoot" -ForegroundColor DarkGray
        Write-Host "    Khong cai SDK vao Program Files." -ForegroundColor DarkGray

        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        } catch { }

        try {
            Invoke-WebRequest -UseBasicParsing -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
        } catch {
            Write-Host "    Invoke-WebRequest loi, thu lai bang curl.exe..." -ForegroundColor Yellow
            $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
            if (!$curl) { throw "Khong tai duoc dotnet-install.ps1 va may khong co curl.exe." }
            & $curl.Source -L --fail --retry 3 "https://dot.net/v1/dotnet-install.ps1" -o $installer
            if ($LASTEXITCODE -ne 0) { throw "curl khong tai duoc dotnet-install.ps1." }
        }
        Assert-File $installer "dotnet-install.ps1"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Channel "10.0" -Architecture "x64" -InstallDir $dotnetRoot -NoPath
        if ($LASTEXITCODE -ne 0) { throw ".NET SDK download/install that bai (exit $LASTEXITCODE)." }
        Assert-File $dotnetExe ".NET SDK"
    } else {
        Step "Da co .NET SDK cuc bo - bo qua buoc tai"
    }

    Step "Kiem tra SDK"
    & $dotnetExe --info
    if ($LASTEXITCODE -ne 0) { throw "Khong chay duoc SDK cuc bo." }

    Step "Build FocusLock"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build-release.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Build FocusLock that bai." }

    Assert-File (Join-Path $publishRoot "App\FocusLock.exe") "FocusLock.exe"
    Assert-File (Join-Path $publishRoot "Service\FocusLock.Service.exe") "FocusLock.Service.exe"
    Assert-File (Join-Path $publishRoot "NativeHost\FocusLock.NativeHost.exe") "FocusLock.NativeHost.exe"

    Step "Cai Windows Service, startup va Browser Bridge"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $publishRoot "install-v5.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Installer V5 that bai." }

    Step "HOAN TAT"
    Write-Host "FocusLock.exe:  $publishRoot\App\FocusLock.exe" -ForegroundColor Green
    Write-Host "Data:           $publishRoot\Data" -ForegroundColor Green
    Write-Host "Extension:      $publishRoot\BrowserExtension" -ForegroundColor Green
    Write-Host ""
    Write-Host "Ban khong can chay build-release.ps1 hay install-v5.ps1 bang tay nua." -ForegroundColor Green

    # Giup buoc extension de hon: mo folder va browser extensions page neu tim thay.
    $extensionDir = Join-Path $publishRoot "BrowserExtension"
    try { Start-Process explorer.exe -ArgumentList "`"$extensionDir`"" | Out-Null } catch { }

    [array]$chromeCandidates = @(
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe" } else { $null }),
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe" } else { $null })
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($chromeCandidates.Count -gt 0) {
        try { Start-Process $chromeCandidates[0] -ArgumentList "chrome://extensions/" | Out-Null } catch { }
    } else {
        $edge = if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe" } else { $null }
        if ($edge -and (Test-Path $edge)) {
            try { Start-Process $edge -ArgumentList "edge://extensions/" | Out-Null } catch { }
        }
    }

    Write-Host ""
    Write-Host "Neu dung tinh nang Browser V5: trong trang Extensions bat Developer mode -> Load unpacked -> chon thu muc BrowserExtension vua mo." -ForegroundColor Yellow
    exit 0
}
catch {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "CAI DAT THAT BAI" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "Thu muc code: $root" -ForegroundColor DarkGray
    Write-Host "Neu loi mang khi tai SDK, kiem tra Internet/VPN/Firewall roi chay lai CAI_DAT.bat." -ForegroundColor Yellow
    exit 1
}
finally {
    Pop-Location
}
