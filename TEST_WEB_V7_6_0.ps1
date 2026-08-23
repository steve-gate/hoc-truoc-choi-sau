$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "web-v760-test.txt"
Remove-Item $out -Force -ErrorAction SilentlyContinue

function Snapshot {
  $p=$null;$r=$null;$w=$null
  try {
    $p=New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
    $p.Connect(1200)
    $r=New-Object System.IO.StreamReader($p,[Text.Encoding]::UTF8,$true,4096,$true)
    $w=New-Object System.IO.StreamWriter($p,(New-Object Text.UTF8Encoding($false)),4096,$true);$w.AutoFlush=$true
    $w.WriteLine('{"id":"v760test","command":"snapshot"}')
    $line=$r.ReadLine()
    if($line){return ($line|ConvertFrom-Json).snapshot}
  } catch {}
  finally {if($r){$r.Dispose()};if($w){$w.Dispose()};if($p){$p.Dispose()}}
  return $null
}

"FocusLock V7.6 web test $(Get-Date)" | Out-File $out -Encoding utf8
Write-Host "30 giay test. 0-15s mo WEB HOC; 15-30s mo WEB GIAI TRI." -ForegroundColor Yellow
for($i=0;$i -lt 30;$i++){
  $s=Snapshot
  if($s){
    $line="{0} host={1} cat={2} fg={3} focusQ={4} ent={5} access={6} wallet={7} focus={8} hb={9}" -f `
      (Get-Date -Format HH:mm:ss),$s.currentBrowserHost,$s.currentBrowserCategory,$s.browserForegroundActive,`
      $s.browserFocusQualified,$s.entertainmentSessionActive,$s.currentBrowserAccess,`
      $s.state.entertainmentBalanceSeconds,$s.state.focusProgressSeconds,$s.heartbeatHealthy
    $line | Tee-Object -FilePath $out -Append
  }
  Start-Sleep 1
}
Write-Host "Xong: $out" -ForegroundColor Green
