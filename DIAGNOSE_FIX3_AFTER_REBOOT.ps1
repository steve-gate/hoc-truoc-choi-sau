# FocusLock FIX3 post-reboot diagnostic. READ ONLY for FocusLock data/configuration.
# Windows PowerShell 5.1 compatible. ASCII-only.
$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest

function Find-CodeRoot {
    $cursor = Get-Item -LiteralPath $PSScriptRoot
    for ($i = 0; $i -lt 10 -and $null -ne $cursor; $i++) {
        if (Test-Path -LiteralPath (Join-Path $cursor.FullName 'FocusLock.sln') -PathType Leaf) { return $cursor.FullName }
        $cursor = $cursor.Parent
    }
    return $PSScriptRoot
}
function Hex([byte[]]$bytes) {
    $sb = New-Object System.Text.StringBuilder ($bytes.Length * 2)
    foreach ($v in $bytes) { [void]$sb.Append($v.ToString('X2')) }
    return $sb.ToString()
}
function Sha256-File([string]$path) {
    try {
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return 'MISSING' }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return Hex($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))) } finally { $sha.Dispose() }
    } catch { return 'ERROR: ' + $_.Exception.Message }
}
function Sha256-Bytes([byte[]]$bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return Hex($sha.ComputeHash($bytes)) } finally { $sha.Dispose() }
}
function Read-Envelope([string]$statePath) {
    $r = [ordered]@{ Path=$statePath; Exists=$false; Length=0; LastWriteUtc=''; Bom=$false; OuterJson=$false; PayloadBase64=$false; PayloadLength=0; PayloadSha256=''; EnvelopeHmac=''; PayloadJson=$false; Error='' ; PayloadBytes=$null }
    try {
        if (!(Test-Path -LiteralPath $statePath -PathType Leaf)) { $r.Error='MISSING'; return [pscustomobject]$r }
        $r.Exists=$true
        $item=Get-Item -LiteralPath $statePath
        $r.Length=$item.Length
        $r.LastWriteUtc=$item.LastWriteTimeUtc.ToString('o')
        $bytes=[System.IO.File]::ReadAllBytes($statePath)
        $r.Bom=($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $text=[System.IO.File]::ReadAllText($statePath,[System.Text.Encoding]::UTF8)
        $env=$text | ConvertFrom-Json
        $r.OuterJson=$true
        if ($null -eq $env -or [string]::IsNullOrWhiteSpace([string]$env.Payload) -or [string]::IsNullOrWhiteSpace([string]$env.Hmac)) { $r.Error='Envelope missing Payload/Hmac'; return [pscustomobject]$r }
        $payload=[Convert]::FromBase64String([string]$env.Payload)
        $r.PayloadBase64=$true
        $r.PayloadLength=$payload.Length
        $r.PayloadSha256=Sha256-Bytes $payload
        $r.EnvelopeHmac=([string]$env.Hmac).Trim().ToUpperInvariant()
        $r.PayloadBytes=$payload
        try { $null=([System.Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json); $r.PayloadJson=$true } catch { $r.Error='Payload JSON parse: '+$_.Exception.Message }
        return [pscustomobject]$r
    } catch { $r.Error=$_.Exception.Message; return [pscustomobject]$r }
}
function Read-Secret([string]$secretPath) {
    $r=[ordered]@{Path=$secretPath;Exists=$false;Length=0;LastWriteUtc='';Sha256='';Attributes='';Bytes=$null;Error=''}
    try {
        if (!(Test-Path -LiteralPath $secretPath -PathType Leaf)) { $r.Error='MISSING'; return [pscustomobject]$r }
        $r.Exists=$true
        $item=Get-Item -LiteralPath $secretPath -Force
        $r.Length=$item.Length
        $r.LastWriteUtc=$item.LastWriteTimeUtc.ToString('o')
        $r.Attributes=[string]$item.Attributes
        $b=[System.IO.File]::ReadAllBytes($secretPath)
        $r.Bytes=$b
        $r.Sha256=Sha256-Bytes $b
        return [pscustomobject]$r
    } catch { $r.Error=$_.Exception.Message; return [pscustomobject]$r }
}
function Test-Matrix($env,$secret) {
    if (!$env.Exists -or !$env.PayloadBase64 -or $null -eq $env.PayloadBytes) { return 'STATE_UNREADABLE' }
    if (!$secret.Exists -or $null -eq $secret.Bytes -or $secret.Bytes.Length -lt 32) { return 'SECRET_UNREADABLE' }
    try {
        $h=New-Object System.Security.Cryptography.HMACSHA256
        try { $h.Key=$secret.Bytes; $actual=Hex($h.ComputeHash($env.PayloadBytes)) } finally { $h.Dispose() }
        if ($actual -ieq $env.EnvelopeHmac) { return 'VALID' }
        return 'HMAC_MISMATCH computed='+$actual.Substring(0,16)+'... envelope='+$env.EnvelopeHmac.Substring(0,[Math]::Min(16,$env.EnvelopeHmac.Length))+'...'
    } catch { return 'ERROR: '+$_.Exception.Message }
}
function Test-Pipe {
    $c=$null
    try { $c=New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5',[System.IO.Pipes.PipeDirection]::InOut); $c.Connect(1200); return $c.IsConnected }
    catch { return $false }
    finally { if ($null -ne $c) { $c.Dispose() } }
}
function Write-Section([string]$title) { Add-Content -LiteralPath $report -Value ("`r`n===== "+$title+" =====") -Encoding UTF8 }
function Write-Line([string]$line) { Add-Content -LiteralPath $report -Value $line -Encoding UTF8 }

$root=Find-CodeRoot
$data=Join-Path $root 'FocusLock-Data\Data'
$outDir=Join-Path $root 'FocusLock-Diagnostics'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$tag=Get-Date -Format 'yyyyMMdd-HHmmss'
$report=Join-Path $outDir ('FIX3-AFTER-REBOOT-'+$tag+'.txt')

Write-Line ('Time='+[DateTimeOffset]::Now.ToString('o'))
Write-Line ('CodeRoot='+$root)
Write-Line ('Data='+$data)

Write-Section 'Service and pipe'
try {
    $svc=Get-CimInstance Win32_Service -Filter "Name='FocusLockGuard'" -ErrorAction Stop
    Write-Line ('Name='+$svc.Name)
    Write-Line ('State='+$svc.State)
    Write-Line ('ProcessId='+$svc.ProcessId)
    Write-Line ('StartMode='+$svc.StartMode)
    Write-Line ('StartName='+$svc.StartName)
    Write-Line ('ExitCode='+$svc.ExitCode)
    Write-Line ('PathName='+$svc.PathName)
} catch { Write-Line ('ServiceError='+$_.Exception.Message) }
Write-Line ('PipeReachable='+(Test-Pipe))

Write-Section 'Environment and startup'
foreach($scope in @('Process','User','Machine')) { try { Write-Line ('FOCUSLOCK_HOME['+$scope+']='+[Environment]::GetEnvironmentVariable('FOCUSLOCK_HOME',$scope)) } catch {} }
try { $run=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name FocusLock -ErrorAction Stop).FocusLock; Write-Line ('HKCU Run FocusLock='+$run) } catch { Write-Line 'HKCU Run FocusLock=<missing>' }
foreach($tn in @('FocusLock Guard Recovery','FocusLock Protected Window Watchdog')) {
    try { $t=Get-ScheduledTask -TaskName $tn -ErrorAction Stop; Write-Line ('Task '+$tn+' State='+$t.State); foreach($a in $t.Actions){Write-Line ('  Action='+$a.Execute+' '+$a.Arguments)} } catch { Write-Line ('Task '+$tn+'=<missing>') }
}

Write-Section 'Live file metadata'
$state1=Read-Envelope (Join-Path $data 'state.v2.json')
$state2=Read-Envelope (Join-Path $data 'state.v2.bak')
$sec1=Read-Secret (Join-Path $data 'guard.secret')
$sec2=Read-Secret (Join-Path $data 'guard.secret.bak')
foreach($s in @($state1,$state2)) {
    Write-Line ('STATE '+$s.Path)
    Write-Line ('  Exists='+$s.Exists+' Length='+$s.Length+' LastWriteUtc='+$s.LastWriteUtc+' BOM='+$s.Bom)
    Write-Line ('  OuterJson='+$s.OuterJson+' PayloadBase64='+$s.PayloadBase64+' PayloadLength='+$s.PayloadLength+' PayloadJson='+$s.PayloadJson)
    Write-Line ('  PayloadSHA256='+$s.PayloadSha256)
    Write-Line ('  EnvelopeHMAC='+$s.EnvelopeHmac)
    Write-Line ('  Error='+$s.Error)
}
foreach($k in @($sec1,$sec2)) {
    Write-Line ('SECRET '+$k.Path)
    Write-Line ('  Exists='+$k.Exists+' Length='+$k.Length+' LastWriteUtc='+$k.LastWriteUtc+' Attr='+$k.Attributes)
    Write-Line ('  SHA256='+$k.Sha256)
    Write-Line ('  Error='+$k.Error)
}

Write-Section 'Exact 4-way HMAC matrix'
Write-Line ('state.v2.json + guard.secret     = '+(Test-Matrix $state1 $sec1))
Write-Line ('state.v2.json + guard.secret.bak = '+(Test-Matrix $state1 $sec2))
Write-Line ('state.v2.bak  + guard.secret     = '+(Test-Matrix $state2 $sec1))
Write-Line ('state.v2.bak  + guard.secret.bak = '+(Test-Matrix $state2 $sec2))

Write-Section 'Known recovery reference hashes (READ ONLY)'
$refs=@(
    (Join-Path $root 'publish\Data\state.v2.bak'),
    (Join-Path $root 'publish\Data\guard.secret'),
    (Join-Path $root 'FocusLock-Persistence-Config-Safety-20260824-175350\ProgramData-Data-Before\guard.secret')
)
foreach($p in $refs) { if(Test-Path -LiteralPath $p -PathType Leaf){ $i=Get-Item -LiteralPath $p -Force; Write-Line ($p+' Length='+$i.Length+' LastWriteUtc='+$i.LastWriteTimeUtc.ToString('o')+' SHA256='+(Sha256-File $p)) } }

Write-Section 'ACL'
try { (& icacls.exe $data 2>&1) | ForEach-Object { Write-Line ([string]$_) } } catch { Write-Line ('icacls error='+$_.Exception.Message) }

Write-Section 'Recent Application events'
try {
    Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-30)} -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','FocusLock.Service') } |
        Select-Object -First 12 |
        ForEach-Object { Write-Line ('['+$_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')+'] '+$_.ProviderName+' ID='+$_.Id+' | '+($_.Message -replace "`r?`n",' | ')) }
} catch { Write-Line ('EventLogError='+$_.Exception.Message) }

Write-Section 'Recent System service events'
try {
    Get-WinEvent -FilterHashtable @{LogName='System';StartTime=(Get-Date).AddMinutes(-30)} -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -eq 'Service Control Manager' -and $_.Message -like '*FocusLock*' } |
        Select-Object -First 12 |
        ForEach-Object { Write-Line ('['+$_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')+'] SCM ID='+$_.Id+' | '+($_.Message -replace "`r?`n",' | ')) }
} catch { Write-Line ('SCMLogError='+$_.Exception.Message) }

Write-Host ''
Write-Host 'FIX3 POST-REBOOT DIAGNOSTIC COMPLETE' -ForegroundColor Green
Write-Host 'READ ONLY: no FocusLock Data or service configuration was changed.'
Write-Host ('Report: '+$report)
exit 0
