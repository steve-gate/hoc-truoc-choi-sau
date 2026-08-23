using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace FocusLock.App;

public partial class BubbleWindow : Window
{
    private Point _dragStart;
    private bool _dragging;

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public BubbleWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) => PositionBottomRight();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
    }

    private void MakeNonActivating()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            style |= WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        }
        catch
        {
            // Bubble interaction must never affect the main app.
        }
    }

    private void PositionBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PointToScreen(e.GetPosition(this));
        Left = p.X - _dragStart.X;
        Top = p.Y - _dragStart.Y;
    }

    public void Update(string mode, TimeSpan time, string detail)
        => Update(mode, time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss"), detail);

    public void Update(string mode, string timeText, string detail)
    {
        ModeText.Text = mode;
        TimeText.Text = timeText;
        DetailText.Text = detail;

        // Always reset the main clock and border first so urgency styling
        // cannot leak into the next Focus/normal state.
        TimeText.Foreground = Brush("#182033");
        RootBorder.BorderBrush = Brush("#E4E9F2");
        RootBorder.BorderThickness = new Thickness(1);

        if (mode.Contains("KHÓA SAU", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("HẾT THỜI GIAN", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("COOLDOWN", StringComparison.OrdinalIgnoreCase))
        {
            SetPill("#FDEEEE", "#C64242");
            TimeText.Foreground = Brush("#C64242");
            RootBorder.BorderBrush = Brush("#EAA4A4");
            RootBorder.BorderThickness = new Thickness(2);
        }
        else if (mode.Contains("SẮP KHÓA", StringComparison.OrdinalIgnoreCase))
        {
            SetPill("#FFF2E8", "#D16E32");
            TimeText.Foreground = Brush("#C8692E");
            RootBorder.BorderBrush = Brush("#F0C49F");
            RootBorder.BorderThickness = new Thickness(2);
        }
        else if (mode.Contains("GIẢI TRÍ", StringComparison.OrdinalIgnoreCase))
            SetPill("#FFF2E8", "#D16E32");
        else if (mode.Contains("TẠM DỪNG", StringComparison.OrdinalIgnoreCase))
            SetPill("#F1F3F6", "#6C7485");
        else if (mode.Contains("KIỂM TRA", StringComparison.OrdinalIgnoreCase) || mode.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase))
            SetPill("#FDEEEE", "#C64242");
        else
            SetPill("#EAF3FF", "#2D7FF9");
    }

    private void SetPill(string background, string foreground)
    {
        ModePill.Background = Brush(background);
        ModeText.Foreground = Brush(foreground);
    }

    private static Brush Brush(string value)
        => (Brush)new BrushConverter().ConvertFromString(value)!;
}
