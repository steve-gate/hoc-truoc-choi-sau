# FocusLock V7.8.0.2 - safe cleanup for obsolete OneDir folders.
# Launched by FocusLock.exe with Administrator privileges.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path)).TrimEnd('\')
$parent = [System.IO.Path]::GetFullPath((Split-Path -Parent $root)).TrimEnd('\')
$logs = Join-Path $root 'Logs'
$reportPath = Join-Path $logs 'onedir-cleanup-last.txt'
$currentData = Join-Path $root 'Data'
$currentBackupDir = Join-Path $currentData 'Backups'
$serviceName = 'FocusLockGuard'
$nativeHostName = 'com.focuslock.browserbridge'

New-Item -ItemType Directory -Path $logs -Force | Out-Null
New-Item -ItemType Directory -Path $currentBackupDir -Force | Out-Null

$deleted = New-Object System.Collections.Generic.List[string]
$blocked = New-Object System.Collections.Generic.List[string]
$preserved = 0

function Normalize-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))).TrimEnd('\') }
    catch { return $null }
}

function Test-PathUnder([string]$Path, [string]$Folder) {
    $p = Normalize-Path $Path
    $f = Normalize-Path $Folder
    if ([string]::IsNullOrWhiteSpace($p) -or [string]::IsNullOrWhiteSpace($f)) { return $false }
    if ($p.Equals($f, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $p.StartsWith($f + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Extract-Exe([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }
    $v = [Environment]::ExpandEnvironmentVariables($CommandLine.Trim())
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -gt 1) { return $v.Substring(1, $end - 1) }
    }
    $idx = $v.IndexOf('.exe', [System.StringComparison]::OrdinalIgnoreCase)
    if ($idx -ge 0) { return $v.Substring(0, $idx + 4).Trim() }
    return $v
}

function Get-ServiceExe {
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$serviceName")
        if ($null -eq $key) { return $null }
        try { return Extract-Exe ([string]$key.GetValue('ImagePath')) }
        finally { $key.Dispose() }
    } catch { return $null }
}

function Get-NativeManifestPaths {
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($sub in @(
        "Software\Google\Chrome\NativeMessagingHosts\$nativeHostName",
        "Software\Microsoft\Edge\NativeMessagingHosts\$nativeHostName"
    )) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($sub)
            if ($null -ne $key) {
                try {
                    $v = [string]$key.GetValue($null)
                    if (![string]::IsNullOrWhiteSpace($v)) { $result.Add($v) }
                } finally { $key.Dispose() }
            }
        } catch { }
    }
    return $result
}

function Get-WatchdogExecutables {
    $result = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($taskName in @('FocusLock Protected Window Watchdog')) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($null -ne $task) {
                foreach ($action in $task.Actions) {
                    if (![string]::IsNullOrWhiteSpace([string]$action.Execute)) { $result.Add([string]$action.Execute) }
                }
            }
        }
    } catch { }
    return $result
}

function Get-RunningExecutablePaths {
    $result = New-Object System.Collections.Generic.List[string]
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object {
            if (![string]::IsNullOrWhiteSpace([string]$_.ExecutablePath)) { $result.Add([string]$_.ExecutablePath) }
        }
    } catch { }
    return $result
}

function Test-CandidateHasNewerState([string]$Candidate) {
    $oldState = Join-Path $Candidate 'Data\state.v2.json'
    if (!(Test-Path -LiteralPath $oldState -PathType Leaf)) { return $false }
    $newState = Join-Path $currentData 'state.v2.json'
    if (!(Test-Path -LiteralPath $newState -PathType Leaf)) { return $true }
    try {
        $oldTime = (Get-Item -LiteralPath $oldState).LastWriteTimeUtc
        $newTime = (Get-Item -LiteralPath $newState).LastWriteTimeUtc
        return $oldTime -gt $newTime.AddSeconds(5)
    } catch { return $true }
}

function Preserve-PortableBackups([string]$Candidate) {
    $data = Join-Path $Candidate 'Data'
    if (!(Test-Path -LiteralPath $data -PathType Container)) { return }
    Get-ChildItem -LiteralPath $data -Filter '*.focuslockbackup' -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $dest = Join-Path $currentBackupDir $_.Name
        if (Test-Path -LiteralPath $dest -PathType Leaf) {
            $stem = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            $ext = [System.IO.Path]::GetExtension($_.Name)
            $dest = Join-Path $currentBackupDir ("$stem-old-$(Get-Date -Format 'yyyyMMdd-HHmmssfff')$ext")
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        $script:preserved++
    }
}

try {
    $serviceExe = Get-ServiceExe
    $nativePaths = @(Get-NativeManifestPaths)
    $watchdogPaths = @(Get-WatchdogExecutables)
    $runningPaths = @(Get-RunningExecutablePaths)

    $candidates = @(Get-ChildItem -LiteralPath $parent -Directory -ErrorAction Stop |
        Where-Object { $_.Name -like 'FocusLock-OneDir*' } |
        Sort-Object FullName)

    foreach ($dir in $candidates) {
        $candidate = Normalize-Path $dir.FullName
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ($candidate.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        # Never recurse into junctions/symlinks/reparse points.
        if (($dir.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $blocked.Add("$($dir.Name): bỏ qua vì là junction/symlink")
            continue
        }

        $reason = $null
        if (Test-PathUnder $serviceExe $candidate) {
            $reason = 'Guard Service vẫn trỏ vào thư mục này'
        }
        elseif (@($nativePaths | Where-Object { Test-PathUnder $_ $candidate }).Count -gt 0) {
            $reason = 'Native Host registry vẫn trỏ vào thư mục này'
        }
        elseif (@($watchdogPaths | Where-Object { Test-PathUnder $_ $candidate }).Count -gt 0) {
            $reason = 'watchdog vẫn trỏ vào thư mục này'
        }
        elseif (@($runningPaths | Where-Object { Test-PathUnder $_ $candidate }).Count -gt 0) {
            $reason = 'vẫn có tiến trình đang chạy từ thư mục này'
        }
        elseif (Test-CandidateHasNewerState $candidate) {
            $reason = 'Data/state.v2.json của bản cũ mới hơn bản hiện tại; giữ lại để tránh mất dữ liệu'
        }

        if ($null -ne $reason) {
            $blocked.Add("$($dir.Name): $reason")
            continue
        }

        try {
            Preserve-PortableBackups $candidate
            try { & takeown.exe /F $candidate /R /D Y | Out-Null } catch { }
            try { & icacls.exe $candidate /grant "*S-1-5-32-544:(OI)(CI)F" /T /C /Q | Out-Null } catch { }
            try { & icacls.exe $candidate /grant "$($env:USERNAME):(OI)(CI)F" /T /C /Q | Out-Null } catch { }
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction Stop
            $deleted.Add($dir.Name)
        }
        catch {
            $blocked.Add("$($dir.Name): xóa thất bại - $($_.Exception.Message)")
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    if ($deleted.Count -gt 0) { $lines.Add("Đã xóa: $($deleted -join ', ')") }
    else { $lines.Add('Không có bản OneDir cũ nào đủ điều kiện để xóa.') }
    $lines.Add("Đã giữ lại $preserved file .focuslockbackup từ các bản cũ.")
    if ($blocked.Count -gt 0) {
        $lines.Add('Giữ lại:')
        foreach ($item in $blocked) { $lines.Add("- $item") }
    }
    $lines.Add("Bản hiện tại luôn được giữ nguyên: $root")

    $text = $lines -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($reportPath, $text, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host $text
    exit 0
}
catch {
    $text = "Dọn OneDir thất bại: $($_.Exception.Message)"
    try { [System.IO.File]::WriteAllText($reportPath, $text, (New-Object System.Text.UTF8Encoding($true))) } catch { }
    Write-Host $text -ForegroundColor Red
    exit 1
}
