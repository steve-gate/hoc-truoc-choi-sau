# FocusLock V7.7.9 OneDir first-run registration.
# This script is launched by FocusLock.exe only when the current folder is not registered.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Assert-File([string]$Path, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
}

function Test-GuardPipe {
    param([int]$TimeoutMs = 1500)
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'FocusLock.Guard.V5', [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect($TimeoutMs)
        return $pipe.IsConnected
    }
    catch { return $false }
    finally { if ($null -ne $pipe) { $pipe.Dispose() } }
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceDir = Join-Path $root 'Service'
$nativeDir = Join-Path $root 'NativeHost'
$extensionDir = Join-Path $root 'BrowserExtension'
$dataDir = Join-Path $root 'Data'
$logsDir = Join-Path $root 'Logs'
$serviceExe = Join-Path $serviceDir 'FocusLock.Service.exe'
$appExe = Join-Path $root 'FocusLock.exe'
$nativeExe = Join-Path $nativeDir 'FocusLock.NativeHost.exe'
$nativeManifest = Join-Path $nativeDir 'com.focuslock.browserbridge.json'
$serviceName = 'FocusLockGuard'
$extensionId = 'njmmdgnpjlfkhcngkfbbliondpnfalnb'

try {
    Step 'Checking OneDir files'
    Assert-File $appExe 'FocusLock.exe'
    Assert-File $serviceExe 'FocusLock.Service.exe'
    Assert-File $nativeExe 'FocusLock.NativeHost.exe'
    if (!(Test-Path -LiteralPath $extensionDir -PathType Container)) { throw "BrowserExtension is missing: $extensionDir" }

    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    Step 'Repairing permissions'
    & icacls.exe $root /grant '*S-1-5-18:(OI)(CI)RX' /T /C /Q | Out-Null
    & icacls.exe $dataDir /reset /T /C /Q | Out-Null
    & icacls.exe $dataDir /inheritance:r /T /C /Q | Out-Null
    & icacls.exe $dataDir /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C /Q | Out-Null

    Step 'Registering Browser Bridge'
    $manifestObject = @{
        name = 'com.focuslock.browserbridge'
        description = 'FocusLock Browser Bridge'
        path = $nativeExe
        type = 'stdio'
        allowed_origins = @("chrome-extension://$extensionId/")
    }
    $manifestJson = $manifestObject | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($nativeManifest, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

    $nativeRegistryPaths = @(
        'Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge',
        'Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge'
    )
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            foreach ($relativePath in $nativeRegistryPaths) {
                $subKey = $baseKey.CreateSubKey($relativePath, $true)
                try { $subKey.SetValue('', $nativeManifest, [Microsoft.Win32.RegistryValueKind]::String) }
                finally { $subKey.Dispose() }
            }
        }
        finally { $baseKey.Dispose() }
    }

    Step 'Installing / updating Guard Service'
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
        & sc.exe config $serviceName binPath= ('"' + $serviceExe + '"') start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not update FocusLockGuard service path.' }
    }
    else {
        New-Service -Name $serviceName -BinaryPathName ('"' + $serviceExe + '"') -DisplayName 'FocusLock Guard' -StartupType Automatic | Out-Null
    }

    & sc.exe description $serviceName 'FocusLock enforcement service - OneDir edition.' | Out-Null
    & sc.exe config $serviceName start= auto | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/5000/restart/10000 | Out-Null
    & sc.exe failureflag $serviceName 1 | Out-Null

    # Normal users may query/interrogate/start this one service, but not stop/delete/reconfigure it.
    try {
        $sdLines = @(& sc.exe sdshow $serviceName 2>$null)
        $sd = ($sdLines | Where-Object { $_ -match '^D:' } | Select-Object -First 1)
        $startAce = '(A;;LCSWRPLO;;;AU)'
        if (![string]::IsNullOrWhiteSpace($sd) -and !$sd.Contains($startAce)) {
            $saclIndex = $sd.IndexOf('S:')
            if ($saclIndex -ge 0) { $newSd = $sd.Substring(0, $saclIndex) + $startAce + $sd.Substring($saclIndex) }
            else { $newSd = $sd + $startAce }
            & sc.exe sdset $serviceName $newSd | Out-Null
        }
    } catch { Write-Warning "Could not update service start permission: $($_.Exception.Message)" }

    try {
        $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\sc.exe') -Argument "start $serviceName"
        $triggers = @((New-ScheduledTaskTrigger -AtStartup), (New-ScheduledTaskTrigger -AtLogOn))
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
        Register-ScheduledTask -TaskName 'FocusLock Guard Recovery' -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null
    } catch { Write-Warning "Could not create recovery task: $($_.Exception.Message)" }

    Start-Service -Name $serviceName -ErrorAction Stop
    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    try { $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15)) } catch { }
    $svc.Refresh()
    if ($svc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) { throw 'FocusLockGuard did not reach Running state.' }

    $pipeOk = $false
    for ($i=0; $i -lt 10; $i++) {
        if (Test-GuardPipe 1200) { $pipeOk = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$pipeOk) {
        Restart-Service -Name $serviceName -Force -ErrorAction Stop
        Start-Sleep -Seconds 2
        for ($i=0; $i -lt 10; $i++) {
            if (Test-GuardPipe 1200) { $pipeOk = $true; break }
            Start-Sleep -Milliseconds 500
        }
    }
    if (!$pipeOk) { throw 'FocusLockGuard is running but its Named Pipe is not reachable.' }

    Step 'Registering startup path'
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'FocusLock' -Value ('"' + $appExe + '"') -PropertyType String -Force | Out-Null

    Write-Host ''
    Write-Host 'FOCUSLOCK ONEDIR READY' -ForegroundColor Green
    Write-Host "App:       $appExe"
    Write-Host "Service:   $serviceExe"
    Write-Host "Data:      $dataDir"
    Write-Host "Extension: $extensionDir"
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'FOCUSLOCK ONEDIR SETUP FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    try {
        New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
        "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $($_.Exception.ToString())" | Out-File -LiteralPath (Join-Path $logsDir 'onedir-install.log') -Append -Encoding utf8
    } catch { }
    exit 1
}
