param(
    [string]$DotNetExe = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Push-Location $PSScriptRoot
try {
    if ([string]::IsNullOrWhiteSpace($DotNetExe)) {
        $localDotnet = Join-Path $PSScriptRoot ".tools\dotnet\dotnet.exe"
        if (Test-Path -LiteralPath $localDotnet) {
            $DotNetExe = $localDotnet
        }
        else {
            $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
            if (!$cmd) { throw ".NET SDK was not found. Run CAI_DAT.bat." }
            $DotNetExe = $cmd.Source
        }
    }

    if (!(Test-Path -LiteralPath $DotNetExe)) { throw "dotnet.exe was not found: $DotNetExe" }

    function Step([string]$Text) {
        Write-Host ""
        Write-Host "==> $Text" -ForegroundColor Cyan
    }

    function Run-DotNet([string[]]$Arguments) {
        Write-Host ("> dotnet " + ($Arguments -join " ")) -ForegroundColor DarkGray
        & $DotNetExe @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)."
        }
    }

    function Stop-FocusLockRuntime {
        Write-Host "Stopping old FocusLock runtime before file replacement..." -ForegroundColor DarkGray
        $serviceName = "FocusLockGuard"
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc) {
            try {
                if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
                    try { $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(15)) } catch { }
                }
            } catch { }

            # If SCM did not stop it cleanly, terminate only the service process as a fallback.
            try {
                $wmiSvc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
                if ($wmiSvc -and $wmiSvc.State -ne "Stopped" -and [int]$wmiSvc.ProcessId -gt 0) {
                    Stop-Process -Id ([int]$wmiSvc.ProcessId) -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }

        foreach ($name in @("FocusLock", "FocusLock.NativeHost", "FocusLock.Service")) {
            Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }

        # Tray mode can keep the UI alive even after the window is closed.
        # taskkill provides a second hard stop before replacing publish\App.
        & cmd.exe /d /c 'taskkill /F /T /IM "FocusLock.exe" >nul 2>&1'
        & cmd.exe /d /c 'taskkill /F /T /IM "FocusLock.NativeHost.exe" >nul 2>&1'

        # Give Windows a moment to release CLR/native DLL handles.
        for ($i = 0; $i -lt 20; $i++) {
            $stillRunning = @(
                Get-Process -Name "FocusLock.Service" -ErrorAction SilentlyContinue
                Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue
                Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue
            ) | Where-Object { $_ }
            if (!$stillRunning) { break }
            Start-Sleep -Milliseconds 250
            foreach ($p in $stillRunning) { try { $p.Kill() } catch { } }
        }
        Start-Sleep -Milliseconds 750
    }

    function Remove-DirectoryWithRetry([string]$Path) {
        if (!(Test-Path -LiteralPath $Path)) { return }
        $lastError = $null
        for ($attempt = 1; $attempt -le 12; $attempt++) {
            try {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
                return
            }
            catch {
                $lastError = $_
                if ($attempt -eq 4 -or $attempt -eq 8) { Stop-FocusLockRuntime }
                Start-Sleep -Milliseconds 500
            }
        }
        throw "Could not replace '$Path' because Windows is still locking a file. Last error: $($lastError.Exception.Message)"
    }

    $toolsRoot = Join-Path $PSScriptRoot ".tools"
    $envMap = @{
        DOTNET_CLI_HOME = (Join-Path $toolsRoot "dotnet-home")
        NUGET_PACKAGES = (Join-Path $toolsRoot "nuget")
        TEMP = (Join-Path $toolsRoot "temp")
        TMP = (Join-Path $toolsRoot "temp")
    }
    foreach ($pair in $envMap.GetEnumerator()) {
        New-Item -ItemType Directory -Path $pair.Value -Force | Out-Null
        Set-Item -Path "Env:$($pair.Key)" -Value $pair.Value
    }
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"

    # IMPORTANT: build into a staging folder that is never used by the running service.
    # This avoids clrjit.dll / hostfxr.dll locks while restore/publish is happening.
    $stageRoot = Join-Path $PSScriptRoot ".build-v62"
    Remove-DirectoryWithRetry $stageRoot
    foreach ($name in @("App", "Service", "NativeHost", "BrowserExtension")) {
        New-Item -Path (Join-Path $stageRoot $name) -ItemType Directory -Force | Out-Null
    }

    Step "Compiling into a safe staging folder"
    Run-DotNet @("restore", ".\FocusLock.sln")
    Run-DotNet @("publish", ".\FocusLock.App\FocusLock.App.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", (Join-Path $stageRoot "App"))
    Run-DotNet @("publish", ".\FocusLock.Service\FocusLock.Service.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", (Join-Path $stageRoot "Service"))
    Run-DotNet @("publish", ".\FocusLock.NativeHost\FocusLock.NativeHost.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", (Join-Path $stageRoot "NativeHost"))
    Copy-Item ".\BrowserExtension\*" (Join-Path $stageRoot "BrowserExtension") -Recurse -Force

    $stageRequired = @(
        (Join-Path $stageRoot "App\FocusLock.exe"),
        (Join-Path $stageRoot "Service\FocusLock.Service.exe"),
        (Join-Path $stageRoot "NativeHost\FocusLock.NativeHost.exe")
    )
    foreach ($file in $stageRequired) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required staging output is missing: $file" }
    }

    Step "Replacing the installed runtime"
    $stagedAppHash = (Get-FileHash -LiteralPath (Join-Path $stageRoot "App\FocusLock.exe") -Algorithm SHA256).Hash
    Stop-FocusLockRuntime

    $publishRoot = Join-Path $PSScriptRoot "publish"
    New-Item -Path $publishRoot -ItemType Directory -Force | Out-Null
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot "Data"))) {
        New-Item -Path (Join-Path $publishRoot "Data") -ItemType Directory -Force | Out-Null
    }

    # Preserve publish\Data. App/Service/Extension can be replaced normally.
    # NativeHost is deployed to a NEW folder every update because Chrome/Edge may
    # keep old self-contained runtime DLLs open for a while.
    foreach ($name in @("App", "Service", "BrowserExtension")) {
        $final = Join-Path $publishRoot $name
        Remove-DirectoryWithRetry $final
        Move-Item -LiteralPath (Join-Path $stageRoot $name) -Destination $final -Force
    }

    $nativeSlotName = "NativeHost-" + (Get-Date -Format "yyyyMMdd-HHmmss")
    $nativeFinal = Join-Path $publishRoot $nativeSlotName
    Move-Item -LiteralPath (Join-Path $stageRoot "NativeHost") -Destination $nativeFinal -Force
    [System.IO.File]::WriteAllText(
        (Join-Path $publishRoot "nativehost.current"),
        $nativeSlotName,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Copy-Item ".\install-v5.ps1" (Join-Path $publishRoot "install-v5.ps1") -Force
    Copy-Item ".\uninstall-v5.ps1" (Join-Path $publishRoot "uninstall-v5.ps1") -Force
    Copy-Item ".\README.md" (Join-Path $publishRoot "README.md") -Force

    Remove-DirectoryWithRetry $stageRoot

    $required = @(
        (Join-Path $publishRoot "App\FocusLock.exe"),
        (Join-Path $publishRoot "Service\FocusLock.Service.exe"),
        (Join-Path $nativeFinal "FocusLock.NativeHost.exe")
    )
    foreach ($file in $required) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required deployed output is missing: $file" }
    }

    $deployedApp = Join-Path $publishRoot "App\FocusLock.exe"
    $deployedHash = (Get-FileHash -LiteralPath $deployedApp -Algorithm SHA256).Hash
    if ($deployedHash -ne $stagedAppHash) {
        throw "publish\App\FocusLock.exe is NOT the binary that was just built. Runtime replacement failed."
    }

    $deployedVersion = (Get-Item -LiteralPath $deployedApp).VersionInfo.FileVersion
    if ($deployedVersion -notlike "7.5.2*") {
        throw "Wrong FocusLock UI version after deployment: $deployedVersion (expected 7.5.2.0)."
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $publishRoot "runtime.version"),
        "7.5.2",
        (New-Object System.Text.UTF8Encoding($false))
    )

    Write-Host ""
    Write-Host "BUILD + SAFE REPLACE OK - VERIFIED V7.5.2" -ForegroundColor Green
    Write-Host "  UI:         $publishRoot\App\FocusLock.exe"
    Write-Host "  Service:    $publishRoot\Service\FocusLock.Service.exe"
    Write-Host "  NativeHost: $nativeFinal\FocusLock.NativeHost.exe"
    Write-Host "  Data:       $publishRoot\Data"
}
finally {
    Pop-Location
}

# Explicit success: ignore stale exit codes from harmless native helper commands.
$global:LASTEXITCODE = 0
