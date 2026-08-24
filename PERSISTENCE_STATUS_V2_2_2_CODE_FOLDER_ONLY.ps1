$ErrorActionPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$root=$PSScriptRoot
$serviceName='FocusLockGuard'
$codeDataRoot=Join-Path $root 'FocusLock-Data'
$dataDir=Join-Path $codeDataRoot 'Data'
$statePath=Join-Path $dataDir 'state.v2.json'
$secretPath=Join-Path $dataDir 'guard.secret'
$serviceRegPath="HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
function Bytes-ToHex([byte[]]$bytes){return ([BitConverter]::ToString($bytes)).Replace('-','')}
function Validate-State([string]$state,[string]$secret){
  try{
    if(!(Test-Path -LiteralPath $state -PathType Leaf)-or !(Test-Path -LiteralPath $secret -PathType Leaf)){return $null}
    $secretBytes=[IO.File]::ReadAllBytes($secret)
    $envObj=([IO.File]::ReadAllText($state,[Text.Encoding]::UTF8)|ConvertFrom-Json)
    $payload=[Convert]::FromBase64String([string]$envObj.Payload)
    $h=New-Object System.Security.Cryptography.HMACSHA256
    try{$h.Key=$secretBytes;$actual=Bytes-ToHex ($h.ComputeHash($payload))}finally{$h.Dispose()}
    if($actual -ine ([string]$envObj.Hmac).Trim()){return [pscustomobject]@{Valid=$false}}
    $s=([Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json)
    return [pscustomobject]@{Valid=$true;Schema=$s.SchemaVersion;BrowserRules=@($s.BrowserRules).Count;Apps=@($s.Apps).Count;Profiles=@($s.BlockProfiles).Count;Audit=@($s.AuditLog).Count}
  }catch{return [pscustomobject]@{Valid=$false;Error=$_.Exception.Message}}
}
Write-Host '=== FocusLock Persistence V2.2.2 CODE-FOLDER-ONLY Status ===' -ForegroundColor Cyan
Write-Host "Expected root: $codeDataRoot"
Write-Host "Expected Data: $dataDir"
$r=Validate-State $statePath $secretPath
if($null -eq $r){Write-Host 'STATE: MISSING' -ForegroundColor Red}
elseif($r.Valid){Write-Host 'STATE: VALID' -ForegroundColor Green;Write-Host "Schema=$($r.Schema) BrowserRules=$($r.BrowserRules) Apps=$($r.Apps) Profiles=$($r.Profiles) Audit=$($r.Audit)"}
else{Write-Host 'STATE: INVALID' -ForegroundColor Red;if($r.PSObject.Properties.Name -contains 'Error'){Write-Host $r.Error}}
$svc=Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if($svc){Write-Host "Service: $($svc.State) PID=$($svc.ProcessId)";Write-Host "Path:    $($svc.PathName)"}else{Write-Host 'Service: MISSING' -ForegroundColor Red}
$serviceEnv=@((Get-ItemProperty -LiteralPath $serviceRegPath -Name Environment).Environment)
Write-Host 'Service Environment:'
$serviceEnv | ForEach-Object { Write-Host "  $_" }
$machineOverride=[Environment]::GetEnvironmentVariable('FOCUSLOCK_HOME','Machine')
if([string]::IsNullOrWhiteSpace($machineOverride)){Write-Host 'Machine FOCUSLOCK_HOME: <empty> (expected)' -ForegroundColor Green}else{Write-Host "Machine FOCUSLOCK_HOME: $machineOverride (unexpected)" -ForegroundColor Yellow}
if(Test-Path -LiteralPath $statePath){Write-Host "state modified:  $((Get-Item -LiteralPath $statePath).LastWriteTime)";Write-Host "state SHA256:    $((Get-FileHash -Algorithm SHA256 -LiteralPath $statePath).Hash)"}
if(Test-Path -LiteralPath $secretPath){Write-Host "secret modified: $((Get-Item -LiteralPath $secretPath).LastWriteTime)";Write-Host "secret SHA256:   $((Get-FileHash -Algorithm SHA256 -LiteralPath $secretPath).Hash)"}
