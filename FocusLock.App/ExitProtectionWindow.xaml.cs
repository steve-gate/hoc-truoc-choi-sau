using System.Windows;
using System.Windows.Controls;
using FocusLock.App.Services;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;

namespace FocusLock.App;

public partial class ExitProtectionWindow : Window
{
    private readonly ServiceClient _client = new();
    private ServiceSnapshot? _snapshot;
    private string? _editingId;

    public ExitProtectionWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            PrepareNewEditor();
            await RefreshAsync();
        };
    }

    private ExitProtectionSchedule? SelectedSchedule => SchedulesList.SelectedItem as ExitProtectionSchedule;

    private async Task RefreshAsync(string? selectId = null)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        if (!response.Ok || response.Snapshot is null)
        {
            StatusText.Text = response.Message;
            return;
        }

        _snapshot = response.Snapshot;
        var schedules = _snapshot.State.ControlPolicy.ExitProtectionSchedules
            .OrderByDescending(x => x.IsActiveNow)
            .ThenBy(x => x.CreatedUtc)
            .ToList();
        SchedulesList.ItemsSource = schedules;

        var active = schedules.Where(x => x.IsActiveNow)
            .OrderByDescending(x => x.ActiveUntilLocal ?? DateTime.MaxValue)
            .FirstOrDefault();
        if (active is not null)
        {
            ActiveWarningBorder.Visibility = Visibility.Visible;
            var until = active.ActiveUntilLocal;
            ActiveWarningText.Text = until is DateTime value
                ? $"🔒 '{active.Name}' đang khóa thoát tới {value:dd/MM/yyyy HH:mm}. Trong lúc này không thể sửa/xóa lịch hoặc cấu hình."
                : $"🔒 '{active.Name}' đang khóa thoát.";
            EditorPanel.IsEnabled = false;
        }
        else
        {
            ActiveWarningBorder.Visibility = Visibility.Collapsed;
            EditorPanel.IsEnabled = true;
        }

        if (!string.IsNullOrWhiteSpace(selectId))
            SchedulesList.SelectedItem = schedules.FirstOrDefault(x => x.Id == selectId);
        else if (_editingId is not null)
            SchedulesList.SelectedItem = schedules.FirstOrDefault(x => x.Id == _editingId);

        if (SchedulesList.SelectedItem is null && schedules.Count > 0)
            SchedulesList.SelectedIndex = 0;

        StatusText.Text = $"{schedules.Count} lịch · có thể tạo nhiều lịch và để chúng chồng nhau.";
    }

    private void NewSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.State.ControlPolicy.ExitProtectionActive == true)
        {
            MessageBox.Show(this, "Đang có lịch không thể tắt hoạt động. Hãy chờ khung hiện tại kết thúc.", "FocusLock", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SchedulesList.SelectedItem = null;
        PrepareNewEditor();
    }

    private void PrepareNewEditor()
    {
        _editingId = null;
        NameBox.Text = "Không thể tắt FocusLock";
        TypeCombo.SelectedIndex = 1;
        StartTimeBox.Text = "08:00";
        EndTimeBox.Text = "17:00";
        var now = DateTime.Now;
        OneTimeStartDate.SelectedDate = now.Date;
        OneTimeStartTime.Text = now.AddMinutes(5).ToString("HH:mm");
        OneTimeEndDate.SelectedDate = now.Date;
        OneTimeEndTime.Text = now.AddHours(2).ToString("HH:mm");
        MonCheck.IsChecked = TueCheck.IsChecked = WedCheck.IsChecked = ThuCheck.IsChecked = FriCheck.IsChecked = true;
        SatCheck.IsChecked = SunCheck.IsChecked = false;
        ToggleButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        UpdatePanels();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(_editingId);

    private void SchedulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedSchedule is not { } schedule) return;
        _editingId = schedule.Id;
        NameBox.Text = schedule.Name;
        TypeCombo.SelectedIndex = schedule.Type switch
        {
            ExitProtectionScheduleType.OneTime => 0,
            ExitProtectionScheduleType.Daily => 1,
            _ => 2
        };
        StartTimeBox.Text = schedule.StartTime;
        EndTimeBox.Text = schedule.EndTime;
        if (schedule.OneTimeStartUtc is DateTime start)
        {
            var local = start.ToLocalTime();
            OneTimeStartDate.SelectedDate = local.Date;
            OneTimeStartTime.Text = local.ToString("HH:mm");
        }
        if (schedule.OneTimeEndUtc is DateTime end)
        {
            var local = end.ToLocalTime();
            OneTimeEndDate.SelectedDate = local.Date;
            OneTimeEndTime.Text = local.ToString("HH:mm");
        }

        SunCheck.IsChecked = schedule.IsDaySelected(0);
        MonCheck.IsChecked = schedule.IsDaySelected(1);
        TueCheck.IsChecked = schedule.IsDaySelected(2);
        WedCheck.IsChecked = schedule.IsDaySelected(3);
        ThuCheck.IsChecked = schedule.IsDaySelected(4);
        FriCheck.IsChecked = schedule.IsDaySelected(5);
        SatCheck.IsChecked = schedule.IsDaySelected(6);
        ToggleButton.IsEnabled = true;
        DeleteButton.IsEnabled = true;
        UpdatePanels();
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        var type = SelectedType();
        OneTimePanel.Visibility = type == ExitProtectionScheduleType.OneTime ? Visibility.Visible : Visibility.Collapsed;
        RecurringPanel.Visibility = type == ExitProtectionScheduleType.OneTime ? Visibility.Collapsed : Visibility.Visible;
        WeeklyDaysPanel.Visibility = type == ExitProtectionScheduleType.Weekly ? Visibility.Visible : Visibility.Collapsed;
    }

    private ExitProtectionScheduleType SelectedType()
    {
        var tag = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "OneTime" => ExitProtectionScheduleType.OneTime,
            "Weekly" => ExitProtectionScheduleType.Weekly,
            _ => ExitProtectionScheduleType.Daily
        };
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split('|');
        if (parts.Length != 2) return;
        StartTimeBox.Text = parts[0];
        EndTimeBox.Text = parts[1];
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var schedule = BuildScheduleFromEditor();
            var startsNow = schedule.IsActive(DateTime.Now, DateTime.UtcNow);
            if (startsNow)
            {
                var until = schedule.GetActiveUntilLocal(DateTime.Now, DateTime.UtcNow);
                var endText = until is DateTime value ? value.ToString("dd/MM/yyyy HH:mm") : "khi khung kết thúc";
                var confirm = MessageBox.Show(this,
                    $"Khung này đang có hiệu lực NGAY BÂY GIỜ. Sau khi lưu, bạn sẽ không thể tắt FocusLock hoặc sửa/xóa lịch cho tới {endText}.\n\nTiếp tục?",
                    "Bắt đầu khóa thoát ngay", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            var response = await _client.SendAsync(new PipeRequest
            {
                Command = "saveExitProtectionSchedule",
                ExitProtectionSchedule = schedule
            });
            StatusText.Text = response.Message;
            if (!response.Ok)
            {
                MessageBox.Show(this, response.Message, "Không thể lưu lịch", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshAsync(_editingId);
                return;
            }

            _editingId = schedule.Id;
            await RefreshAsync(schedule.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Lịch không thể tắt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private ExitProtectionSchedule BuildScheduleFromEditor()
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Hãy đặt tên cho lịch.");

        var existing = SelectedSchedule;
        var schedule = new ExitProtectionSchedule
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            CreatedUtc = existing?.CreatedUtc ?? DateTime.UtcNow,
            Name = name,
            Enabled = existing?.Enabled ?? true,
            Type = SelectedType(),
            StartTime = StartTimeBox.Text.Trim(),
            EndTime = EndTimeBox.Text.Trim(),
            WeeklyDaysMask = BuildWeeklyMask()
        };

        if (schedule.Type == ExitProtectionScheduleType.OneTime)
        {
            if (OneTimeStartDate.SelectedDate is not DateTime startDate ||
                OneTimeEndDate.SelectedDate is not DateTime endDate ||
                !TimeOnly.TryParse(OneTimeStartTime.Text.Trim(), out var startClock) ||
                !TimeOnly.TryParse(OneTimeEndTime.Text.Trim(), out var endClock))
                throw new InvalidOperationException("Ngày/giờ một lần không hợp lệ. Giờ ví dụ: 08:00.");

            var startLocal = DateTime.SpecifyKind(startDate.Date.Add(startClock.ToTimeSpan()), DateTimeKind.Local);
            var endLocal = DateTime.SpecifyKind(endDate.Date.Add(endClock.ToTimeSpan()), DateTimeKind.Local);
            if (endLocal <= startLocal) throw new InvalidOperationException("Thời gian kết thúc phải sau thời gian bắt đầu.");
            schedule.OneTimeStartUtc = startLocal.ToUniversalTime();
            schedule.OneTimeEndUtc = endLocal.ToUniversalTime();
        }
        else
        {
            if (!ExitProtectionSchedule.TryParseClock(schedule.StartTime, out var startClock) ||
                !ExitProtectionSchedule.TryParseClock(schedule.EndTime, out var endClock))
                throw new InvalidOperationException("Giờ phải theo dạng HH:mm, ví dụ 08:00 hoặc 22:30.");
            if (startClock == endClock)
                throw new InvalidOperationException("Giờ bắt đầu và kết thúc không được giống nhau.");
            schedule.StartTime = startClock.ToString("HH:mm");
            schedule.EndTime = endClock.ToString("HH:mm");
            if (schedule.Type == ExitProtectionScheduleType.Weekly && !ExitProtectionSchedule.IsValidWeeklyMask(schedule.WeeklyDaysMask))
                throw new InvalidOperationException("Lịch theo thứ phải chọn ít nhất một ngày.");
        }

        return schedule;
    }

    private string BuildWeeklyMask()
    {
        return new string(new[]
        {
            SunCheck.IsChecked == true ? '1' : '0',
            MonCheck.IsChecked == true ? '1' : '0',
            TueCheck.IsChecked == true ? '1' : '0',
            WedCheck.IsChecked == true ? '1' : '0',
            ThuCheck.IsChecked == true ? '1' : '0',
            FriCheck.IsChecked == true ? '1' : '0',
            SatCheck.IsChecked == true ? '1' : '0'
        });
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSchedule is not { } schedule) return;
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "toggleExitProtectionSchedule",
            ExitProtectionScheduleId = schedule.Id
        });
        StatusText.Text = response.Message;
        if (!response.Ok)
            MessageBox.Show(this, response.Message, "Không thể thay đổi lịch", MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshAsync(schedule.Id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSchedule is not { } schedule) return;
        var confirm = MessageBox.Show(this, $"Xóa lịch '{schedule.Name}'?", "Xóa lịch", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "removeExitProtectionSchedule",
            ExitProtectionScheduleId = schedule.Id
        });
        StatusText.Text = response.Message;
        if (!response.Ok)
        {
            MessageBox.Show(this, response.Message, "Không thể xóa lịch", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync(schedule.Id);
            return;
        }
        PrepareNewEditor();
        await RefreshAsync();
    }
}
