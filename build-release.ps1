$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $localDotnet = Join-Path $PSScriptRoot ".tools\dotnet\dotnet.exe"
    if (Test-Path $localDotnet) {
        $dotnet = $localDotnet
    } else {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if (!$cmd) {
            throw ".NET SDK khong tim thay. Hay chay CAI_DAT.bat; file nay se tu tai SDK vao thu muc code."
        }
        $dotnet = $cmd.Source
    }

    function Run-DotNet([string[]]$Arguments) {
        & $dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($Arguments -join ' ') that bai (exit $LASTEXITCODE)."
        }
    }

    # Giu cache/build tren thu muc code neu chay build truc tiep.
    $toolsRoot = Join-Path $PSScriptRoot ".tools"
    foreach ($pair in @{
        DOTNET_CLI_HOME = (Join-Path $toolsRoot "dotnet-home")
        NUGET_PACKAGES = (Join-Path $toolsRoot "nuget")
        TEMP = (Join-Path $toolsRoot "temp")
        TMP = (Join-Path $toolsRoot "temp")
    }.GetEnumerator()) {
        New-Item -ItemType Directory -Path $pair.Value -Force | Out-Null
        Set-Item -Path "Env:$($pair.Key)" -Value $pair.Value
    }
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"

    # Code-folder mode deliberately preserves publish\Data across rebuilds.
    New-Item .\publish -ItemType Directory -Force | Out-Null
    
    # Stop running instances to unlock files before deletion
    Get-Service -Name "FocusLockGuard" -ErrorAction SilentlyContinue | Stop-Service -Force -ErrorAction SilentlyContinue
    Get-Process -Name "FocusLock", "FocusLock.NativeHost", "FocusLock.Service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    foreach ($name in @("App", "Service", "NativeHost", "BrowserExtension")) {
        $path = Join-Path .\publish $name
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        New-Item $path -ItemType Directory -Force | Out-Null
    }
    try {
        if (!(Test-Path .\publish\Data)) { New-Item .\publish\Data -ItemType Directory -Force | Out-Null }
    } catch {
        Write-Host "Data folder dang duoc bao ve; giu nguyen." -ForegroundColor DarkGray
    }

    Run-DotNet @("restore", ".\FocusLock.sln")
    Run-DotNet @("publish", ".\FocusLock.App\FocusLock.App.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", ".\publish\App")
    Run-DotNet @("publish", ".\FocusLock.Service\FocusLock.Service.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", ".\publish\Service")
    Run-DotNet @("publish", ".\FocusLock.NativeHost\FocusLock.NativeHost.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", ".\publish\NativeHost")

    $required = @(
        ".\publish\App\FocusLock.exe",
        ".\publish\Service\FocusLock.Service.exe",
        ".\publish\NativeHost\FocusLock.NativeHost.exe"
    )
    foreach ($file in $required) {
        if (!(Test-Path $file)) { throw "Build thieu file bat buoc: $file" }
    }

    Copy-Item .\BrowserExtension\* .\publish\BrowserExtension -Recurse -Force
    Copy-Item .\install-v5.ps1 .\publish\install-v5.ps1 -Force
    Copy-Item .\uninstall-v5.ps1 .\publish\uninstall-v5.ps1 -Force
    Copy-Item .\README.md .\publish\README.md -Force

    Write-Host ""
    Write-Host "BUILD THANH CONG" -ForegroundColor Green
    Write-Host "  UI:         $PSScriptRoot\publish\App\FocusLock.exe"
    Write-Host "  Service:    $PSScriptRoot\publish\Service\FocusLock.Service.exe"
    Write-Host "  NativeHost: $PSScriptRoot\publish\NativeHost\FocusLock.NativeHost.exe"
    Write-Host "  Data:       $PSScriptRoot\publish\Data"
}
catch {
    Write-Host ""
    Write-Host "BUILD THAT BAI: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally { Pop-Location }
