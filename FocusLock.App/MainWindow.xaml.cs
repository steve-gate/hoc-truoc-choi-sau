using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FocusLock.App.Services;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;
using Microsoft.Win32;
using System.IO;

namespace FocusLock.App;

public partial class MainWindow : Window
{
    private readonly ServiceClient _client = new();
    private readonly Win32Activity _sensor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ServiceSnapshot? _snapshot;
    private BubbleWindow? _bubble;
    private bool _busy;
    private string? _lastNewestKey;
    private bool _settingsLoaded;
    private string _statsPeriod = "week";

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await AgentTickAsync();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => { _sensor.Dispose(); _bubble?.Close(); };
    }

    private async Task StartAsync()
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        ApplyResponse(response);

        if (response.Ok && response.Snapshot?.State.Apps.Count == 0)
        {
            var legacy = LegacyStateReader.TryRead();
            if (legacy is not null && legacy.Apps.Count > 0)
            {
                var migrated = await _client.SendAsync(new PipeRequest { Command = "importLegacy", LegacyState = legacy });
                ApplyResponse(migrated);
                FooterText.Text = migrated.Message;
            }
        }

        _timer.Start();
    }

    private async Task AgentTickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var response = await _client.SendAsync(new PipeRequest { Command = "activity", Activity = _sensor.Capture() });
            ApplyResponse(response);
        }
        finally { _busy = false; }
    }

    private void ApplyResponse(PipeResponse response)
    {
        if (response.Snapshot is null) return;
        _snapshot = response.Snapshot;
        var s = _snapshot.State;

        ServiceStatusText.Text = _snapshot.ServiceOnline ? _snapshot.ServiceStatus : "Guard OFFLINE – không có bảo vệ nền";
        CurrentStatusText.Text = _snapshot.CurrentMode;
        CurrentAppText.Text = _snapshot.CurrentApp;
        ActivityScoreText.Text = _snapshot.ActivityEventsLastMinute.ToString();
        HeartbeatText.Text = _snapshot.HeartbeatHealthy ? (_snapshot.InputMonitorHealthy ? "Heartbeat + human input OK" : "Heartbeat OK · input fallback") : "Heartbeat chưa ổn định";

        var target = Math.Max(60, s.Settings.FocusMinutesPerKey * 60);
        FocusProgressBar.Maximum = target;
        FocusProgressBar.Value = Math.Min(target, s.FocusProgressSeconds);
        FocusProgressText.Text = $"{Format(s.FocusProgressSeconds)} / {Format(target)}";
        EntertainmentText.Text = Format(s.EntertainmentBalanceSeconds);
        LockStateText.Text = s.ClockRollbackDetected ? "Khóa do thay đổi giờ" : s.EntertainmentBalanceSeconds > 0 ? "Đã mở khóa" : "Đang khóa";
        TotalFocusText.Text = "Focus " + FormatLong(s.TotalFocusSeconds);
        TotalPlayText.Text = "Giải trí " + FormatLong(s.TotalEntertainmentSeconds);
        SuspiciousText.Text = "Nghi ngờ " + FormatLong(s.SuspiciousSeconds);

        AppsGrid.ItemsSource = s.Apps;
        BrowserRulesGrid.ItemsSource = s.BrowserRules;
        BrowserBridgeStatusText.Text = _snapshot.BrowserBridgeHealthy ? $"✓ {_snapshot.CurrentBrowser} Extension + Native Host đang kết nối" : "⚠ Chưa nhận heartbeat từ Chrome/Edge Extension";
        BrowserCurrentPageText.Text = $"Trang hiện tại: {_snapshot.CurrentBrowserTitle}";
        BrowserCurrentUrlText.Text = string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserUrl) ? "—" : _snapshot.CurrentBrowserUrl;
        BrowserCurrentRuleText.Text = $"Rule: {_snapshot.CurrentBrowserRule} · {_snapshot.CurrentBrowserCategory}{(_snapshot.CurrentBrowserBlocked ? " · ĐANG KHÓA" : "")}";
        KeysGrid.ItemsSource = s.Keys.OrderByDescending(k => k.CreatedUtc).ToList();
        AuditGrid.ItemsSource = s.AuditLog.OrderByDescending(a => a.AtUtc).ToList();
        SessionsGrid.ItemsSource = s.SessionHistory.OrderByDescending(x => x.StartedUtc).Take(100).ToList();

        var newest = s.Keys.OrderByDescending(k => k.CreatedUtc).FirstOrDefault()?.Code;
        if (_lastNewestKey is not null && newest is not null && newest != _lastNewestKey)
            FooterText.Text = $"🎁 Key mới: {newest}";
        _lastNewestKey = newest;

        if (!_settingsLoaded && _snapshot.ServiceOnline)
        {
            LoadSettingsToUi(s.Settings);
            StartupRegistration.Apply(s.Settings.StartWithWindows);
            _settingsLoaded = true;
        }

        RefreshStatistics();
        RefreshBubble();
        if (!response.Ok) FooterText.Text = response.Message;
    }

    private void RefreshStatistics()
    {
        if (_snapshot is null) return;
        var analytics = _snapshot.Analytics;
        var period = _statsPeriod switch
        {
            "today" => analytics.Today,
            "month" => analytics.Month,
            _ => analytics.Week
        };

        StatsPeriodLabel.Text = period.Label;
        StatsFocusText.Text = FormatLong(period.FocusSeconds);
        StatsPlayText.Text = FormatLong(period.EntertainmentSeconds);
        StatsFocusPercentText.Text = $"{period.FocusPercent:0}% thời gian Focus/Play";
        StatsPlayPercentText.Text = $"{period.EntertainmentPercent:0}% thời gian Focus/Play";
        StatsKeyText.Text = $"{period.KeysGenerated} tạo · {period.KeysRedeemed} dùng";
        StatsRewardText.Text = $"{period.KeysExpired} hết hạn · +{FormatLong(period.RewardSecondsGranted)} thưởng";
        StatsStreakText.Text = $"🔥 {analytics.CurrentStreakDays} ngày";
        StatsBestStreakText.Text = $"Kỷ lục {analytics.BestStreakDays} ngày · mục tiêu {analytics.StreakGoalMinutes} phút/ngày";
        StatsSuspiciousText.Text = $"Thời gian nghi ngờ: {FormatLong(period.SuspiciousSeconds)}";

        StatsAppsGrid.ItemsSource = period.Apps.Take(30).Select(x => new AppStatsView
        {
            AppName = x.AppName,
            Category = x.Category,
            Duration = FormatLong(x.Seconds)
        }).ToList();

        var maxSeconds = Math.Max(1L, analytics.Last7Days.SelectMany(x => new[] { x.FocusSeconds, x.EntertainmentSeconds }).DefaultIfEmpty(1L).Max());
        const double maxWidth = 270;
        WeeklyChartItems.ItemsSource = analytics.Last7Days.Select(x => new ChartRowView
        {
            DayLabel = x.DayLabel,
            FocusBarWidth = Math.Max(2, x.FocusSeconds * maxWidth / maxSeconds),
            PlayBarWidth = Math.Max(2, x.EntertainmentSeconds * maxWidth / maxSeconds),
            Summary = $"{CompactDuration(x.FocusSeconds)} / {CompactDuration(x.EntertainmentSeconds)}"
        }).ToList();
    }

    private void StatsPeriodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatsPeriodCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            _statsPeriod = tag;
        RefreshStatistics();
    }

    private void RefreshBubble()
    {
        if (_snapshot is null) return;
        var state = _snapshot.State;
        if (!state.Settings.BubbleEnabled)
        {
            _bubble?.Hide();
            return;
        }

        _bubble ??= new BubbleWindow();
        if (!_bubble.IsVisible) _bubble.Show();
        var isEntertainment = _snapshot.CurrentMode.Contains("giải trí", StringComparison.OrdinalIgnoreCase);
        var target = Math.Max(60, state.Settings.FocusMinutesPerKey * 60);
        var time = isEntertainment ? TimeSpan.FromSeconds(state.EntertainmentBalanceSeconds) : TimeSpan.FromSeconds(Math.Max(0, target - state.FocusProgressSeconds));
        var title = !_snapshot.ServiceOnline ? "⚠ GUARD OFFLINE" : isEntertainment ? "🎮 GIẢI TRÍ" : _snapshot.IsIdle ? "⏸ FOCUS PAUSED" : "🎓 FOCUS";
        _bubble.Update(title, time, $"{_snapshot.CurrentMode} · {_snapshot.CurrentApp}");
    }

    private void AddFocusApp_Click(object sender, RoutedEventArgs e) => _ = AddAppAsync(AppCategory.Focus);
    private void AddEntertainmentApp_Click(object sender, RoutedEventArgs e) => _ = AddAppAsync(AppCategory.Entertainment);

    private async Task AddAppAsync(AppCategory category)
    {
        var dlg = new OpenFileDialog { Filter = "Ứng dụng Windows (*.exe)|*.exe", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        var full = Path.GetFullPath(dlg.FileName);
        var app = TrackedApp.FromPath(full, category, FileHashService.TrySha256(full));
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "addApp", App = app }));
    }

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not TrackedApp app) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "removeApp", AppId = app.Id }));
    }

    private async void ToggleApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not TrackedApp app) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "toggleApp", AppId = app.Id }));
    }

    private void AddBrowserFocusRule_Click(object sender, RoutedEventArgs e) => _ = AddBrowserRuleAsync(AppCategory.Focus);
    private void AddBrowserEntertainmentRule_Click(object sender, RoutedEventArgs e) => _ = AddBrowserRuleAsync(AppCategory.Entertainment);

    private async Task AddBrowserRuleAsync(AppCategory category)
    {
        var pattern = BrowserRulePatternBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(this, "Hãy nhập domain/URL/tiêu đề cần phân loại.", "FocusLock V5");
            return;
        }

        var matchType = GetBrowserMatchType();
        var rule = new BrowserRule
        {
            Name = BrowserRuleNameBox.Text.Trim(),
            Pattern = pattern,
            MatchType = matchType,
            Category = category,
            Enabled = true
        };
        var response = await _client.SendAsync(new PipeRequest { Command = "addBrowserRule", BrowserRule = rule });
        ApplyResponse(response);
        FooterText.Text = response.Message;
        if (response.Ok)
        {
            BrowserRulePatternBox.Clear();
            BrowserRuleNameBox.Clear();
        }
    }

    private BrowserRuleMatchType GetBrowserMatchType()
    {
        if (BrowserMatchTypeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<BrowserRuleMatchType>(tag, true, out var type)) return type;
        return BrowserRuleMatchType.HostSuffix;
    }

    private void UseCurrentBrowserPage_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserUrl))
        {
            MessageBox.Show(this, "Chưa nhận được trang hiện tại từ Browser Extension.", "FocusLock V5");
            return;
        }

        var type = GetBrowserMatchType();
        BrowserRulePatternBox.Text = type switch
        {
            BrowserRuleMatchType.HostSuffix => _snapshot.CurrentBrowserHost == "—" ? "" : _snapshot.CurrentBrowserHost,
            BrowserRuleMatchType.TitleContains => _snapshot.CurrentBrowserTitle == "—" ? "" : _snapshot.CurrentBrowserTitle,
            _ => _snapshot.CurrentBrowserUrl
        };
        if (string.IsNullOrWhiteSpace(BrowserRuleNameBox.Text))
            BrowserRuleNameBox.Text = _snapshot.CurrentBrowserHost == "—" ? _snapshot.CurrentBrowserTitle : _snapshot.CurrentBrowserHost;
    }

    private async void ToggleBrowserRule_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserRulesGrid.SelectedItem is not BrowserRule rule) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "toggleBrowserRule", BrowserRuleId = rule.Id }));
    }

    private async void RemoveBrowserRule_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserRulesGrid.SelectedItem is not BrowserRule rule) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "removeBrowserRule", BrowserRuleId = rule.Id }));
    }

    private void OpenExtensionFolder_Click(object sender, RoutedEventArgs e)
    {
        // Code-folder mode: App lives in <root>\App and the extension lives in <root>\BrowserExtension.
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "BrowserExtension"));
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "Không tìm thấy thư mục BrowserExtension cạnh thư mục App. Hãy chạy build-release.ps1.", "FocusLock V5");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async void RedeemKey_Click(object sender, RoutedEventArgs e)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "redeem", KeyCode = RedeemKeyTextBox.Text.Trim() });
        RedeemResultText.Text = response.Message;
        if (response.Ok) RedeemKeyTextBox.Clear();
        ApplyResponse(response);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(FocusMinutesBox.Text, out var focus) ||
            !TryPositiveInt(RewardMinutesBox.Text, out var reward) ||
            !TryPositiveInt(KeyExpiryBox.Text, out var expiry) ||
            !TryPositiveInt(IdleSecondsBox.Text, out var idle) ||
            !TryPositiveInt(MaxBalanceBox.Text, out var max) ||
            !TryPositiveInt(ActivityEventsBox.Text, out var eventsPerMinute) ||
            !TryPositiveInt(HeartbeatBox.Text, out var heartbeat) ||
            !TryPositiveInt(ClockToleranceBox.Text, out var clockTolerance) ||
            !TryPositiveInt(StreakGoalBox.Text, out var streakGoal) ||
            !TryPositiveInt(RetentionDaysBox.Text, out var retentionDays) ||
            !TryPositiveInt(SessionLimitBox.Text, out var sessionLimit) ||
            !TryPositiveInt(BrowserContextTimeoutBox.Text, out var browserTimeout))
        {
            MessageBox.Show(this, "Các thông số phải là số nguyên dương.", "FocusLock V5");
            return;
        }

        var settings = new UserSettings
        {
            FocusMinutesPerKey = focus,
            RewardMinutesPerKey = reward,
            KeyExpiryMinutes = expiry,
            IdleThresholdSeconds = idle,
            MaxEntertainmentMinutes = max,
            AntiCheatEnabled = AntiCheatCheck.IsChecked == true,
            MinimumActivityEventsPerMinute = eventsPerMinute,
            AgentHeartbeatTimeoutSeconds = heartbeat,
            ClockRollbackToleranceSeconds = clockTolerance,
            VerifyExecutableHash = VerifyHashCheck.IsChecked == true,
            BubbleEnabled = BubbleEnabledCheck.IsChecked == true,
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            StreakMinimumFocusMinutes = streakGoal,
            StatisticsRetentionDays = retentionDays,
            SessionHistoryLimit = sessionLimit,
            BrowserRulesEnabled = BrowserRulesEnabledCheck.IsChecked == true,
            BrowserContextTimeoutSeconds = browserTimeout
        };
        var response = await _client.SendAsync(new PipeRequest { Command = "settings", Settings = settings });
        if (response.Ok) StartupRegistration.Apply(settings.StartWithWindows);
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private void LoadSettingsToUi(UserSettings s)
    {
        if (FocusMinutesBox.IsKeyboardFocusWithin) return;
        FocusMinutesBox.Text = s.FocusMinutesPerKey.ToString();
        RewardMinutesBox.Text = s.RewardMinutesPerKey.ToString();
        KeyExpiryBox.Text = s.KeyExpiryMinutes.ToString();
        IdleSecondsBox.Text = s.IdleThresholdSeconds.ToString();
        MaxBalanceBox.Text = s.MaxEntertainmentMinutes.ToString();
        ActivityEventsBox.Text = s.MinimumActivityEventsPerMinute.ToString();
        HeartbeatBox.Text = s.AgentHeartbeatTimeoutSeconds.ToString();
        ClockToleranceBox.Text = s.ClockRollbackToleranceSeconds.ToString();
        StreakGoalBox.Text = s.StreakMinimumFocusMinutes.ToString();
        RetentionDaysBox.Text = s.StatisticsRetentionDays.ToString();
        SessionLimitBox.Text = s.SessionHistoryLimit.ToString();
        BrowserContextTimeoutBox.Text = s.BrowserContextTimeoutSeconds.ToString();
        AntiCheatCheck.IsChecked = s.AntiCheatEnabled;
        VerifyHashCheck.IsChecked = s.VerifyExecutableHash;
        BubbleEnabledCheck.IsChecked = s.BubbleEnabled;
        StartWithWindowsCheck.IsChecked = s.StartWithWindows;
        BrowserRulesEnabledCheck.IsChecked = s.BrowserRulesEnabled;
    }

    private static bool TryPositiveInt(string text, out int value) => int.TryParse(text, out value) && value > 0;
    private static string Format(int seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    private static string FormatLong(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string CompactDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}";
        return $"{t.Minutes}m";
    }

    private sealed class AppStatsView
    {
        public string AppName { get; init; } = "";
        public string Category { get; init; } = "";
        public string Duration { get; init; } = "";
    }

    private sealed class ChartRowView
    {
        public string DayLabel { get; init; } = "";
        public double FocusBarWidth { get; init; }
        public double PlayBarWidth { get; init; }
        public string Summary { get; init; } = "";
    }
}
