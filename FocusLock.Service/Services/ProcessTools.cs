using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FocusLock.Service.Services;

internal static class ProcessTools
{
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    public static string TrySha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return ""; }
    }

    public static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    public static bool TrySuspend(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            return NtSuspendProcess(process.Handle) == 0;
        }
        catch { return false; }
    }

    public static bool TryResume(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            return NtResumeProcess(process.Handle) == 0;
        }
        catch { return false; }
    }
}
