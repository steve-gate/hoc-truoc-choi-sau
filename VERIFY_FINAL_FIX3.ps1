# ASCII-only verification. Read-only.
$ErrorActionPreference='SilentlyContinue'
$root=$PSScriptRoot
while($root -and !(Test-Path (Join-Path $root 'FocusLock.sln'))){$root=Split-Path -Parent $root}
if(!$root){Write-Host 'FocusLock.sln not found.';exit 1}
$data=Join-Path $root 'FocusLock-Data\Data'
function Hex([byte[]]$b){$s='';foreach($v in $b){$s+=$v.ToString('X2')};$s}
function Valid([string]$sp,[string]$kp){try{$k=[IO.File]::ReadAllBytes($kp);$raw=[IO.File]::ReadAllText($sp,[Text.Encoding]::UTF8);$e=$raw|ConvertFrom-Json;$p=[Convert]::FromBase64String([string]$e.Payload);$h=New-Object Security.Cryptography.HMACSHA256;$h.Key=$k;$x=Hex($h.ComputeHash($p));$h.Dispose();if($x-ine([string]$e.Hmac).Trim()){return $null};return([Text.Encoding]::UTF8.GetString($p)|ConvertFrom-Json)}catch{return $null}}
function Pipe(){try{$c=New-Object IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5',[IO.Pipes.PipeDirection]::InOut);$c.Connect(1200);$ok=$c.IsConnected;$c.Dispose();return $ok}catch{return $false}}
$svc=Get-CimInstance Win32_Service -Filter "Name='FocusLockGuard'"
Write-Host ('Service State='+$svc.State)
Write-Host ('Service Path='+$svc.PathName)
Write-Host ('PipeReachable='+(Pipe))
Write-Host ('Data='+$data)
foreach($n in @('state.v2.json','state.v2.bak')){$p=Join-Path $data $n;if(Test-Path $p){$b=[IO.File]::ReadAllBytes($p);$bom=($b.Length-ge3-and$b[0]-eq0xEF-and$b[1]-eq0xBB-and$b[2]-eq0xBF);Write-Host ($n+' BOM='+$bom+' Bytes='+$b.Length)}}
$s=Valid (Join-Path $data 'state.v2.json') (Join-Path $data 'guard.secret');if($null-eq$s){$s=Valid (Join-Path $data 'state.v2.json') (Join-Path $data 'guard.secret.bak')}
if($null-eq$s){Write-Host 'STATE: INVALID';exit 2}
Write-Host 'STATE: VALID'
Write-Host ('Counts: BrowserRules='+@($s.BrowserRules).Count+' Apps='+@($s.Apps).Count+' Profiles='+@($s.BlockProfiles).Count+' Audit='+@($s.AuditLog).Count)
