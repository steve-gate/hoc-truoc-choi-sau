# FocusLock V7.8.0.2 FINAL FIX 3 installer
# Root cause fix: UTF-8 BOM on state envelope + safe HMAC persistence.
# ASCII-only / Windows PowerShell 5.1 compatible.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Find-CodeRoot {
    $cursor = Get-Item -LiteralPath $PSScriptRoot
    for ($depth = 0; $depth -lt 10 -and $null -ne $cursor; $depth++) {
        if (Test-Path -LiteralPath (Join-Path $cursor.FullName 'FocusLock.sln') -PathType Leaf) { return $cursor.FullName }
        $cursor = $cursor.Parent
    }
    throw 'FocusLock.sln was not found above the patch folder.'
}

$codeRoot = Find-CodeRoot
$payloadRoot = Join-Path $PSScriptRoot '_FINAL_FIX3_PAYLOAD'
$timeTag = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $codeRoot 'FINAL-FIX3-INSTALL.log'
$sourceBackup = Join-Path $codeRoot ('.source-backups\FINAL-FIX3-' + $timeTag)
$dataRoot = Join-Path $codeRoot 'FocusLock-Data'
$dataDir = Join-Path $dataRoot 'Data'
$recoveryDir = Join-Path $codeRoot ('FocusLock-Recovery\FINAL-FIX3-' + $timeTag)
$stageRoot = Join-Path $codeRoot ('.build-final-fix3-' + $timeTag)
$runtimeBase = Join-Path $codeRoot 'FocusLock-OneDir-V7.8.0.2-FINAL-FIX3'
$runtimeRoot = $runtimeBase
if (Test-Path -LiteralPath $runtimeRoot) { $runtimeRoot = $runtimeBase + '-' + $timeTag }
$serviceName = 'FocusLockGuard'
$pipeName = 'FocusLock.Guard.V5'
$buildSucceeded = $false
$serviceWasPresent = $false

function Log([string]$text) {
    $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $text
    Write-Host $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}
function Step([string]$text) { Write-Host ''; Write-Host ('==> ' + $text) -ForegroundColor Cyan; Log $text }
function Ensure-Dir([string]$path) { if (!(Test-Path -LiteralPath $path -PathType Container)) { New-Item -ItemType Directory -Path $path -Force | Out-Null } }
function Copy-DirContent([string]$source,[string]$destination) { Ensure-Dir $destination; Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force } }
function Bytes-ToHex([byte[]]$bytes) { $b = New-Object System.Text.StringBuilder ($bytes.Length*2); foreach($v in $bytes){[void]$b.Append($v.ToString('X2'))}; $b.ToString() }
function Count-Items($value) { if ($null -eq $value) { return 0 }; try { return @($value).Count } catch { return 0 } }
function Has-Utf8Bom([string]$path) {
    try { $b=[System.IO.File]::ReadAllBytes($path); return ($b.Length -ge 3 -and $b[0]-eq 0xEF -and $b[1]-eq 0xBB -and $b[2]-eq 0xBF) } catch { return $false }
}
function Remove-Utf8BomSafely([string]$path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $bytes=[System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 3 -or $bytes[0]-ne 0xEF -or $bytes[1]-ne 0xBB -or $bytes[2]-ne 0xBF) { return $false }
    $clean = New-Object byte[] ($bytes.Length - 3)
    [Array]::Copy($bytes,3,$clean,0,$clean.Length)
    $tmp=$path+'.nobom-'+[Guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllBytes($tmp,$clean)
    try { if(Test-Path -LiteralPath $path){[System.IO.File]::SetAttributes($path,[System.IO.FileAttributes]::Normal)} } catch { }
    Move-Item -LiteralPath $tmp -Destination $path -Force
    return $true
}
function Read-ValidPair([string]$statePath,[string]$secretPath) {
    try {
        if (!(Test-Path -LiteralPath $statePath -PathType Leaf) -or !(Test-Path -LiteralPath $secretPath -PathType Leaf)) { return $null }
        $secretBytes=[System.IO.File]::ReadAllBytes($secretPath); if($secretBytes.Length -lt 32){return $null}
        $raw=[System.IO.File]::ReadAllText($statePath,[System.Text.Encoding]::UTF8)
        $env=$raw | ConvertFrom-Json
        if($null -eq $env -or [string]::IsNullOrWhiteSpace([string]$env.Payload) -or [string]::IsNullOrWhiteSpace([string]$env.Hmac)){return $null}
        $payload=[Convert]::FromBase64String([string]$env.Payload)
        $h=New-Object System.Security.Cryptography.HMACSHA256
        try{$h.Key=$secretBytes;$actual=Bytes-ToHex($h.ComputeHash($payload))}finally{$h.Dispose()}
        if($actual -ine ([string]$env.Hmac).Trim()){return $null}
        $state=([System.Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json)
        return [pscustomobject]@{State=$state;StateBytes=[System.IO.File]::ReadAllBytes($statePath);SecretBytes=$secretBytes;StatePath=$statePath;SecretPath=$secretPath}
    } catch { return $null }
}
function Score-State($state) {
    $rules=Count-Items $state.BrowserRules; $apps=Count-Items $state.Apps; $keys=Count-Items $state.Keys; $profiles=Count-Items $state.BlockProfiles; $audit=Count-Items $state.AuditLog; $sessions=Count-Items $state.SessionHistory
    $focus=0L;$play=0L;try{$focus=[long]$state.TotalFocusSeconds}catch{};try{$play=[long]$state.TotalEntertainmentSeconds}catch{}
    $onboard=0;try{if([bool]$state.Settings.OnboardingCompleted){$onboard=500}}catch{}
    return [int](($rules*10000)+($apps*10000)+($keys*3000)+([Math]::Max(0,$profiles-1)*3000)+([Math]::Min($audit,200)*100)+([Math]::Min($sessions,200)*20)+[Math]::Min(5000,[int](($focus+$play)/60))+$onboard)
}
function Find-BestPair {
    $states=@(Get-ChildItem -LiteralPath $codeRoot -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {$_.Name -in @('state.v2.json','state.v2.bak') -and $_.FullName -notlike '*\.tools\*' -and $_.FullName -notlike '*\.build-*'})
    $secrets=@(Get-ChildItem -LiteralPath $codeRoot -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object {$_.Name -in @('guard.secret','guard.secret.bak') -and $_.FullName -notlike '*\.tools\*' -and $_.FullName -notlike '*\.build-*'})
    Log ('Fallback scan: states='+$states.Count+' secrets='+$secrets.Count)
    $list=New-Object System.Collections.Generic.List[object]
    foreach($sf in $states){foreach($kf in $secrets){$v=Read-ValidPair $sf.FullName $kf.FullName;if($null -eq $v){continue};$list.Add([pscustomobject]@{Valid=$v;Score=(Score-State $v.State);Modified=$sf.LastWriteTimeUtc})}}
    if($list.Count -eq 0){return $null}
    return ($list|Sort-Object -Property @{Expression='Score';Descending=$true},@{Expression='Modified';Descending=$true}|Select-Object -First 1).Valid
}
function Write-BytesSafe([string]$path,[byte[]]$bytes) {
    Ensure-Dir (Split-Path -Parent $path); try{if(Test-Path -LiteralPath $path){[System.IO.File]::SetAttributes($path,[System.IO.FileAttributes]::Normal)}}catch{}
    $tmp=$path+'.tmp-'+[Guid]::NewGuid().ToString('N'); [System.IO.File]::WriteAllBytes($tmp,$bytes); Move-Item -LiteralPath $tmp -Destination $path -Force
}
function Test-GuardPipe([int]$timeoutMs=1000){$c=$null;try{$c=New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName,[System.IO.Pipes.PipeDirection]::InOut);$c.Connect($timeoutMs);return $c.IsConnected}catch{return $false}finally{if($null -ne $c){$c.Dispose()}}}
function Capture-Failure { try { Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -in @('.NET Runtime','Application Error','FocusLock.Service')} | Select-Object -First 8 | ForEach-Object { Log ('EVENT '+$_.ProviderName+' ID='+$_.Id+' | '+($_.Message -replace "`r?`n",' | ')) } } catch {} }

$patchedFiles=@('FocusLock.Service\Services\SecureStateStore.cs','FocusLock.Service\Services\FocusAuthorityEngine.cs','FocusLock.App\App.xaml.cs','Install-OneDir.ps1')

try {
    Step 'Stop current Guard and disable crash recovery during install'
    $svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if($svc){$serviceWasPresent=$true;Log('Current Guard path: '+[string]$svc.PathName);& sc.exe config $serviceName start= disabled | Out-Null;try{Stop-Service $serviceName -Force -ErrorAction SilentlyContinue}catch{};Start-Sleep -Seconds 1}
    try{Disable-ScheduledTask -TaskName 'FocusLock Guard Recovery' -ErrorAction SilentlyContinue|Out-Null}catch{}
    Get-Process FocusLock,FocusLock.NativeHost -ErrorAction SilentlyContinue|Stop-Process -Force -ErrorAction SilentlyContinue

    Step 'Keep current D-drive live data if its HMAC is valid'
    Ensure-Dir $recoveryDir
    if(Test-Path -LiteralPath $dataDir -PathType Container){Copy-DirContent $dataDir (Join-Path $recoveryDir 'FocusLock-Data-before')}
    $live=Read-ValidPair (Join-Path $dataDir 'state.v2.json') (Join-Path $dataDir 'guard.secret')
    if($null -eq $live){$live=Read-ValidPair (Join-Path $dataDir 'state.v2.json') (Join-Path $dataDir 'guard.secret.bak')}
    if($null -eq $live){
        Log 'Current FocusLock-Data is not HMAC-valid; using backup scan only as fallback.'
        $live=Find-BestPair
        if($null -eq $live){throw 'No HMAC-valid state + secret pair found. Data was not overwritten.'}
        Ensure-Dir $dataDir
        Write-BytesSafe (Join-Path $dataDir 'state.v2.json') $live.StateBytes
        Write-BytesSafe (Join-Path $dataDir 'state.v2.bak') $live.StateBytes
        Write-BytesSafe (Join-Path $dataDir 'guard.secret') $live.SecretBytes
        Write-BytesSafe (Join-Path $dataDir 'guard.secret.bak') $live.SecretBytes
        Log ('Recovered state from: '+$live.StatePath)
        Log ('Recovered secret from: '+$live.SecretPath)
    } else { Log 'Current FocusLock-Data HMAC pair is VALID and will be preserved.' }

    Step 'Normalize legacy UTF-8 BOM on the outer state envelope'
    $p1=Join-Path $dataDir 'state.v2.json';$p2=Join-Path $dataDir 'state.v2.bak'
    $hadBom1=Has-Utf8Bom $p1;$hadBom2=Has-Utf8Bom $p2
    Log ('Before normalization: state.v2.json BOM='+$hadBom1+' state.v2.bak BOM='+$hadBom2)
    if(Remove-Utf8BomSafely $p1){Log 'Removed UTF-8 BOM from state.v2.json.'}
    if(Remove-Utf8BomSafely $p2){Log 'Removed UTF-8 BOM from state.v2.bak.'}
    $check=Read-ValidPair $p1 (Join-Path $dataDir 'guard.secret')
    if($null -eq $check){$check=Read-ValidPair $p1 (Join-Path $dataDir 'guard.secret.bak')}
    if($null -eq $check){throw 'BOM normalization unexpectedly invalidated HMAC. Recovery backup was preserved.'}
    Log 'Post-normalization HMAC = VALID.'

    Step 'Patch reviewed source files'
    foreach($rel in $patchedFiles){$target=Join-Path $codeRoot $rel;$patch=Join-Path $payloadRoot $rel;if(!(Test-Path -LiteralPath $target)){throw('Source missing: '+$rel)};if(!(Test-Path -LiteralPath $patch)){throw('Payload missing: '+$rel)};$bak=Join-Path $sourceBackup $rel;Ensure-Dir(Split-Path -Parent $bak);Copy-Item $target $bak -Force;Copy-Item $patch $target -Force}

    Step 'Build App, Guard and NativeHost'
    $dotnet=Join-Path $codeRoot '.tools\dotnet\dotnet.exe';if(!(Test-Path $dotnet)){$cmd=Get-Command dotnet -ErrorAction SilentlyContinue;if(!$cmd){throw '.NET SDK not found.'};$dotnet=$cmd.Source}
    Ensure-Dir $stageRoot;Ensure-Dir(Join-Path $stageRoot 'App');Ensure-Dir(Join-Path $stageRoot 'Service');Ensure-Dir(Join-Path $stageRoot 'NativeHost')
    $env:DOTNET_CLI_HOME=Join-Path $codeRoot '.tools\dotnet-home';$env:NUGET_PACKAGES=Join-Path $codeRoot '.tools\nuget';$env:DOTNET_NOLOGO='1';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
    & $dotnet restore (Join-Path $codeRoot 'FocusLock.sln');if($LASTEXITCODE -ne 0){throw 'dotnet restore failed.'}
    & $dotnet publish (Join-Path $codeRoot 'FocusLock.App\FocusLock.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $stageRoot 'App');if($LASTEXITCODE -ne 0){throw 'App publish failed.'}
    & $dotnet publish (Join-Path $codeRoot 'FocusLock.Service\FocusLock.Service.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $stageRoot 'Service');if($LASTEXITCODE -ne 0){throw 'Service publish failed.'}
    & $dotnet publish (Join-Path $codeRoot 'FocusLock.NativeHost\FocusLock.NativeHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $stageRoot 'NativeHost');if($LASTEXITCODE -ne 0){throw 'NativeHost publish failed.'}
    $buildSucceeded=$true

    Step 'Assemble FINAL-FIX3 OneDir'
    Ensure-Dir $runtimeRoot;Copy-DirContent (Join-Path $stageRoot 'App') $runtimeRoot;Copy-DirContent (Join-Path $stageRoot 'Service') (Join-Path $runtimeRoot 'Service');Copy-DirContent (Join-Path $stageRoot 'NativeHost') (Join-Path $runtimeRoot 'NativeHost');Copy-DirContent (Join-Path $codeRoot 'BrowserExtension') (Join-Path $runtimeRoot 'BrowserExtension');Copy-Item (Join-Path $codeRoot 'Install-OneDir.ps1') (Join-Path $runtimeRoot 'Install-OneDir.ps1') -Force;Ensure-Dir(Join-Path $runtimeRoot 'Logs')

    Step 'Grant LocalSystem access to D-drive FocusLock-Data and verify ACL command'
    $sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $dataRoot /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ('*'+$sid+':(OI)(CI)F') /T /C
    if($LASTEXITCODE -ne 0){throw('icacls failed with exit code '+$LASTEXITCODE)}
    try{[Environment]::SetEnvironmentVariable('FOCUSLOCK_HOME',$null,'Machine')}catch{};try{[Environment]::SetEnvironmentVariable('FOCUSLOCK_HOME',$null,'User')}catch{};Remove-Item Env:FOCUSLOCK_HOME -ErrorAction SilentlyContinue

    Step 'Register FINAL-FIX3 Guard and verify Named Pipe'
    $serviceExe=Join-Path $runtimeRoot 'Service\FocusLock.Service.exe'
    if($serviceWasPresent){& sc.exe config $serviceName binPath= ('"'+$serviceExe+'"') start= auto obj= LocalSystem | Out-Null;if($LASTEXITCODE -ne 0){throw 'sc.exe config failed.'}}else{New-Service -Name $serviceName -BinaryPathName ('"'+$serviceExe+'"') -DisplayName 'FocusLock Guard' -StartupType Automatic|Out-Null}
    & sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
    Start-Service $serviceName -ErrorAction Stop
    $ready=$false
    for($i=0;$i -lt 40;$i++){if(Test-GuardPipe 1000){$ready=$true;break};$now=Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue;if($now -and [string]$now.State -eq 'Stopped'){break};Start-Sleep -Milliseconds 500}
    if(!$ready){Capture-Failure;& sc.exe config $serviceName start= disabled|Out-Null;try{Stop-Service $serviceName -Force -ErrorAction SilentlyContinue}catch{};throw 'FINAL-FIX3 Guard did not expose its Named Pipe. Service disabled; see log.'}
    Log 'Guard pipe = REACHABLE.'

    Step 'Verify live state after Service startup'
    $post=Read-ValidPair $p1 (Join-Path $dataDir 'guard.secret');if($null -eq $post){$post=Read-ValidPair $p1 (Join-Path $dataDir 'guard.secret.bak')};if($null -eq $post){throw 'Guard is running but live state is not HMAC-valid.'}
    Log ('Live state = VALID; rules='+(Count-Items $post.State.BrowserRules)+' apps='+(Count-Items $post.State.Apps)+' profiles='+(Count-Items $post.State.BlockProfiles)+' audit='+(Count-Items $post.State.AuditLog))

    Step 'Register startup and watchdog for FINAL-FIX3'
    $appExe=Join-Path $runtimeRoot 'FocusLock.exe';$run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';New-Item $run -Force|Out-Null;New-ItemProperty $run -Name FocusLock -Value ('"'+$appExe+'"') -PropertyType String -Force|Out-Null
    try{$a=New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\sc.exe') -Argument "start $serviceName";$t=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -AtLogOn));$pr=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;$st=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2);Register-ScheduledTask -TaskName 'FocusLock Guard Recovery' -Action $a -Trigger $t -Principal $pr -Settings $st -Force|Out-Null}catch{Log('Warning scheduled task: '+$_.Exception.Message)}
    try{$wa=New-ScheduledTaskAction -Execute $appExe -Argument '--ensure-scheduled';$wt=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650);$wu=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name;$wp=New-ScheduledTaskPrincipal -UserId $wu -LogonType Interactive -RunLevel Limited;$ws=New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2);Register-ScheduledTask -TaskName 'FocusLock Protected Window Watchdog' -Action $wa -Trigger $wt -Principal $wp -Settings $ws -Force|Out-Null}catch{Log('Warning watchdog: '+$_.Exception.Message)}

    Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '';Write-Host 'FINAL FIX 3 INSTALLED SUCCESSFULLY' -ForegroundColor Green;Write-Host ('App:  '+$appExe);Write-Host ('Data: '+$dataDir);Write-Host 'Guard pipe: REACHABLE';Write-Host 'Live HMAC: VALID';Write-Host ('Legacy BOM removed: '+($hadBom1 -or $hadBom2));Write-Host ''
    Start-Process $appExe
    exit 0
}
catch {
    Write-Host '';Write-Host 'FINAL FIX 3 FAILED' -ForegroundColor Red;Write-Host $_.Exception.Message -ForegroundColor Red;Log('ERROR: '+$_.Exception.ToString());Capture-Failure
    if(!$buildSucceeded){foreach($rel in $patchedFiles){$bak=Join-Path $sourceBackup $rel;$target=Join-Path $codeRoot $rel;if(Test-Path $bak){Copy-Item $bak $target -Force}}}
    Write-Host ('Log: '+$logPath);Write-Host ('Data safety backup: '+$recoveryDir);exit 1
}
