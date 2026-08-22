$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) { throw "Open CMD as Administrator, then run NANG_CAP_V7_5_5.bat." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Join-Path $root ".tools\dotnet\dotnet.exe"
if (!(Test-Path $dotnet)) { $dotnet = "dotnet" }

$stage = Join-Path $root ".build-v755\App"
$target = Join-Path $root "publish\App"
$stageExe = Join-Path $stage "FocusLock.exe"
$targetExe = Join-Path $target "FocusLock.exe"

Write-Host ""
Write-Host "==> FocusLock V7.5.5 - rebuilding ONLY desktop UI/agent" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $stage

if ($LASTEXITCODE -ne 0) { throw "FocusLock.App build failed: $LASTEXITCODE" }
if (!(Test-Path $stageExe)) { throw "Staged FocusLock.exe is missing." }

$stageHash = (Get-FileHash $stageExe -Algorithm SHA256).Hash

Write-Host "==> Stopping old UI/tray agent" -ForegroundColor Cyan
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

for ($i=0; $i -lt 20; $i++) {
    if (-not (Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Milliseconds 500

Write-Host "==> Replacing publish\App" -ForegroundColor Cyan
$last = $null
for ($i=0; $i -lt 20; $i++) {
    try {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item (Join-Path $stage "*") $target -Recurse -Force -ErrorAction Stop
        $last = $null
        break
    } catch {
        $last = $_
        Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
}
if ($null -ne $last) { throw "Could not replace publish\App: $($last.Exception.Message)" }

if ((Get-FileHash $targetExe -Algorithm SHA256).Hash -ne $stageHash) {
    throw "Deployed FocusLock.exe hash mismatch."
}
$ver = (Get-Item $targetExe).VersionInfo.FileVersion
if ($ver -notlike "7.5.5*") { throw "Wrong deployed version: $ver" }

Write-Host "==> Opening FocusLock and waiting for desktop heartbeat" -ForegroundColor Cyan
Start-Process $targetExe

function Read-Snapshot {
    $p=$null; $r=$null; $w=$null
    try {
        $p=New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect(1200)
        $r=New-Object System.IO.StreamReader($p,[Text.Encoding]::UTF8,$true,4096,$true)
        $w=New-Object System.IO.StreamWriter($p,(New-Object Text.UTF8Encoding($false)),4096,$true)
        $w.AutoFlush=$true
        $w.WriteLine('{"id":"verify","command":"snapshot"}')
        $line=$r.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { return $null }
        return ($line | ConvertFrom-Json)
    } catch { return $null }
    finally {
        if($r){$r.Dispose()}; if($w){$w.Dispose()}; if($p){$p.Dispose()}
    }
}

$healthy=$false
for($i=0;$i -lt 20;$i++){
    Start-Sleep -Milliseconds 500
    $snap=Read-Snapshot
    if($snap -and $snap.snapshot -and $snap.snapshot.heartbeatHealthy){
        $healthy=$true
        break
    }
}

if(-not $healthy){
    throw "V7.5.5 UI is installed, but desktop heartbeat did not become healthy within 10 seconds."
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.5.5 INSTALLED" -ForegroundColor Green
Write-Host "Desktop heartbeat: HEALTHY" -ForegroundColor Green
Write-Host "Version: $ver" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
