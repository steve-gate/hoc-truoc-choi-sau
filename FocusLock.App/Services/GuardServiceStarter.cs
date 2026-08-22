using System.Diagnostics;

namespace FocusLock.App.Services;

internal static class GuardServiceStarter
{
    private const string ServiceName = "FocusLockGuard";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastAttemptUtc = DateTime.MinValue;

    public static async Task TryEnsureRunningAsync(CancellationToken cancellationToken)
    {
        // Avoid spawning sc.exe every second while Windows is booting.
        if ((DateTime.UtcNow - _lastAttemptUtc).TotalSeconds < 8)
            return;

        if (!await Gate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            if ((DateTime.UtcNow - _lastAttemptUtc).TotalSeconds < 8)
                return;

            _lastAttemptUtc = DateTime.UtcNow;

            // Primary path. After V6.6 installation, authenticated users have only
            // SERVICE_START/query access to this one service.
            await RunHiddenAsync("sc.exe", $"start {ServiceName}", cancellationToken);

            // Backup path. Installer also creates this SYSTEM task at logon.
            // Running it here is harmless if the service is already running.
            await RunHiddenAsync("schtasks.exe", "/Run /TN \"FocusLock Guard Recovery\"", cancellationToken);
        }
        catch
        {
            // Connection retry in ServiceClient will decide whether Guard recovered.
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task RunHiddenAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch
        {
            // Best effort only. Named-pipe retries provide the final health result.
        }
    }
}
