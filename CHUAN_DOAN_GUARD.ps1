$ErrorActionPreference = "Continue"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "guard-diagnostic.txt"
$serviceName = "FocusLockGuard"
$expectedExe = Join-Path $root "publish\Service\FocusLock.Service.exe"
$pipeName = "FocusLock.Guard.V5"

function W([string]$s = "") {
    $s | Tee-Object -FilePath $out -Append
}

function Test-Pipe([int]$timeout = 1200) {
    $p = $null
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut
        )
        $p.Connect($timeout)
        if ($p.IsConnected) { return "OK" }
        return "NOT_CONNECTED"
    }
    catch [System.UnauthorizedAccessException] { return "ACCESS_DENIED: " + $_.Exception.Message }
    catch [System.TimeoutException] { return "TIMEOUT" }
    catch { return "ERROR: " + $_.Exception.GetType().FullName + " :: " + $_.Exception.Message }
    finally { if ($null -ne $p) { $p.Dispose() } }
}

Remove-Item $out -Force -ErrorAction SilentlyContinue
W "FOCUSLOCK GUARD DIAGNOSTIC"
W ("Time: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
W ("User: " + [Environment]::UserName)
W ("Root: " + $root)
W ""

W "=== 1. SERVICE CONFIGURATION ==="
$svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    W "SERVICE_NOT_FOUND"
} else {
    W ("Name       : " + $svc.Name)
    W ("State      : " + $svc.State)
    W ("StartMode  : " + $svc.StartMode)
    W ("StartName  : " + $svc.StartName)
    W ("ProcessId  : " + $svc.ProcessId)
    W ("PathName   : " + $svc.PathName)
}
W ("ExpectedExe: " + $expectedExe)
W ("Expected exists: " + (Test-Path $expectedExe))
if (Test-Path $expectedExe) {
    try {
        $fi = Get-Item $expectedExe
        W ("Expected size: " + $fi.Length)
        W ("Expected modified: " + $fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))
        W ("Expected SHA256: " + (Get-FileHash $expectedExe -Algorithm SHA256).Hash)
    } catch { W ("Expected file inspect error: " + $_.Exception.Message) }
}
W ""

W "=== 2. CURRENT PIPE TEST ==="
$currentPipe = Test-Pipe 1500
W ("Pipe test while service is running: " + $currentPipe)
W ""

W "=== 3. PIPE / SERVICE LOG ==="
$pipeLog = Join-Path $root "publish\Logs\service-pipe.log"
W ("service-pipe.log path: " + $pipeLog)
W ("service-pipe.log exists: " + (Test-Path $pipeLog))
if (Test-Path $pipeLog) {
    W "--- tail service-pipe.log ---"
    Get-Content $pipeLog -Tail 80 | ForEach-Object { W $_ }
}
W ""

W "=== 4. WINDOWS APPLICATION EVENT LOG ==="
try {
    $events = Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-4)} -ErrorAction Stop |
        Where-Object {
            $_.ProviderName -match 'FocusLock|\.NET Runtime|Application Error' -or
            $_.Message -match 'FocusLock'
        } |
        Select-Object -First 30
    if ($events.Count -eq 0) {
        W "No matching events in last 4 hours."
    } else {
        foreach ($e in $events) {
            W ("[" + $e.TimeCreated.ToString("yyyy-MM-dd HH:mm:ss") + "] " + $e.ProviderName + " ID=" + $e.Id)
            W (($e.Message -replace "`r","") -replace "`n"," | ")
            W ""
        }
    }
} catch {
    W ("Event log read failed: " + $_.Exception.Message)
}
W ""

W "=== 5. INTERACTIVE SERVICE BINARY TEST ==="
W "Temporarily stopping Windows service..."
try { Stop-Service $serviceName -Force -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 2

$consoleOut = Join-Path $root "service-console.stdout.txt"
$consoleErr = Join-Path $root "service-console.stderr.txt"
Remove-Item $consoleOut,$consoleErr -Force -ErrorAction SilentlyContinue

$proc = $null
$oldHome = $env:FOCUSLOCK_HOME
try {
    if (-not (Test-Path $expectedExe)) {
        W "Cannot run interactive test: expected service EXE does not exist."
    } else {
        $env:FOCUSLOCK_HOME = Join-Path $root "publish"
        W ("Launching interactively: " + $expectedExe)
        $proc = Start-Process -FilePath $expectedExe -WorkingDirectory (Split-Path $expectedExe -Parent) `
            -PassThru -RedirectStandardOutput $consoleOut -RedirectStandardError $consoleErr

        $interactiveResult = "TIMEOUT"
        for ($i=0; $i -lt 12; $i++) {
            Start-Sleep -Milliseconds 500
            if ($proc.HasExited) {
                W ("Interactive process EXITED EARLY with code: " + $proc.ExitCode)
                break
            }
            $interactiveResult = Test-Pipe 800
            if ($interactiveResult -eq "OK") { break }
        }
        W ("Pipe test in interactive mode: " + $interactiveResult)

        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500

        if (Test-Path $consoleOut) {
            W "--- interactive stdout ---"
            Get-Content $consoleOut -Tail 100 | ForEach-Object { W $_ }
        }
        if (Test-Path $consoleErr) {
            W "--- interactive stderr ---"
            Get-Content $consoleErr -Tail 100 | ForEach-Object { W $_ }
        }
    }
} catch {
    W ("Interactive test exception: " + $_.Exception.ToString())
} finally {
    $env:FOCUSLOCK_HOME = $oldHome
}
W ""

W "=== 6. RESTART WINDOWS SERVICE ==="
try {
    Start-Service $serviceName -ErrorAction Stop
    Start-Sleep -Seconds 2
    $svc2 = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    W ("After restart State: " + $svc2.State)
    W ("After restart PID: " + $svc2.ProcessId)
    W ("After restart PathName: " + $svc2.PathName)
    W ("Pipe after restart: " + (Test-Pipe 1500))
} catch {
    W ("Restart service failed: " + $_.Exception.Message)
}

W ""
W "=== DIAGNOSIS HINT ==="
W "If INTERACTIVE pipe = OK but Windows-service pipe = TIMEOUT => service context/path/permission/startup problem."
W "If INTERACTIVE pipe = TIMEOUT too => service code is not reaching the pipe listener; stdout/stderr/Event Log should show why."
W "If PathName differs from ExpectedExe => Windows is running an old service binary."

Write-Host ""
Write-Host "DONE." -ForegroundColor Green
Write-Host "Send this file:" -ForegroundColor Yellow
Write-Host $out -ForegroundColor Yellow
