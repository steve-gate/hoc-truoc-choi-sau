# FocusLock V7.7.9 OneDir source upgrade + safe OneDir build.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string]$Text) { Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }

$root = $PSScriptRoot
$payload = Join-Path $root 'payload'
$sln = Join-Path $root 'FocusLock.sln'
if (!(Test-Path -LiteralPath $sln -PathType Leaf)) {
    throw 'FocusLock.sln not found. Extract this V7.7.9 ZIP into the FocusLock source folder, then run again.'
}
if (!(Test-Path -LiteralPath $payload -PathType Container)) { throw 'payload folder is missing.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $root ".source-backups\V7.7.9-$stamp"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Write-Host "Source backup: $backupRoot" -ForegroundColor DarkGray

$changed = New-Object System.Collections.Generic.List[string]
try {
    Step 'Applying V7.7.9 OneDir source'
    $payloadFiles = Get-ChildItem -LiteralPath $payload -File -Recurse
    foreach ($file in $payloadFiles) {
        $relative = $file.FullName.Substring($payload.Length).TrimStart('\','/')
        $target = Join-Path $root $relative
        $backup = Join-Path $backupRoot $relative
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup -Force
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $changed.Add($relative)
    }

    Step 'Building FocusLock OneDir - current runtime is untouched'
    & (Join-Path $root 'BUILD_ONEDIR.ps1')
    if ($LASTEXITCODE -ne 0) { throw "OneDir build failed: $LASTEXITCODE" }

    $exe = Join-Path $root 'FocusLock-OneDir\FocusLock.exe'
    $svc = Join-Path $root 'FocusLock-OneDir\Service\FocusLock.Service.exe'
    $native = Join-Path $root 'FocusLock-OneDir\NativeHost\FocusLock.NativeHost.exe'
    foreach ($path in @($exe,$svc,$native)) {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build finished but output is missing: $path" }
    }

    Step 'V7.7.9 ONEDIR READY'
    Write-Host "EXE: $exe" -ForegroundColor Green
    Write-Host 'Lan dau mo FocusLock.exe, Windows se hoi Administrator 1 lan.' -ForegroundColor Yellow
    Write-Host 'Runtime publish cu khong bi thay doi.' -ForegroundColor DarkGray
    exit 0
}
catch {
    Write-Host ''
    Write-Host '[ROLLBACK] Build/source upgrade failed -> restoring previous source.' -ForegroundColor Yellow
    foreach ($relative in $changed) {
        $target = Join-Path $root $relative
        $backup = Join-Path $backupRoot $relative
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
        else {
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
