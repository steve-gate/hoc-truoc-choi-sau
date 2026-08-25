$ErrorActionPreference = 'SilentlyContinue'
function Find-CodeRoot {
    $cursor = Get-Item -LiteralPath $PSScriptRoot
    for ($depth=0; $depth -lt 10 -and $null -ne $cursor; $depth++) {
        if (Test-Path -LiteralPath (Join-Path $cursor.FullName 'FocusLock.sln') -PathType Leaf) { return $cursor.FullName }
        $cursor = $cursor.Parent
    }
    return $null
}
function Bytes-ToHex([byte[]]$bytes) { $builder=New-Object System.Text.StringBuilder ($bytes.Length*2); foreach($value in $bytes){[void]$builder.Append($value.ToString('X2'))}; return $builder.ToString() }
function Validate([string]$statePath,[string]$secretPath) {
    try {
        $secret=[IO.File]::ReadAllBytes($secretPath); if($secret.Length -lt 32){return $null}
        $envObj=([IO.File]::ReadAllText($statePath,[Text.Encoding]::UTF8)|ConvertFrom-Json)
        $payload=[Convert]::FromBase64String([string]$envObj.Payload)
        $h=New-Object System.Security.Cryptography.HMACSHA256; try{$h.Key=$secret;$actual=Bytes-ToHex($h.ComputeHash($payload))}finally{$h.Dispose()}
        if($actual -ine ([string]$envObj.Hmac).Trim()){return $null}
        return ([Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json)
    }catch{return $null}
}
function Test-Pipe { $c=$null; try{$c=New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5',[System.IO.Pipes.PipeDirection]::InOut);$c.Connect(1200);return $c.IsConnected}catch{return $false}finally{if($c){$c.Dispose()}} }
$root=Find-CodeRoot
if(!$root){Write-Host 'ERROR: FocusLock.sln not found.';exit 2}
$data=Join-Path $root 'FocusLock-Data\Data'
$svc=Get-CimInstance Win32_Service -Filter "Name='FocusLockGuard'"
Write-Host ('CodeRoot: ' + $root)
Write-Host ('Data: ' + $data)
if($svc){Write-Host ('Service State=' + $svc.State + ' PID=' + $svc.ProcessId);Write-Host ('Service Path=' + $svc.PathName)}else{Write-Host 'Service: NOT FOUND'}
Write-Host ('PipeReachable=' + (Test-Pipe))
$checks=@(
    @('state.v2.json','guard.secret'),
    @('state.v2.json','guard.secret.bak'),
    @('state.v2.bak','guard.secret'),
    @('state.v2.bak','guard.secret.bak')
)
$any=$false
foreach($pair in $checks){
    $sp=Join-Path $data $pair[0];$kp=Join-Path $data $pair[1]
    $state=Validate $sp $kp
    $ok=$null -ne $state
    Write-Host ($pair[0] + ' + ' + $pair[1] + ' = ' + ($(if($ok){'VALID'}else{'INVALID'})))
    if($ok -and !$any){
        $any=$true
        $rules=@($state.BrowserRules).Count;$apps=@($state.Apps).Count;$profiles=@($state.BlockProfiles).Count;$audit=@($state.AuditLog).Count
        Write-Host ('Counts: BrowserRules=' + $rules + ' Apps=' + $apps + ' Profiles=' + $profiles + ' Audit=' + $audit)
    }
}
if(!$any){exit 3}
exit 0
