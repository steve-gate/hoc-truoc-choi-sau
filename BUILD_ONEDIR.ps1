# Build FocusLock V7.7.9 as a Windows x64 self-contained OneDir folder.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string]$Text) { Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }
function Ensure-Dir([string]$Path) { if (!(Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null } }
function Assert-File([string]$Path,[string]$Label) { if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label missing: $Path" } }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (!(Test-Path -LiteralPath (Join-Path $root 'FocusLock.sln') -PathType Leaf)) { throw 'FocusLock.sln not found next to BUILD_ONEDIR.ps1.' }

$toolsRoot = Join-Path $root '.tools'
$dotnetRoot = Join-Path $toolsRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
$dotnetInstall = Join-Path $toolsRoot 'dotnet-install.ps1'
$stage = Join-Path $root '.build-onedir-v779'
$dist = Join-Path $root 'FocusLock-OneDir'
$newDist = Join-Path $root '.FocusLock-OneDir.new'
$preserve = Join-Path $root '.onedir-data-preserve'

try {
    Step 'Preparing local .NET SDK'
    Ensure-Dir $toolsRoot
    if (!(Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
        if (!(Test-Path -LiteralPath $dotnetInstall -PathType Leaf)) {
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstall -UseBasicParsing
        }
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $dotnetInstall -Channel '10.0' -Architecture 'x64' -InstallDir $dotnetRoot -NoPath
        if ($LASTEXITCODE -ne 0) { throw "dotnet-install failed: $LASTEXITCODE" }
    }
    Assert-File $dotnetExe '.NET SDK'
    & $dotnetExe --version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Local dotnet SDK cannot run.' }

    Step 'Preserving existing OneDir / publish Data'
    Remove-Item -LiteralPath $preserve -Recurse -Force -ErrorAction SilentlyContinue
    $currentData = Join-Path $dist 'Data'
    $legacyData = Join-Path $root 'publish\Data'
    if (Test-Path -LiteralPath $currentData -PathType Container) {
        Copy-Item -LiteralPath $currentData -Destination $preserve -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $legacyData -PathType Container) {
        Copy-Item -LiteralPath $legacyData -Destination $preserve -Recurse -Force
    }

    Step 'Publishing App - OneDir root EXE'
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $stage
    $appStage = Join-Path $stage 'AppRoot'
    $svcStage = Join-Path $stage 'Service'
    $nativeStage = Join-Path $stage 'NativeHost'

    & $dotnetExe publish (Join-Path $root 'FocusLock.App\FocusLock.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $appStage
    if ($LASTEXITCODE -ne 0) { throw "App publish failed: $LASTEXITCODE" }

    Step 'Publishing Guard Service'
    & $dotnetExe publish (Join-Path $root 'FocusLock.Service\FocusLock.Service.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $svcStage
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed: $LASTEXITCODE" }

    Step 'Publishing Native Host'
    & $dotnetExe publish (Join-Path $root 'FocusLock.NativeHost\FocusLock.NativeHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $nativeStage
    if ($LASTEXITCODE -ne 0) { throw "NativeHost publish failed: $LASTEXITCODE" }

    Assert-File (Join-Path $appStage 'FocusLock.exe') 'FocusLock.exe'
    Assert-File (Join-Path $svcStage 'FocusLock.Service.exe') 'FocusLock.Service.exe'
    Assert-File (Join-Path $nativeStage 'FocusLock.NativeHost.exe') 'FocusLock.NativeHost.exe'

    Step 'Assembling final OneDir folder'
    Remove-Item -LiteralPath $newDist -Recurse -Force -ErrorAction SilentlyContinue
    Ensure-Dir $newDist
    Copy-Item -Path (Join-Path $appStage '*') -Destination $newDist -Recurse -Force
    Copy-Item -LiteralPath $svcStage -Destination (Join-Path $newDist 'Service') -Recurse -Force
    Copy-Item -LiteralPath $nativeStage -Destination (Join-Path $newDist 'NativeHost') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'BrowserExtension') -Destination (Join-Path $newDist 'BrowserExtension') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'Install-OneDir.ps1') -Destination (Join-Path $newDist 'Install-OneDir.ps1') -Force
    Ensure-Dir (Join-Path $newDist 'Data')
    Ensure-Dir (Join-Path $newDist 'Logs')
    if (Test-Path -LiteralPath $preserve -PathType Container) {
        Copy-Item -Path (Join-Path $preserve '*') -Destination (Join-Path $newDist 'Data') -Recurse -Force -ErrorAction SilentlyContinue
    }

    @'
FocusLock V7.7.9 - OneDir Win-x64

CHAY PHAN MEM:
  Double-click FocusLock.exe

LAN DAU:
  FocusLock.exe se yeu cau quyen Administrator 1 lan de dang ky Guard Service va Browser Bridge.
  Sau khi dang ky thanh cong, cac lan sau chi can chay FocusLock.exe binh thuong.

THU MUC:
  FocusLock.exe       - ung dung chinh
  Service\             - Guard Service
  NativeHost\          - Browser Native Messaging Host
  BrowserExtension\    - Extension Chrome/Edge
  Data\                - du lieu + backup
  Logs\                - log chan doan
  Install-OneDir.ps1  - script noi bo, FocusLock.exe tu goi khi can

KHONG nen tach rieng FocusLock.exe khoi thu muc nay.
Neu di chuyen FocusLock sang thu muc/o dia khac, chay FocusLock.exe tai vi tri moi; no se tu cap nhat Service path.
'@ | Set-Content -LiteralPath (Join-Path $newDist 'README-ONEDIR.txt') -Encoding UTF8

    if (Test-Path -LiteralPath $dist) {
        $old = Join-Path $root ('.FocusLock-OneDir.old-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath $dist -Destination $old -Force
    }
    Move-Item -LiteralPath $newDist -Destination $dist -Force

    $zip = Join-Path $root 'FocusLock-V7.7.9-ONEDIR-WIN-X64.zip'
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal

    Step 'DONE'
    Write-Host "OneDir: $dist" -ForegroundColor Green
    Write-Host "EXE:    $(Join-Path $dist 'FocusLock.exe')" -ForegroundColor Green
    Write-Host "ZIP:    $zip" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Tu bay gio, chi can mo FocusLock-OneDir\FocusLock.exe.' -ForegroundColor Yellow
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'BUILD ONEDIR FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item -LiteralPath $newDist -Recurse -Force -ErrorAction SilentlyContinue
}
