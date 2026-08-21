using System.Threading;
using System.Windows;

namespace FocusLock.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, name: @"Local\FocusLock.V5.Agent", createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("FocusLock V5 đang chạy trong phiên Windows này.", "FocusLock V5");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
