using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusLock.Shared.Protocol;

namespace FocusLock.App.Services;

/// <summary>
/// Thu thập foreground/idle và chỉ đếm số event input thật.
/// Không lưu virtual-key, scan-code, text, clipboard hay nội dung người dùng nhập.
/// </summary>
public sealed class Win32Activity : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x00000010;
    private const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly string _agentInstanceId = Guid.NewGuid().ToString("N");
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private int _humanInputEvents;
    private uint _lastObservedInputTick;
    private long _sequence;
    private bool _disposed;

    public Win32Activity()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
    }

    public ActivitySample Capture()
    {
        var input = ReadInput();
        var rawInputChanged = input.Tick != _lastObservedInputTick;
        _lastObservedInputTick = input.Tick;

        var humanEvents = Math.Max(0, Interlocked.Exchange(ref _humanInputEvents, 0));
        var monitorHealthy = _keyboardHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;

        var sample = new ActivitySample
        {
            AgentInstanceId = _agentInstanceId,
            Sequence = Interlocked.Increment(ref _sequence),
            IdleMilliseconds = (long)input.Idle.TotalMilliseconds,
            InputChanged = monitorHealthy ? humanEvents > 0 : rawInputChanged,
            HumanInputEvents = Math.Min(humanEvents, 10000),
            InputMonitorHealthy = monitorHealthy,
            ObservedUtc = DateTime.UtcNow
        };

        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return sample;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return sample;
            using var process = Process.GetProcessById((int)pid);
            sample.ProcessId = process.Id;
            sample.ProcessName = process.ProcessName;
            sample.ExePath = TryGetProcessPath(process) ?? "";
        }
        catch { }

        return sample;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLKHF_INJECTED) == 0) Interlocked.Increment(ref _humanInputEvents);
            }
            catch { }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLMHF_INJECTED) == 0) Interlocked.Increment(ref _humanInputEvents);
            }
            catch { }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static (TimeSpan Idle, uint Tick) ReadInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return (TimeSpan.MaxValue, 0);
        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - info.dwTime);
        return (TimeSpan.FromMilliseconds(elapsed), info.dwTime);
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }
}
