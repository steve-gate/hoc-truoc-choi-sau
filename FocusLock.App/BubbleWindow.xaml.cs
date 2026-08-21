using System.Windows;
using System.Windows.Input;

namespace FocusLock.App;

public partial class BubbleWindow : Window
{
    private Point _dragStart;
    private bool _dragging;

    public BubbleWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionBottomRight();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) => _dragging = false;
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
    {
        ModeText.Text = mode;
        TimeText.Text = time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");
        DetailText.Text = detail;
    }
}
