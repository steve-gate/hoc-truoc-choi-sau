using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FocusLock.App.Services;
using FocusLock.Shared.Protocol;

namespace FocusLock.App;

public partial class App : Application
{
    private const string MutexName = @"Local\FocusLock.V5.Agent"; // kept for upgrade compatibility
    private const string ActivationEventName = @"Local\FocusLock.V5.ShowWindow";
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;
    private bool _quietWatchdogExit;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();
        var ensureScheduledRun = e.Args.Any(x => string.Equals(x, "--ensure-scheduled", StringComparison.OrdinalIgnoreCase));
        if (!ensureScheduledRun)
            AppCrashLogger.Info($"START pid={Environment.ProcessId} path={Environment.ProcessPath}");

        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
            if (!createdNew)
            {
                if (ensureScheduledRun)
                {
                    // The minute watchdog must stay completely silent when the real
                    // UI is already alive; it must not steal focus or show dialogs.
                    _quietWatchdogExit = true;
                    Shutdown(0);
                    return;
                }

                AppCrashLogger.Info("SECONDARY INSTANCE detected. Activating existing FocusLock window.");
                if (!SignalExistingInstance() && !TryActivateExistingWindow())
                {
                    MessageBox.Show(
                        "FocusLock đang chạy trong phiên Windows này nhưng chưa thể đưa cửa sổ ra trước. Hãy thử biểu tượng FocusLock ở khay hệ thống.",
                        "FocusLock",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                Shutdown(0);
                return;
            }

            StartActivationListener();

            if (ensureScheduledRun && !ProtectedWindowRequiresUi())
            {
                _quietWatchdogExit = true;
                Shutdown(0);
                return;
            }

            base.OnStartup(e);
            if (ensureScheduledRun)
                AppCrashLogger.Info("WATCHDOG restored FocusLock UI because a protected window is active.");

            // OneDir: the UI itself performs first-run registration of Guard/Native Host.
            // After the first successful setup, future launches do not require elevation.
            if (!OneDirBootstrapper.EnsureReady())
            {
                Shutdown(20);
                return;
            }

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

    private static bool ProtectedWindowRequiresUi()
    {
        try
        {
            var client = new ServiceClient();
            var response = client.SendAsync(new PipeRequest { Command = "snapshot" }, timeoutMs: 900)
                .GetAwaiter().GetResult();
            return response.Ok && response.Snapshot?.ExitProtectionActive == true;
        }
        catch
        {
            // The watchdog is intentionally quiet. Normal user launches still run the
            // OneDir bootstrapper and can repair an unavailable Guard.
            return false;
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

    private void StartActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationCancellation = new CancellationTokenSource();
            var token = _activationCancellation.Token;
            var handle = _activationEvent;

            _ = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!handle.WaitOne(500)) continue;
                        if (token.IsCancellationRequested) break;
                        Dispatcher.BeginInvoke(BringMainWindowToFront);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        AppCrashLogger.Exception("Activation event listener", ex);
                        break;
                    }
                }
            }, token);
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("Could not create activation event", ex);
        }
    }

    private static bool SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activation = EventWaitHandle.OpenExisting(ActivationEventName);
                return activation.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void BringMainWindowToFront()
    {
        try
        {
            var window = MainWindow ?? Windows.OfType<Window>().FirstOrDefault();
            if (window is null) return;

            window.ShowInTaskbar = true;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
            AppCrashLogger.Info("Existing hidden/tray window restored by activation event.");
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("BringMainWindowToFront", ex);
        }
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
        if (!_quietWatchdogExit)
            AppCrashLogger.Info($"EXIT code={e.ApplicationExitCode}");
        try { _activationCancellation?.Cancel(); } catch { }
        try { _activationEvent?.Set(); } catch { }
        try { _activationEvent?.Dispose(); } catch { }
        _activationEvent = null;
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
