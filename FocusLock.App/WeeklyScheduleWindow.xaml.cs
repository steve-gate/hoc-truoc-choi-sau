using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FocusLock.Shared.Models;

namespace FocusLock.App;

public partial class WeeklyScheduleWindow : Window
{
    private readonly bool[] _slots = new bool[336];
    private readonly Border[] _cells = new Border[336];
    private bool _painting;
    private bool _paintValue;

    private static readonly string[] Days = { "CN", "T2", "T3", "T4", "T5", "T6", "T7" };
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(42, 45, 52));
    private static readonly Brush EmptyBrush = Brushes.White;
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(225, 230, 238));

    public string ScheduleMask => new(_slots.Select(x => x ? '1' : '0').ToArray());
    public bool ScheduleEnabled => EnabledCheck.IsChecked == true;

    public WeeklyScheduleWindow(BlockProfile profile)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        ProfileText.Text = $"Block Profile: {profile.Name} · {profile.ModeLabel}";
        EnabledCheck.IsChecked = profile.ScheduleEnabled;

        var mask = BlockProfile.IsValidMask(profile.WeeklyScheduleMask)
            ? profile.WeeklyScheduleMask
            : new string('0', 336);
        for (var i = 0; i < _slots.Length; i++) _slots[i] = mask[i] == '1';

        BuildGrid();
        RefreshAll();
        Loaded += (_, _) =>
        {
            var target = Math.Max(0, DateTime.Now.Hour * 50 - 170);
            ScheduleScroll.ScrollToVerticalOffset(target);
        };
        PreviewMouseLeftButtonUp += (_, _) => _painting = false;
        MouseLeave += (_, _) =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) _painting = false;
        };
    }

    private void BuildGrid()
    {
        ScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        for (var d = 0; d < 7; d++)
            ScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 92 });

        ScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        for (var s = 0; s < 48; s++) ScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });

        for (var d = 0; d < 7; d++)
        {
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)),
                BorderBrush = GridBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = Days[d],
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(header, d + 1);
            Grid.SetRow(header, 0);
            ScheduleGrid.Children.Add(header);
        }

        for (var slot = 0; slot < 48; slot++)
        {
            var hour = slot / 2;
            var minute = slot % 2 == 0 ? "00" : "30";
            var time = new TextBlock
            {
                Text = slot % 2 == 0 ? $"{hour:00}:{minute}" : "",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -7, 8, 0)
            };
            Grid.SetColumn(time, 0);
            Grid.SetRow(time, slot + 1);
            ScheduleGrid.Children.Add(time);

            var endMinutes = (slot + 1) * 30;
            var endLabel = endMinutes >= 1440 ? "24:00" : $"{endMinutes / 60:00}:{endMinutes % 60:00}";
            for (var day = 0; day < 7; day++)
            {
                var index = day * 48 + slot;
                var cell = new Border
                {
                    Tag = index,
                    BorderBrush = GridBrush,
                    BorderThickness = new Thickness(0, 0, 1, slot % 2 == 1 ? 1 : 0.45),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{Days[day]} {hour:00}:{minute}–{endLabel}"
                };
                cell.MouseLeftButtonDown += Cell_MouseLeftButtonDown;
                cell.MouseEnter += Cell_MouseEnter;
                _cells[index] = cell;
                Grid.SetColumn(cell, day + 1);
                Grid.SetRow(cell, slot + 1);
                ScheduleGrid.Children.Add(cell);
            }
        }

        // Current-time marker, similar to a calendar app.
        var current = DateTime.Now;
        var currentSlot = current.Hour * 2 + (current.Minute >= 30 ? 1 : 0);
        var currentLine = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        Grid.SetColumn(currentLine, (int)current.DayOfWeek + 1);
        Grid.SetRow(currentLine, currentSlot + 1);
        Panel.SetZIndex(currentLine, 20);
        ScheduleGrid.Children.Add(currentLine);
    }

    private void Cell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        _paintValue = !_slots[index];
        _painting = true;
        SetSlot(index, _paintValue);
        e.Handled = true;
    }

    private void Cell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_painting || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is Border { Tag: int index }) SetSlot(index, _paintValue);
    }

    private void SetSlot(int index, bool value)
    {
        if (index < 0 || index >= _slots.Length) return;
        _slots[index] = value;
        _cells[index].Background = value ? SelectedBrush : EmptyBrush;
        UpdateSummary();
    }

    private void RefreshAll()
    {
        for (var i = 0; i < _slots.Length; i++)
            _cells[i].Background = _slots[i] ? SelectedBrush : EmptyBrush;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _slots.Count(x => x);
        SummaryText.Text = count == 0 ? "Chưa chọn thời gian" : $"{count} ô · {count * 0.5:0.#} giờ/tuần";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Array.Fill(_slots, false);
        RefreshAll();
    }

    private void PresetWeekNights_Click(object sender, RoutedEventArgs e)
    {
        // Monday-Friday 22:00-24:00.
        for (var day = 1; day <= 5; day++)
            for (var slot = 44; slot < 48; slot++)
                _slots[day * 48 + slot] = true;
        EnabledCheck.IsChecked = true;
        RefreshAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleEnabled && !_slots.Any(x => x))
        {
            MessageBox.Show(this, "Bạn đang bật lịch nhưng chưa tô ô thời gian nào.", "Lịch khóa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
