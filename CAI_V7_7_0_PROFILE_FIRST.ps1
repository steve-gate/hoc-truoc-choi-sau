$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-Admin {
  $id=[Security.Principal.WindowsIdentity]::GetCurrent()
  $p=New-Object Security.Principal.WindowsPrincipal($id)
  return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if(-not (Test-Admin)){ throw "Mo CMD bang Run as administrator." }

$root=Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$engine=Join-Path $root "FocusLock.Service\Services\FocusAuthorityEngine.cs"
$mainCs=Join-Path $root "FocusLock.App\MainWindow.xaml.cs"
$appProj=Join-Path $root "FocusLock.App\FocusLock.App.csproj"
$mainXaml=Join-Path $root "FocusLock.App\MainWindow.xaml"

foreach($p in @($engine,$mainCs,$appProj,$mainXaml)){
  if(!(Test-Path $p)){ throw "Thieu file: $p" }
}

Write-Host ""
Write-Host "==> V7.7.0 Profile-first UX" -ForegroundColor Cyan

# Preflight repair of compile artifacts from earlier hotfixes.
$t=[IO.File]::ReadAllText($mainCs)
$t=$t.Replace('`r`n',[Environment]::NewLine)
$t=[regex]::Replace($t,'(?m)^\s*Opacity\s*=\s*0\.55\s*,?\s*$','')
if($t -match '\bnew\s+Run\(' -and $t -notmatch '(?m)^using System\.Windows\.Documents;\s*$'){
  $t="using System.Windows.Documents;`r`n"+$t
}
[IO.File]::WriteAllText($mainCs,$t,(New-Object Text.UTF8Encoding($true)))

# Patch AddApp: honor requested valid Profile instead of always default profile.
$e=[IO.File]::ReadAllText($engine)

$oldApp=@'
        if (app.Category == AppCategory.Entertainment)
        {
            var profile = GetDefaultBlockProfileUnsafe();
            app.BlockProfileId = profile.Id;
            app.BlockProfileName = profile.Name;
        }
'@
$newApp=@'
        if (app.Category == AppCategory.Entertainment)
        {
            var requestedProfile = _state.BlockProfiles.FirstOrDefault(p => p.Id == app.BlockProfileId);
            var profile = requestedProfile ?? GetDefaultBlockProfileUnsafe();
            app.BlockProfileId = profile.Id;
            app.BlockProfileName = profile.Name;
        }
'@
if($e.Contains($oldApp)){
  $e=$e.Replace($oldApp,$newApp)
}else{
  Write-Host "[INFO] AddApp profile-preserve patch already applied or source differs." -ForegroundColor Yellow
}

# Patch AddBrowserRule the same way.
$oldWeb=@'
        if (rule.Category == AppCategory.Entertainment)
        {
            var profile = GetDefaultBlockProfileUnsafe();
            rule.BlockProfileId = profile.Id;
            rule.BlockProfileName = profile.Name;
        }
'@
$newWeb=@'
        if (rule.Category == AppCategory.Entertainment)
        {
            var requestedProfile = _state.BlockProfiles.FirstOrDefault(p => p.Id == rule.BlockProfileId);
            var profile = requestedProfile ?? GetDefaultBlockProfileUnsafe();
            rule.BlockProfileId = profile.Id;
            rule.BlockProfileName = profile.Name;
        }
'@
if($e.Contains($oldWeb)){
  $e=$e.Replace($oldWeb,$newWeb)
}else{
  Write-Host "[INFO] AddBrowserRule profile-preserve patch already applied or source differs." -ForegroundColor Yellow
}

[IO.File]::WriteAllText($engine,$e,(New-Object Text.UTF8Encoding($true)))

# Version.
$p=[IO.File]::ReadAllText($appProj)
$p=[regex]::Replace($p,'<Version>[^<]+</Version>','<Version>7.7.0</Version>',1)
$p=[regex]::Replace($p,'<AssemblyVersion>[^<]+</AssemblyVersion>','<AssemblyVersion>7.7.0.0</AssemblyVersion>',1)
$p=[regex]::Replace($p,'<FileVersion>[^<]+</FileVersion>','<FileVersion>7.7.0.0</FileVersion>',1)
$p=[regex]::Replace($p,'<InformationalVersion>[^<]+</InformationalVersion>','<InformationalVersion>7.7.0</InformationalVersion>',1)
[IO.File]::WriteAllText($appProj,$p,(New-Object Text.UTF8Encoding($true)))

$x=[IO.File]::ReadAllText($mainXaml)
$x=[regex]::Replace($x,'Text="V7\.[0-9.]+"','Text="V7.7.0"',1)
[IO.File]::WriteAllText($mainXaml,$x,(New-Object Text.UTF8Encoding($true)))

$dotnet=Join-Path $root ".tools\dotnet\dotnet.exe"
if(!(Test-Path $dotnet)){ $dotnet="dotnet" }

$stage=Join-Path $root ".build-v770"
$appStage=Join-Path $stage "App"
$svcStage=Join-Path $stage "Service"
if(Test-Path $stage){Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue}
New-Item -ItemType Directory -Path $appStage,$svcStage -Force | Out-Null

Write-Host "==> Build App" -ForegroundColor Cyan
& $dotnet publish ".\FocusLock.App\FocusLock.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appStage
if($LASTEXITCODE -ne 0){throw "App build failed: $LASTEXITCODE"}

Write-Host "==> Build Service" -ForegroundColor Cyan
& $dotnet publish ".\FocusLock.Service\FocusLock.Service.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $svcStage
if($LASTEXITCODE -ne 0){throw "Service build failed: $LASTEXITCODE"}

Get-Process FocusLock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$svc=Get-Service FocusLockGuard -ErrorAction SilentlyContinue
if($svc -and $svc.Status -ne "Stopped"){
  Stop-Service FocusLockGuard -Force -ErrorAction SilentlyContinue
  try{$svc.WaitForStatus("Stopped",[TimeSpan]::FromSeconds(15))}catch{}
}
Start-Sleep -Milliseconds 700

function Replace-Dir([string]$src,[string]$dst){
  if(Test-Path $dst){Remove-Item $dst -Recurse -Force}
  New-Item -ItemType Directory -Path $dst -Force | Out-Null
  Copy-Item (Join-Path $src "*") $dst -Recurse -Force
}

$publish=Join-Path $root "publish"
Replace-Dir $appStage (Join-Path $publish "App")
Replace-Dir $svcStage (Join-Path $publish "Service")

$svcExe=Join-Path $publish "Service\FocusLock.Service.exe"
if(Get-Service FocusLockGuard -ErrorAction SilentlyContinue){
  & sc.exe config FocusLockGuard binPath= ('"' + $svcExe + '"') start= auto obj= LocalSystem | Out-Null
}else{
  New-Service -Name FocusLockGuard -BinaryPathName ('"' + $svcExe + '"') -DisplayName "FocusLock Guard" -StartupType Automatic | Out-Null
}
Start-Service FocusLockGuard
$s=Get-Service FocusLockGuard
try{$s.WaitForStatus("Running",[TimeSpan]::FromSeconds(20))}catch{}
$s.Refresh()
if($s.Status -ne "Running"){throw "FocusLockGuard khong Running."}

$appExe=Join-Path $publish "App\FocusLock.exe"
$ver=(Get-Item $appExe).VersionInfo.FileVersion
if($ver -notlike "7.7.0*"){throw "Sai App version: $ver"}

Start-Process $appExe

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "OK - FOCUSLOCK V7.7.0 PROFILE-FIRST INSTALLED" -ForegroundColor Green
Write-Host "Add App/Web directly inside selected Profile" -ForegroundColor Green
Write-Host "Existing item -> move directly to selected Profile" -ForegroundColor Green
Write-Host "Policy editor -> policy only" -ForegroundColor Green
Write-Host "publish\Data / Browser Core / NativeHost: UNCHANGED" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
