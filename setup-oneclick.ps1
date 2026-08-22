$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$logFile = Join-Path $root "install.log"
$toolsRoot = Join-Path $root ".tools"
$dotnetRoot = Join-Path $toolsRoot "dotnet"
$dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
$dotnetInstall = Join-Path $toolsRoot "dotnet-install.ps1"
$localTemp = Join-Path $toolsRoot "temp"
$nugetRoot = Join-Path $toolsRoot "nuget"
$cliHome = Join-Path $toolsRoot "dotnet-home"
$publishRoot = Join-Path $root "publish"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Ensure-Dir([string]$path) {
    if (!(Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function Assert-File([string]$path, [string]$label) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$label was not created: $path"
    }
}

# V6.4: elevation is handled by CAI_DAT.bat only.
# If this PS1 is launched directly without admin rights, fail immediately
# instead of opening another PowerShell process and waiting forever.
if (-not (Test-Administrator)) {
    Write-Host "Administrator permission is required." -ForegroundColor Red
    Write-Host "Please run CAI_DAT.bat instead of setup-oneclick.ps1." -ForegroundColor Yellow
    exit 5
}

try {
    if (Test-Path -LiteralPath $logFile) { Remove-Item -LiteralPath $logFile -Force -ErrorAction SilentlyContinue }
    Start-Transcript -Path $logFile -Force | Out-Null
} catch { }

Push-Location $root
try {
    Step "Preparing local build folders"
    foreach ($dir in @($toolsRoot, $dotnetRoot, $localTemp, $nugetRoot, $cliHome)) { Ensure-Dir $dir }

    # Keep SDK/cache/temp in the FocusLock code folder, not on C:.
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_CLI_HOME = $cliHome
    $env:NUGET_PACKAGES = $nugetRoot
    $env:TEMP = $localTemp
    $env:TMP = $localTemp
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:PATH = "$dotnetRoot;$env:PATH"

    $sdkOk = $false
    if (Test-Path -LiteralPath $dotnetExe) {
        try {
            & $dotnetExe --version | Out-Host
            if ($LASTEXITCODE -eq 0) { $sdkOk = $true }
        } catch { $sdkOk = $false }
    }

    if (-not $sdkOk) {
        Step "Downloading .NET 10 SDK into .tools\\dotnet"
        if (Test-Path -LiteralPath $dotnetRoot) {
            Remove-Item -LiteralPath $dotnetRoot -Recurse -Force -ErrorAction SilentlyContinue
            Ensure-Dir $dotnetRoot
        }

        $downloaded = $false
        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($curl) {
            Write-Host "Trying curl.exe..." -ForegroundColor DarkGray
            & $curl.Source -fL --retry 3 --connect-timeout 20 "https://dot.net/v1/dotnet-install.ps1" -o $dotnetInstall
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $dotnetInstall)) { $downloaded = $true }
        }

        if (-not $downloaded) {
            Write-Host "Trying Invoke-WebRequest..." -ForegroundColor DarkGray
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -UseBasicParsing -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $dotnetInstall
                if (Test-Path -LiteralPath $dotnetInstall) { $downloaded = $true }
            } catch { }
        }

        if (-not $downloaded) {
            throw "Could not download dotnet-install.ps1. Check Internet/VPN/Firewall and run CAI_DAT.bat again."
        }

        & $dotnetInstall -Channel "10.0" -Architecture "x64" -InstallDir $dotnetRoot -NoPath
        Assert-File $dotnetExe ".NET SDK"
        & $dotnetExe --version | Out-Host
        if ($LASTEXITCODE -ne 0) { throw ".NET SDK was downloaded but dotnet.exe cannot run." }
    }
    else {
        Step "Local .NET SDK already exists"
    }

    Step "Checking SDK"
    & $dotnetExe --info | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "The local .NET SDK cannot run." }

    Step "Building FocusLock safely"
    # build-release.ps1 is a PowerShell script. Any real failure throws and is caught
    # by this outer try/catch. Do NOT inspect $LASTEXITCODE here: it may contain a
    # harmless native command code (for example taskkill = 128 when no process exists).
    & (Join-Path $root "build-release.ps1") -DotNetExe $dotnetExe

    $deployedAppExe = Join-Path $publishRoot "App\FocusLock.exe"
    Assert-File $deployedAppExe "FocusLock.exe"
    Assert-File (Join-Path $publishRoot "Service\FocusLock.Service.exe") "FocusLock.Service.exe"

    $nativePointer = Join-Path $publishRoot "nativehost.current"
    if (!(Test-Path -LiteralPath $nativePointer -PathType Leaf)) {
        throw "nativehost.current was not created."
    }
    $nativeSlot = ([System.IO.File]::ReadAllText($nativePointer)).Trim()
    if ([string]::IsNullOrWhiteSpace($nativeSlot)) { throw "nativehost.current is empty." }
    Assert-File (Join-Path $publishRoot "$nativeSlot\FocusLock.NativeHost.exe") "FocusLock.NativeHost.exe"

    $uiFileVersion = (Get-Item -LiteralPath $deployedAppExe).VersionInfo.FileVersion
    if ($uiFileVersion -notlike "7.5.2*") {
        throw "Wrong UI binary after build: $uiFileVersion. Expected 7.5.2.0."
    }
    Write-Host "Verified UI file version: $uiFileVersion" -ForegroundColor Green

    Step "Installing/updating Windows Service and Browser Bridge"
    # Same rule here: install-v5.ps1 throws on a real failure. $LASTEXITCODE may be
    # stale from sc.exe/icacls/taskkill, so it is not a reliable script result.
    & (Join-Path $publishRoot "install-v5.ps1")

    Step "Verifying FocusLock UI startup"
    $appExe = Join-Path $publishRoot "App\FocusLock.exe"
    $crashLog = Join-Path $publishRoot "Logs\crash.log"
    $startupLog = Join-Path $publishRoot "Logs\startup.log"
    $uiProcess = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 500
        $candidates = @(Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue)
        foreach ($candidate in $candidates) {
            try {
                if ([System.IO.Path]::GetFullPath($candidate.Path) -eq [System.IO.Path]::GetFullPath($appExe)) {
                    $uiProcess = $candidate
                    break
                }
            } catch { }
        }
        if ($uiProcess) {
            try { $uiProcess.Refresh() } catch { $uiProcess = $null }
            if ($uiProcess -and $uiProcess.HasExited) { $uiProcess = $null }
        }
    } while (-not $uiProcess -and [DateTime]::UtcNow -lt $deadline)

    if (-not $uiProcess) {
        $extra = if (Test-Path -LiteralPath $crashLog) { " Crash log: $crashLog" } elseif (Test-Path -LiteralPath $startupLog) { " Startup log: $startupLog" } else { " No UI log was created." }
        throw "FocusLock.exe exited immediately after installation.$extra"
    }

    # Give WPF a little more time. A living process is enough; MainWindowHandle can be 0 briefly during first-run initialization.
    Start-Sleep -Seconds 3
    try { $uiProcess.Refresh() } catch { }
    if ($uiProcess.HasExited) {
        $extra = if (Test-Path -LiteralPath $crashLog) { " Crash log: $crashLog" } else { "" }
        throw "FocusLock UI started and then exited.$extra"
    }
    $runningVersion = (Get-Item -LiteralPath $appExe).VersionInfo.FileVersion
    if ($runningVersion -notlike "7.5.2*") {
        throw "The running FocusLock is not V7.5.2. Detected file version: $runningVersion"
    }
    Write-Host "UI health check: OK (PID $($uiProcess.Id), version $runningVersion)" -ForegroundColor Green

    Step "DONE"
    Write-Host "FocusLock:      $publishRoot\App\FocusLock.exe" -ForegroundColor Green
    Write-Host "Data:           $publishRoot\Data" -ForegroundColor Green
    Write-Host "Browser add-on: $publishRoot\BrowserExtension" -ForegroundColor Green
    Write-Host "Log:            $logFile" -ForegroundColor Green

    $extensionDir = Join-Path $publishRoot "BrowserExtension"
    try { Start-Process explorer.exe -ArgumentList ('"' + $extensionDir + '"') | Out-Null } catch { }

    $chromeCandidates = @()
    if ($env:ProgramFiles) { $chromeCandidates += (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe") }
    if (${env:ProgramFiles(x86)}) { $chromeCandidates += (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe") }
    if ($env:LOCALAPPDATA) { $chromeCandidates += (Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe") }
    $chrome = $chromeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($chrome) {
        try { Start-Process $chrome -ArgumentList "chrome://extensions/" | Out-Null } catch { }
    }
    else {
        $edgeCandidates = @()
        if ($env:ProgramFiles) { $edgeCandidates += (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe") }
        if (${env:ProgramFiles(x86)}) { $edgeCandidates += (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe") }
        $edge = $edgeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if ($edge) {
            try { Start-Process $edge -ArgumentList "edge://extensions/" | Out-Null } catch { }
        }
    }

    Write-Host ""
    Write-Host "Browser extension: enable Developer mode, click Load unpacked, then choose publish\\BrowserExtension." -ForegroundColor Yellow
}
catch {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "INSTALL FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "Log file: $logFile" -ForegroundColor Yellow
    try { Stop-Transcript | Out-Null } catch { }
    Pop-Location
    exit 1
}

try { Stop-Transcript | Out-Null } catch { }
Pop-Location
exit 0
