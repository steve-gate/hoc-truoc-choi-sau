#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$runtimeRoot = Join-Path $root 'FocusLock-OneDir-V7.8.0.2'
$serviceExe = Join-Path $runtimeRoot 'Service\FocusLock.Service.exe'
$serviceName = 'FocusLockGuard'
$codeDataRoot = Join-Path $root 'FocusLock-Data'
$codeDataDir = Join-Path $codeDataRoot 'Data'
$serviceRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safetyRoot = Join-Path $root ("FocusLock-Persistence-Safety-" + $stamp)
$logPath = Join-Path $root 'PERSISTENCE-V2.2.2-CODE-FOLDER-ONLY.log'
$reportPath = Join-Path $root 'PERSISTENCE-MIGRATION-REPORT-V2.2.2.txt'

function Log([string]$text) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $text"
    Write-Host $text
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}
function Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
    Log $text
}
function Ensure-Dir([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}
function Copy-Tree([string]$from,[string]$to) {
    if (!(Test-Path -LiteralPath $from -PathType Container)) { return }
    $a = [IO.Path]::GetFullPath($from).TrimEnd('\')
    $b = [IO.Path]::GetFullPath($to).TrimEnd('\')
    if ($a -ieq $b) { return }
    Ensure-Dir $to
    & robocopy.exe $from $to /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) { throw "Robocopy failed ($code): $from -> $to" }
}
function Copy-FileIfDifferent([string]$from,[string]$to) {
    if (!(Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing source file: $from" }
    $a = [IO.Path]::GetFullPath($from)
    $b = [IO.Path]::GetFullPath($to)
    if ($a -ieq $b) {
        Log "SKIP self-copy: $a"
        return
    }
    $parent = Split-Path -Parent $b
    if ($parent) { Ensure-Dir $parent }
    Copy-Item -LiteralPath $a -Destination $b -Force
}
function Bytes-ToHex([byte[]]$bytes) { return ([BitConverter]::ToString($bytes)).Replace('-','') }
function Get-Count($value) { if ($null -eq $value) { return 0 }; return @($value).Count }
function Test-GuardPipe([int]$timeoutMs = 1200) {
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($timeoutMs)
        return $pipe.IsConnected
    } catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}
function Invoke-GuardCreateBackupProof([string]$filePath) {
    $pipeClient = $null
    $writer = $null
    $reader = $null
    try {
        $pipeClient = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $pipeClient.Connect(3000)
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        $writer = [System.IO.StreamWriter]::new($pipeClient, $utf8NoBom, 4096, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($pipeClient, [Text.Encoding]::UTF8, $true, 4096, $true)
        $request = [ordered]@{
            id = [Guid]::NewGuid().ToString('N')
            command = 'createbackup'
            filePath = $filePath
        }
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $readTask = $reader.ReadLineAsync()
        if (!$readTask.Wait(10000)) { throw 'Guard proof request timed out.' }
        $line = $readTask.Result
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'Guard returned an empty proof response.' }
        $response = $line | ConvertFrom-Json
        if ($null -eq $response -or -not [bool]$response.ok) {
            $message = if ($null -ne $response) { [string]$response.message } else { 'Invalid response' }
            throw ("Guard proof request failed: " + $message)
        }
        return [string]$response.message
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $pipeClient) { $pipeClient.Dispose() }
    }
}
function Stop-GuardCompletely {
    try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
    for ($i=0; $i -lt 30; $i++) {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        $guardProcessId = if ($svc) { [int]$svc.ProcessId } else { 0 }
        if ($guardProcessId -le 0) { return }
        Start-Sleep -Milliseconds 300
    }
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    $guardProcessId = if ($svc) { [int]$svc.ProcessId } else { 0 }
    if ($guardProcessId -gt 0) {
        Stop-Process -Id $guardProcessId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}
function Get-StateCandidate([string]$statePath,[string]$secretPath) {
    try {
        if (!(Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
        if (!(Test-Path -LiteralPath $secretPath -PathType Leaf)) { return $null }
        $secret = [IO.File]::ReadAllBytes($secretPath)
        if ($secret.Length -lt 32) { return $null }
        $raw = [IO.File]::ReadAllText($statePath, [Text.Encoding]::UTF8)
        $envObj = $raw | ConvertFrom-Json
        if ($null -eq $envObj -or [string]::IsNullOrWhiteSpace([string]$envObj.Payload) -or [string]::IsNullOrWhiteSpace([string]$envObj.Hmac)) { return $null }
        $payload = [Convert]::FromBase64String([string]$envObj.Payload)
        $hmac = New-Object System.Security.Cryptography.HMACSHA256
        try { $hmac.Key = $secret; $actualHex = Bytes-ToHex ($hmac.ComputeHash($payload)) } finally { $hmac.Dispose() }
        if ($actualHex -ine ([string]$envObj.Hmac).Trim()) { return $null }
        $state = ([Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json)
        $rules = @($state.BrowserRules)
        $patterns = @($rules | ForEach-Object { ([string]$_.Pattern).Trim().ToLowerInvariant() } | Where-Object { $_ } | Sort-Object -Unique)
        $sample = @('coursera.org','docs.google.com','facebook.com','khanacademy.org','netflix.com','tiktok.com','youtube.com') | Sort-Object
        $isExactSample = ($patterns.Count -eq $sample.Count -and (($patterns -join '|') -eq ($sample -join '|')))
        $ruleScore = if ($isExactSample) { 0 } else { $rules.Count * 10000 }
        $apps = Get-Count $state.Apps
        $keys = Get-Count $state.Keys
        $audit = Get-Count $state.AuditLog
        $sessions = Get-Count $state.SessionHistory
        $profiles = Get-Count $state.BlockProfiles
        $extraProfiles = [Math]::Max(0, $profiles - 1)
        $totalFocus = 0L; $totalPlay = 0L
        try { $totalFocus = [long]$state.TotalFocusSeconds } catch { }
        try { $totalPlay = [long]$state.TotalEntertainmentSeconds } catch { }
        $usageScore = [Math]::Min(5000, [int](($totalFocus + $totalPlay) / 60))
        $onboard = 0
        try { if ([bool]$state.Settings.OnboardingCompleted) { $onboard = 500 } } catch { }
        $score = $ruleScore + ($apps * 10000) + ($keys * 3000) + ($extraProfiles * 3000) + ([Math]::Min($audit,200) * 100) + ([Math]::Min($sessions,200) * 20) + $usageScore + $onboard
        return [pscustomobject]@{
            StatePath=$statePath; SecretPath=$secretPath; DataDir=(Split-Path -Parent $statePath)
            Score=[int]$score; Rules=$rules.Count; Apps=$apps; Profiles=$profiles; Audit=$audit
            IsExactSample=$isExactSample; ModifiedUtc=(Get-Item -LiteralPath $statePath).LastWriteTimeUtc
        }
    } catch { return $null }
}
function Add-CandidateDir([System.Collections.Generic.List[string]]$list,[string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return }
    try { $full=[IO.Path]::GetFullPath($path).TrimEnd('\') } catch { return }
    if (!(Test-Path -LiteralPath $full -PathType Container)) { return }
    if (!$list.Contains($full)) { $list.Add($full) }
}
function Discover-CandidateDirs {
    $dirs = New-Object 'System.Collections.Generic.List[string]'
    Add-CandidateDir $dirs $codeDataDir
    Add-CandidateDir $dirs (Join-Path $runtimeRoot 'Data')
    Add-CandidateDir $dirs (Join-Path $root 'publish\Data')
    Add-CandidateDir $dirs (Join-Path $root '.onedir-data-preserve')


    Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like 'FocusLock-OneDir*' -or $_.Name -like 'FocusLock-*-Safety-*' -or $_.Name -like 'FocusLock-Restore-Safety-*' -or $_.Name -like 'FocusLock-TwoFix-Backup-*' -or $_.Name -like 'FocusLock-Persistence-Safety-*'
    } | ForEach-Object {
        $base = $_.FullName
        foreach ($name in @('Data','OneDir-Data')) { Add-CandidateDir $dirs (Join-Path $base $name) }
        try {
            Get-ChildItem -LiteralPath $base -Recurse -File -Filter 'state.v2.json' -ErrorAction SilentlyContinue | ForEach-Object {
                Add-CandidateDir $dirs $_.DirectoryName
            }
        } catch { }
    }
    return $dirs
}
function Get-ServiceEnvironment {
    try {
        $v = (Get-ItemProperty -LiteralPath $serviceRegPath -Name Environment -ErrorAction Stop).Environment
        if ($null -eq $v) { return @() }
        return @($v)
    } catch { return @() }
}
function Set-ServiceFocusLockHome([string]$focusRoot) {
    $entries = @(Get-ServiceEnvironment | Where-Object { $_ -notmatch '^(?i)FOCUSLOCK_HOME=' })
    $entries += "FOCUSLOCK_HOME=$focusRoot"
    New-ItemProperty -LiteralPath $serviceRegPath -Name Environment -PropertyType MultiString -Value $entries -Force | Out-Null
}
function Remove-ServiceFocusLockHome {
    $entries = @(Get-ServiceEnvironment | Where-Object { $_ -notmatch '^(?i)FOCUSLOCK_HOME=' })
    if ($entries.Count -gt 0) {
        New-ItemProperty -LiteralPath $serviceRegPath -Name Environment -PropertyType MultiString -Value $entries -Force | Out-Null
    } else {
        Remove-ItemProperty -LiteralPath $serviceRegPath -Name Environment -ErrorAction SilentlyContinue
    }
}
function Validate-LiveState([string]$dir) {
    return Get-StateCandidate (Join-Path $dir 'state.v2.json') (Join-Path $dir 'guard.secret')
}

Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue
$changedConfig = $false

try {
    Step 'Validate existing V7.8.0.2 Service - NO binary replacement'
    if (!(Test-Path -LiteralPath $serviceExe -PathType Leaf)) { throw "Missing stable Service: $serviceExe" }
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if (!$svc) { throw 'FocusLockGuard service is missing.' }

    Step 'Stop Guard so the latest state is flushed once'
    Stop-GuardCompletely

    Step 'Find the best cryptographically VALID state + secret pair'
    $dirs = Discover-CandidateDirs
    $candidates = @()
    foreach ($dir in $dirs) {
        foreach ($stateName in @('state.v2.json','state.v2.bak')) {
            foreach ($secretName in @('guard.secret','guard.secret.bak')) {
                $candidate = Get-StateCandidate (Join-Path $dir $stateName) (Join-Path $dir $secretName)
                if ($null -ne $candidate) { $candidates += $candidate }
            }
        }
    }
    if ($candidates.Count -eq 0) { throw 'No valid state + guard.secret pair was found. Nothing was changed.' }
    $ordered = $candidates | Sort-Object @{Expression='Score';Descending=$true}, @{Expression='ModifiedUtc';Descending=$true}
    $selected = $ordered | Select-Object -First 1

    $report = New-Object System.Collections.Generic.List[string]
    $report.Add('FocusLock Persistence V2.2.2 CODE-FOLDER-ONLY migration report')
    $report.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $report.Add("Persistent root: $codeDataRoot")
    $report.Add('')
    foreach ($c in $ordered) {
        $report.Add(("Score={0} rules={1} apps={2} profiles={3} audit={4} sample={5} modified={6:o}`r`n  state={7}`r`n  secret={8}" -f $c.Score,$c.Rules,$c.Apps,$c.Profiles,$c.Audit,$c.IsExactSample,$c.ModifiedUtc,$c.StatePath,$c.SecretPath))
    }
    $report.Add('')
    $report.Add("SELECTED STATE : $($selected.StatePath)")
    $report.Add("SELECTED SECRET: $($selected.SecretPath)")
    $report.Add("SELECTED SCORE : $($selected.Score)")
    $report | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Selected state: $($selected.StatePath)" -ForegroundColor Green
    Write-Host "Selected score: $($selected.Score)" -ForegroundColor Green

    Step 'Create safety backup INSIDE code folder'
    Ensure-Dir $safetyRoot
    Copy-Tree (Join-Path $runtimeRoot 'Data') (Join-Path $safetyRoot 'OneDir-Data')
    Copy-Tree $codeDataDir (Join-Path $safetyRoot 'CodeData-Before')
    Copy-FileIfDifferent $selected.StatePath (Join-Path $safetyRoot 'selected-state.v2.json')
    Copy-FileIfDifferent $selected.SecretPath (Join-Path $safetyRoot 'selected-guard.secret')

    Step 'Prepare persistent storage INSIDE code folder only'
    Ensure-Dir $codeDataRoot
    Ensure-Dir $codeDataDir
    foreach ($f in @('state.v2.json','state.v2.bak','guard.secret','guard.secret.bak')) {
        $p = Join-Path $codeDataDir $f
        try { if (Test-Path -LiteralPath $p) { [IO.File]::SetAttributes($p,[IO.FileAttributes]::Normal) } } catch { }
    }
    Copy-FileIfDifferent $selected.StatePath (Join-Path $codeDataDir 'state.v2.json')
    Copy-FileIfDifferent $selected.StatePath (Join-Path $codeDataDir 'state.v2.bak')
    Copy-FileIfDifferent $selected.SecretPath (Join-Path $codeDataDir 'guard.secret')
    Copy-FileIfDifferent $selected.SecretPath (Join-Path $codeDataDir 'guard.secret.bak')
    try { [IO.File]::SetAttributes((Join-Path $codeDataDir 'guard.secret'), [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System) } catch { }

    $preStartValidation = Validate-LiveState $codeDataDir
    if ($null -eq $preStartValidation) { throw 'Code-folder state failed HMAC validation before Guard start.' }

    Step 'Pin FocusLock data root to the code folder on drive D'
    Set-ServiceFocusLockHome $codeDataRoot
    # Remove any machine-wide override left by older failed experiments. The Guard uses only its service-specific D-drive value.
    [Environment]::SetEnvironmentVariable('FOCUSLOCK_HOME',$null,'Machine')
    $changedConfig = $true

    Step 'Start unchanged Guard Service and verify Named Pipe'
    Start-Service -Name $serviceName -ErrorAction Stop
    $pipeOk = $false
    for ($i=0; $i -lt 20; $i++) {
        if (Test-GuardPipe 1000) { $pipeOk = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$pipeOk) { throw 'Guard did not expose Named Pipe after code-folder persistence configuration.' }

    Step 'Force one Guard save through Named Pipe and prove the D-drive state changes'
    $statePath = Join-Path $codeDataDir 'state.v2.json'
    $proofDir = Join-Path $codeDataRoot 'Proof'
    Ensure-Dir $proofDir
    $proofFile = Join-Path $proofDir ("persistence-proof-" + $stamp + '.focuslockbackup')
    Remove-Item -LiteralPath $proofFile -Force -ErrorAction SilentlyContinue
    $beforeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $statePath).Hash
    $beforeWrite = (Get-Item -LiteralPath $statePath).LastWriteTimeUtc
    $proofMessage = Invoke-GuardCreateBackupProof $proofFile
    if (!(Test-Path -LiteralPath $proofFile -PathType Leaf)) { throw 'Guard reported backup success, but the proof backup was not created.' }
    $afterValidation = Validate-LiveState $codeDataDir
    if ($null -eq $afterValidation) { throw 'Guard wrote an INVALID D-drive code-folder state.' }
    $afterHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $statePath).Hash
    $afterWrite = (Get-Item -LiteralPath $statePath).LastWriteTimeUtc
    if ($afterHash -eq $beforeHash) { throw 'Guard proof command succeeded, but the D-drive state content did not change.' }
    Remove-Item -LiteralPath $proofFile -Force -ErrorAction SilentlyContinue

    Write-Host ''
    Write-Host 'PERSISTENCE V2.2.2 CODE-FOLDER-ONLY APPLIED SUCCESSFULLY' -ForegroundColor Green
    Write-Host 'Service binaries: UNTOUCHED' -ForegroundColor Green
    Write-Host "Persistent root:  $codeDataRoot" -ForegroundColor Green
    Write-Host "Data:             $codeDataDir" -ForegroundColor Green
    Write-Host "Proof save UTC:   $afterWrite" -ForegroundColor Green
    Write-Host "Proof response:   $proofMessage" -ForegroundColor Green
    Write-Host 'No FocusLock state/secret is configured to live on C:.' -ForegroundColor Green
    Write-Host "Safety backup:    $safetyRoot" -ForegroundColor Yellow
    Write-Host "Report:           $reportPath" -ForegroundColor Yellow
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'PERSISTENCE V2.2.2 CODE-FOLDER-ONLY FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Log ("ERROR: " + $_.Exception.ToString())
    if ($changedConfig) {
        try {
            Stop-GuardCompletely
            Remove-ServiceFocusLockHome
            [Environment]::SetEnvironmentVariable('FOCUSLOCK_HOME',$null,'Machine')
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            Write-Host 'FOCUSLOCK_HOME override was removed. No data target outside the code folder was configured.' -ForegroundColor Yellow
        } catch { }
    } else {
        try { Start-Service -Name $serviceName -ErrorAction SilentlyContinue } catch { }
    }
    Write-Host 'NO Service DLL/EXE was moved, deleted, or overwritten.' -ForegroundColor Yellow
    Write-Host 'NO source Data directory was deleted.' -ForegroundColor Yellow
    Write-Host "Log: $logPath" -ForegroundColor Yellow
    exit 1
}
