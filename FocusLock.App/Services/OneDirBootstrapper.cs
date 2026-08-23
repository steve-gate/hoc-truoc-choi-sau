using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;

namespace FocusLock.App.Services;

internal static class OneDirBootstrapper
{
    private const string ServiceName = "FocusLockGuard";
    private const string NativeHostName = "com.focuslock.browserbridge";

    public static bool EnsureReady()
    {
        var root = GetRootDirectory();
        var serviceExe = Path.Combine(root, "Service", "FocusLock.Service.exe");
        var nativeExe = Path.Combine(root, "NativeHost", "FocusLock.NativeHost.exe");
        var extensionDir = Path.Combine(root, "BrowserExtension");
        var installScript = Path.Combine(root, "Install-OneDir.ps1");

        if (!File.Exists(serviceExe) || !File.Exists(nativeExe) || !Directory.Exists(extensionDir))
        {
            MessageBox.Show(
                "Bộ FocusLock OneDir không đầy đủ. Cần có Service, NativeHost và BrowserExtension nằm cùng thư mục với FocusLock.exe.",
                "FocusLock OneDir",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (!NeedsInstall(root, serviceExe))
            return true;

        if (!File.Exists(installScript))
        {
            MessageBox.Show(
                "Thiếu Install-OneDir.ps1. Hãy giải nén lại đầy đủ thư mục FocusLock OneDir.",
                "FocusLock OneDir",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        var answer = MessageBox.Show(
            "FocusLock cần đăng ký Guard Service và Browser Bridge cho thư mục OneDir này.\n\nWindows sẽ hỏi quyền Administrator một lần. Tiếp tục?",
            "Thiết lập FocusLock OneDir",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return false;

        try
        {
            var quotedScript = installScript.Replace("\"", "\\\"");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{quotedScript}\"",
                WorkingDirectory = root,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            });

            if (process is null)
                throw new InvalidOperationException("Không thể mở trình thiết lập OneDir.");

            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Thiết lập OneDir thất bại (mã {process.ExitCode}).");

            return !NeedsInstall(root, serviceExe);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(
                "Bạn đã hủy yêu cầu quyền Administrator nên FocusLock chưa thể đăng ký Guard Service.",
                "FocusLock OneDir",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("OneDir bootstrap", ex);
            MessageBox.Show(
                $"Không thể hoàn tất thiết lập OneDir.\n\n{ex.Message}\n\nLog: {AppCrashLogger.CrashLogPath}",
                "FocusLock OneDir",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    public static string GetRootDirectory()
    {
        var baseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var current = new DirectoryInfo(baseDir);
        if (current.Name.Equals("App", StringComparison.OrdinalIgnoreCase) && current.Parent is not null)
            return current.Parent.FullName;
        return current.FullName;
    }

    private static bool NeedsInstall(string root, string expectedServiceExe)
    {
        try
        {
            var installedService = ReadServiceExecutable();
            if (!PathEquals(installedService, expectedServiceExe))
                return true;

            var expectedManifest = Path.Combine(root, "NativeHost", $"{NativeHostName}.json");
            var chromeManifest = ReadNativeManifestPath(@"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName);
            var edgeManifest = ReadNativeManifestPath(@"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName);

            if (!PathEquals(chromeManifest, expectedManifest) || !PathEquals(edgeManifest, expectedManifest))
                return true;

            if (!File.Exists(expectedManifest))
                return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string? ReadServiceExecutable()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        var raw = key?.GetValue("ImagePath")?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return ExtractExecutable(raw);
    }

    private static string? ReadNativeManifestPath(string registryPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return key?.GetValue(null)?.ToString();
    }

    private static string ExtractExecutable(string commandLine)
    {
        var value = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1) return value[1..end];
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0) return value[..(exeIndex + 4)].Trim();
        return value;
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
