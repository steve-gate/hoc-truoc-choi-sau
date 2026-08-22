$ErrorActionPreference = "Continue"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$out = Join-Path $root "web-diagnostic.txt"
Remove-Item $out -Force -ErrorAction SilentlyContinue

function W([string]$s = "") {
    $s | Tee-Object -FilePath $out -Append
}

function Get-Snapshot {
    $pipe = $null
    $reader = $null
    $writer = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', 'FocusLock.Guard.V5',
            [System.IO.Pipes.PipeDirection]::InOut
        )
        $pipe.Connect(1500)

        $reader = New-Object System.IO.StreamReader($pipe, [Text.Encoding]::UTF8, $true, 4096, $true)
        $writer = New-Object System.IO.StreamWriter($pipe, (New-Object Text.UTF8Encoding($false)), 4096, $true)
        $writer.AutoFlush = $true

        $id = [Guid]::NewGuid().ToString("N")
        $writer.WriteLine(('{"id":"' + $id + '","command":"snapshot"}'))
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) { return $null }
        return ($line | ConvertFrom-Json)
    }
    catch {
        return $null
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        if ($pipe) { $pipe.Dispose() }
    }
}

W "FOCUSLOCK WEB DIAGNOSTIC"
W ("Time: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
W ("Computer: " + $env:COMPUTERNAME)
W ""

W "=== INSTALLED RUNTIME ==="
$appExe = Join-Path $root "publish\App\FocusLock.exe"
if (Test-Path $appExe) {
    $vi = (Get-Item $appExe).VersionInfo
    W ("App FileVersion: " + $vi.FileVersion)
    W ("App SHA256: " + (Get-FileHash $appExe -Algorithm SHA256).Hash)
} else { W "App EXE: MISSING" }

$svc = Get-CimInstance Win32_Service -Filter "Name='FocusLockGuard'" -ErrorAction SilentlyContinue
if ($svc) {
    W ("Guard State: " + $svc.State)
    W ("Guard Path: " + $svc.PathName)
    W ("Guard PID: " + $svc.ProcessId)
} else { W "Guard: NOT FOUND" }

$pointer = Join-Path $root "publish\nativehost.current"
if (Test-Path $pointer) {
    $slot = (Get-Content $pointer -Raw).Trim()
    W ("NativeHost current slot: " + $slot)
}
$extManifest = Join-Path $root "BrowserExtension\manifest.json"
if (Test-Path $extManifest) {
    try {
        $m = Get-Content $extManifest -Raw | ConvertFrom-Json
        W ("Source BrowserExtension version: " + $m.version)
    } catch {}
}
W ""

W "=== LIVE TEST ==="
W "Trong 45 giay:"
W "  0-15s : mo WEBSITE HOC va click/cuon vai lan"
W " 15-30s : chuyen sang WEBSITE GIAI TRI"
W " 30-45s : giu nguyen website giai tri"
W ""
W "Columns:"
W "time | mode | app | hb | bridge | browserFG | host | category | rule | docVis | focusQ | ent | access | profile | wallet | focusProgress | idle"
W ""

Write-Host ""
Write-Host "BAT DAU CHAN DOAN 45 GIAY" -ForegroundColor Cyan
Write-Host "0-15s  : mo WEBSITE HOC + click/cuon" -ForegroundColor Yellow
Write-Host "15-30s : mo WEBSITE GIAI TRI" -ForegroundColor Yellow
Write-Host "30-45s : giu nguyen WEBSITE GIAI TRI" -ForegroundColor Yellow
Write-Host ""

for ($i = 0; $i -lt 45; $i++) {
    $r = Get-Snapshot
    $now = Get-Date -Format "HH:mm:ss"

    if ($null -eq $r -or $null -eq $r.snapshot) {
        $line = "$now | PIPE_FAIL"
        W $line
        Write-Host $line -ForegroundColor Red
    }
    else {
        $s = $r.snapshot
        $st = $s.state

        $wallet = if ($null -ne $st) { [int]$st.entertainmentBalanceSeconds } else { -1 }
        $focus = if ($null -ne $st) { [int]$st.focusProgressSeconds } else { -1 }

        $line = (
            "$now | mode={0} | app={1} | hb={2} | bridge={3} | browserFG={4} | host={5} | category={6} | rule={7} | docVis={8} | focusQ={9} | ent={10} | access={11} | profile={12} | wallet={13} | focusProgress={14} | idle={15}" -f
            $s.currentMode,
            $s.currentApp,
            $s.heartbeatHealthy,
            $s.browserBridgeHealthy,
            $s.browserForegroundActive,
            $s.currentBrowserHost,
            $s.currentBrowserCategory,
            $s.currentBrowserRule,
            $s.browserDocumentVisible,
            $s.browserFocusQualified,
            $s.entertainmentSessionActive,
            $s.entertainmentAccessMode,
            $s.entertainmentProfileName,
            $wallet,
            $focus,
            $s.isIdle
        )
        W $line

        $phase = if ($i -lt 15) { "WEB HOC" } elseif ($i -lt 30) { "WEB GIAI TRI" } else { "GIU WEB GIAI TRI" }
        Write-Host ("[{0,2}/45] {1,-15} host={2,-28} cat={3,-13} FG={4,-5} wallet={5} focus={6}" -f
            ($i+1), $phase, $s.currentBrowserHost, $s.currentBrowserCategory,
            $s.browserForegroundActive, $wallet, $focus)
    }
    Start-Sleep -Seconds 1
}

W ""
W "=== QUICK INTERPRETATION ==="

$lines = Get-Content $out
if ($lines -match "PIPE_FAIL") {
    W "RESULT: Co loi ket noi Guard/Named Pipe trong luc test."
}
if (-not ($lines -match "bridge=True")) {
    W "RESULT: Browser Bridge khong healthy trong ca bai test."
}
if (($lines -match "bridge=True") -and -not ($lines -match "browserFG=True")) {
    W "RESULT: Guard nhan Browser Bridge nhung khong xac minh Chrome/Edge foreground."
}
if (-not ($lines -match "category=Focus")) {
    W "RESULT: Guard khong nhan website hoc la category Focus."
}
if (-not ($lines -match "category=Entertainment")) {
    W "RESULT: Guard khong nhan website giai tri la category Entertainment."
}
if (($lines -match "category=Focus") -and -not ($lines -match "focusQ=True")) {
    W "RESULT: Website hoc duoc phan loai Focus nhung khong dat dieu kien cong thoi gian."
}
if (($lines -match "category=Entertainment") -and -not ($lines -match "ent=True")) {
    W "RESULT: Website giai tri duoc phan loai nhung EntertainmentSession khong bat."
}

W ""
W "DONE"
W ("Send this file: " + $out)

Write-Host ""
Write-Host "XONG." -ForegroundColor Green
Write-Host "Gui cho toi file:" -ForegroundColor Yellow
Write-Host $out -ForegroundColor Yellow
