using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FocusLock.App.Services;

namespace FocusLock.App;

public partial class App : Application
{
    private const string MutexName = @"Local\FocusLock.V5.Agent"; // kept for upgrade compatibility
    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();
        AppCrashLogger.Info($"START pid={Environment.ProcessId} path={Environment.ProcessPath}");

        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
            if (!createdNew)
            {
                AppCrashLogger.Info("SECONDARY INSTANCE detected. Activating existing FocusLock window.");
                if (!TryActivateExistingWindow())
                {
                    MessageBox.Show(
                        "FocusLock đang chạy trong phiên Windows này. Nếu bạn không thấy cửa sổ, hãy kiểm tra Taskbar hoặc Task Manager.",
                        "FocusLock",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            // Create the main window manually so XAML/startup failures are caught and logged.
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            AppCrashLogger.Info("MAIN WINDOW shown successfully.");
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("Fatal error while starting FocusLock", ex);
            MessageBox.Show(
                $"FocusLock không thể khởi động.\n\nĐã ghi lỗi tại:\n{AppCrashLogger.CrashLogPath}\n\n{ex.Message}",
                "FocusLock - lỗi khởi động",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(10);
        }
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppCrashLogger.Exception("AppDomain unhandled exception", ex);
            else
                AppCrashLogger.Info("AppDomain terminated because of a non-Exception error object.");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppCrashLogger.Exception("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppCrashLogger.Exception("WPF dispatcher exception", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"FocusLock vừa gặp lỗi giao diện nhưng đã chặn việc tự tắt.\n\nChi tiết: {AppCrashLogger.CrashLogPath}\n\n{e.Exception.Message}",
                "FocusLock - lỗi",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch { }
    }

    private static bool TryActivateExistingWindow()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("FocusLock"))
            {
                using (process)
                {
                    if (process.Id == currentPid) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppCrashLogger.Info($"EXIT code={e.ApplicationExitCode}");
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
