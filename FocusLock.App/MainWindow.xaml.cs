using System.Windows.Documents;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FocusLock.App.Services;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;
using Microsoft.Win32;

using FocusLock.Shared.Utilities;
namespace FocusLock.App;

public partial class MainWindow : Window
{
    private readonly ServiceClient _client = new();
    private readonly Win32Activity _sensor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ServiceSnapshot? _snapshot;
    private bool _busy;
    private BubbleWindow? _bubble;
    private string? _lastNewestKey;
    private bool _settingsLoaded;
    private string _statsPeriod = "week";
    private int _onboardingStep;
    private string _appsFingerprint = "";
    private string _profilesFingerprint = "";
    private string _profilePolicyFingerprint = "";
    private string _rulesFingerprint = "";
    private string _keysFingerprint = "";
    private string _focusSessionProfileFingerprint = "";
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private bool _allowExit;

    private sealed class ProfileCardViewModel
    {
        public required BlockProfile Profile { get; init; }
        public string Name => Profile.Name;
        public string StatusLabel => Profile.StatusLabel;
        public required string MembershipSummary { get; init; }
        public string PolicySummary => Profile.PolicySummary;
        public string ScheduleSummary => Profile.ScheduleLabel;
        public string AllowanceSummary => Profile.AllowanceLabel;
        public string DailyBudgetSummary => Profile.DailyBudgetLabel;
        public string CooldownSummary => Profile.CooldownLabel;
        public string RewardRuleSummary => Profile.RewardRuleLabel;
        public string RewardProgressSummary => Profile.RewardProgressLabel;
    }

    private sealed class FocusSessionProfileOption
    {
        public string Id { get; init; } = "";
        public BlockProfile? Profile { get; init; }
        public string Label { get; init; } = "";
    }

    private readonly (string Title, string Subtitle)[] _pages =
    {
        ("Trang chủ", "Tập trung đúng việc, rồi tận hưởng phần thưởng của bạn."),
        ("Ứng dụng", "Chia ứng dụng thành hai nhóm rõ ràng: học/làm việc và giải trí."),
        ("Website", "Phân loại website theo cách đơn giản; quy tắc nâng cao chỉ dùng khi cần."),
        ("Phần thưởng", "Xem và sử dụng thời gian giải trí bạn đã kiếm được."),
        ("Thống kê", "Theo dõi tiến độ theo ngày, tuần và tháng."),
        ("Kiểm soát", "Lịch khóa, Strict Mode, allowance và các phiên tập trung không thể hủy."),
        ("Cài đặt", "Quy tắc cơ bản ở trên; kỹ thuật và chống gian lận nằm trong Nâng cao.")
    };

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        _timer.Tick += async (_, _) => await AgentTickAsync();
        Loaded += async (_, _) => await StartAsync();
        Closing += MainWindow_Closing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && (_snapshot?.State.Settings.MinimizeToTray ?? true))
                HideToTray();
        };
        System.Windows.Application.Current.SessionEnding += (_, _) => _allowExit = true;

        DataObject.AddPastingHandler(SettingsChallengeInput, (_, e) => e.CancelCommand());
        SettingsChallengeInput.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.V) e.Handled = true;
        };
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayMenu = new System.Windows.Forms.ContextMenuStrip();
            _trayMenu.Items.Add("Mở FocusLock", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            _trayMenu.Items.Add("Ẩn cửa sổ", null, (_, _) => Dispatcher.Invoke(HideToTray));
            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _trayMenu.Items.Add("Thoát hoàn toàn", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "FocusLock — Học trước · chơi sau",
                Icon = System.Drawing.SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("InitializeTrayIcon", ex);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit && (_snapshot?.State.Settings.MinimizeToTray ?? true))
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        try { _timer.Stop(); } catch { }
        _sensor.Dispose();
        try { _bubble?.Close(); } catch { }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void HideToTray()
    {
        if (!IsVisible) return;
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task StartAsync()
    {
        try
        {
            AppCrashLogger.Info("MainWindow StartAsync begin");
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

            if (_snapshot?.ServiceOnline == true && !_snapshot.State.Settings.OnboardingCompleted)
                ShowOnboarding();

            AppCrashLogger.Info($"MainWindow StartAsync ready; serviceOnline={_snapshot?.ServiceOnline}");
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("MainWindow StartAsync", ex);
            FooterText.Text = @"Có lỗi khi khởi động giao diện. Xem publish\Logs\crash.log.";
        }
        finally
        {
            _timer.Start();
        }
    }

    private async Task AgentTickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var response = await _client.SendAsync(
                new PipeRequest { Command = "activity", Activity = _sensor.Capture() });
            ApplyResponse(response);
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("AgentTickAsync", ex);
            FooterText.Text = @"Agent gặp lỗi tạm thời; FocusLock vẫn mở. Xem Logs\crash.log.";
        }
        finally
        {
            _busy = false;
        }
    }

    private void ApplyResponse(PipeResponse response)
    {
        if (response.Snapshot is null) return;
        _snapshot = response.Snapshot;
        var s = _snapshot.State;

        // Beginner-facing system state. Technical details live under Settings > Diagnostics.
        if (!_snapshot.ServiceOnline)
        {
            ServiceStatusText.Text = "Cần kiểm tra";
            SidebarStatusText.Text = "Bảo vệ nền đang offline";
            ServiceStatusDot.Fill = BrushOf("DangerBrush");
            SidebarStatusDot.Fill = BrushOf("DangerBrush");
        }
        else if (!_snapshot.HeartbeatHealthy)
        {
            ServiceStatusText.Text = "Đang khởi động";
            SidebarStatusText.Text = "Đang kết nối tác nhân người dùng";
            ServiceStatusDot.Fill = BrushOf("WarningBrush");
            SidebarStatusDot.Fill = BrushOf("WarningBrush");
        }
        else
        {
            ServiceStatusText.Text = "Đang bảo vệ";
            SidebarStatusText.Text = "Hệ thống hoạt động bình thường";
            ServiceStatusDot.Fill = BrushOf("SuccessBrush");
            SidebarStatusDot.Fill = BrushOf("SuccessBrush");
        }

        CurrentStatusText.Text = FriendlyMode(_snapshot.CurrentMode, _snapshot.IsIdle);
        CurrentAppText.Text = string.IsNullOrWhiteSpace(_snapshot.CurrentApp) ? "—" : _snapshot.CurrentApp;
        ActivityScoreText.Text = _snapshot.ActivityEventsLastMinute.ToString();
        HeartbeatText.Text = _snapshot.HeartbeatHealthy
            ? (_snapshot.InputMonitorHealthy ? "Ổn định · input thật OK" : "Ổn định · đang dùng fallback")
            : "Chưa ổn định";

        var focusSessionActive = s.ControlPolicy.FocusSessionActive;
        var currentRewardTarget = _snapshot.CurrentFocusRewardTargetSeconds > 0
            ? _snapshot.CurrentFocusRewardTargetSeconds
            : Math.Max(60, s.Settings.FocusMinutesPerKey * 60);
        var currentRewardProgress = _snapshot.CurrentFocusRewardTargetSeconds > 0
            ? Math.Clamp(_snapshot.CurrentFocusRewardProgressSeconds, 0, currentRewardTarget)
            : Math.Min(currentRewardTarget, s.FocusProgressSeconds);
        var currentRewardSeconds = _snapshot.CurrentFocusRewardSecondsPerKey > 0
            ? _snapshot.CurrentFocusRewardSecondsPerKey
            : Math.Max(60, s.Settings.RewardMinutesPerKey * 60);

        var target = focusSessionActive
            ? Math.Max(1, s.ControlPolicy.FocusSessionTargetSeconds)
            : currentRewardTarget;
        var progress = focusSessionActive
            ? Math.Clamp(s.ControlPolicy.FocusSessionQualifiedSeconds, 0, target)
            : currentRewardProgress;

        FocusProgressBar.Maximum = target;
        FocusProgressBar.Value = progress;
        FocusProgressText.Text = $"{Format(progress)} / {Format(target)}";

        var remaining = Math.Max(0, target - progress);
        HomeFocusRemainingText.Text = focusSessionActive
            ? $"Focus Session{(string.IsNullOrWhiteSpace(s.ControlPolicy.FocusSessionProfileName) ? "" : " · " + s.ControlPolicy.FocusSessionProfileName)} · còn {HumanDuration(remaining)} Focus thực · hoàn thành nhận key +{Format(s.ControlPolicy.FocusSessionRewardSeconds)}"
            : remaining == 0
                ? "Đã đủ điều kiện nhận phần thưởng"
                : $"Còn {HumanDuration(remaining)} · {_snapshot.CurrentFocusRewardProfileName} → key +{Format(currentRewardSeconds)}";

        EntertainmentText.Text = Format(s.EntertainmentBalanceSeconds);
        RewardBalanceText.Text = Format(s.EntertainmentBalanceSeconds);
        LockStateText.Text = s.ClockRollbackDetected
            ? "Đang khóa vì phát hiện thay đổi giờ hệ thống"
            : s.ControlPolicy.FocusSessionActive
                ? $"Focus Session · còn {HumanDuration(s.ControlPolicy.FocusSessionRemainingSeconds)} Focus thực"
                : s.ControlPolicy.LockedSessionActive
                    ? $"Locked Session · tới {s.ControlPolicy.LockedSessionUntilUtc!.Value.ToLocalTime():HH:mm:ss}"
                    : s.ControlPolicy.WhitelistSessionActive
                        ? $"Focus-only · tới {s.ControlPolicy.WhitelistSessionUntilUtc!.Value.ToLocalTime():HH:mm:ss}"
                        : s.EntertainmentBalanceSeconds > 0 ? "Sẵn sàng sử dụng" : "Đang khóa · hãy Focus để nhận thưởng";
        RewardRuleSummaryText.Text = _snapshot.CurrentFocusRewardTargetSeconds > 0
            ? $"{_snapshot.CurrentFocusRewardProfileName}: {Math.Max(1, _snapshot.CurrentFocusRewardTargetSeconds / 60)} phút Focus → +{Math.Max(1, _snapshot.CurrentFocusRewardSecondsPerKey / 60)} phút"
            : $"Công thức chung: {s.Settings.FocusMinutesPerKey} phút Focus → +{s.Settings.RewardMinutesPerKey} phút";

        TotalFocusText.Text = "Focus " + FormatLong(s.TotalFocusSeconds);
        TotalPlayText.Text = "Giải trí " + FormatLong(s.TotalEntertainmentSeconds);
        SuspiciousText.Text = "Nghi ngờ " + FormatLong(s.SuspiciousSeconds);

        // Applications: split into two beginner-friendly lists. Avoid rebinding every second so scrolling stays stable.
        var appsFingerprint = string.Join("|", s.Apps.OrderBy(a => a.Id).Select(a => $"{a.Id}:{a.Category}:{a.Enabled}:{a.Name}:{a.ExePath}:{a.BlockAction}:{a.UseCustomBlockAction}:{a.BlockProfileId}:{a.BlockProfileName}"));
        if (!string.Equals(appsFingerprint, _appsFingerprint, StringComparison.Ordinal))
        {
            _appsFingerprint = appsFingerprint;
            var focusApps = s.Apps.Where(a => a.Category == AppCategory.Focus).OrderBy(a => a.Name).ToList();
            var playApps = s.Apps.Where(a => a.Category == AppCategory.Entertainment).OrderBy(a => a.Name).ToList();
            FocusAppsList.ItemsSource = focusApps;
            EntertainmentAppsList.ItemsSource = playApps;
            FocusAppsEmptyText.Visibility = focusApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EntertainmentAppsEmptyText.Visibility = playApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppsGrid.ItemsSource = s.Apps; // compatibility for old handlers
        }

        var profilesFingerprint = string.Join("|", s.BlockProfiles.OrderBy(p => p.CreatedUtc).Select(p =>
            $"{p.Id}:{p.Name}:{p.Enabled}:{p.PolicyVersion}:{p.DefaultAccessPolicy}:{p.ScheduledAccessPolicy}:{p.ScheduleEnabled}:{p.WeeklyScheduleMask}:{p.DailyAllowanceMinutes}:{p.AllowanceDateKey}:{p.AllowanceUsedSeconds}:{p.DailyBudgetMinutes}:{p.EntertainmentUsageDateKey}:{p.EntertainmentUsedSecondsToday}:{p.CooldownEnabled}:{p.CooldownAfterMinutes}:{p.CooldownMinutes}:{p.CooldownProgressSeconds}:{(p.CooldownUntilUtc?.ToString("O") ?? "")}:{p.CooldownRemainingSeconds}:{p.CustomRewardEnabled}:{p.RewardFocusMinutes}:{p.RewardMinutes}:{p.RewardProgressSeconds}:{p.DefaultBlockAction}:" +
            $"EA{s.Apps.Count(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)}:EW{s.BrowserRules.Count(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)}:" +
            $"FA{s.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id)}:FW{s.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id)}"));
        if (!string.Equals(profilesFingerprint, _profilesFingerprint, StringComparison.Ordinal))
        {
            _profilesFingerprint = profilesFingerprint;
            _profilePolicyFingerprint = profilesFingerprint;
            var cards = s.BlockProfiles.OrderBy(p => p.CreatedUtc).Select(p => new ProfileCardViewModel
            {
                Profile = p,
                MembershipSummary =
                    $"Giải trí: {s.Apps.Count(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)} app · {s.BrowserRules.Count(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)} web · " +
                    $"Focus: {s.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id)} app · {s.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id)} web"
            }).ToList();
            BlockProfilesItems.ItemsSource = cards;
            ProfilePolicyItems.ItemsSource = cards;
        }

        RefreshControlPolicy(s);

        // Browser rules: simple first, advanced optional.
        var rulesFingerprint = string.Join("|", s.BrowserRules.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.Category}:{r.Enabled}:{r.MatchType}:{r.Pattern}:{r.Name}:{r.BlockProfileId}:{r.BlockProfileName}"));
        if (!string.Equals(rulesFingerprint, _rulesFingerprint, StringComparison.Ordinal))
        {
            _rulesFingerprint = rulesFingerprint;
            var focusRules = s.BrowserRules.Where(r => r.Category == AppCategory.Focus).OrderBy(r => r.DisplayName).ToList();
            var playRules = s.BrowserRules.Where(r => r.Category == AppCategory.Entertainment).OrderBy(r => r.DisplayName).ToList();
            BrowserFocusRulesList.ItemsSource = focusRules;
            BrowserEntertainmentRulesList.ItemsSource = playRules;
            BrowserFocusEmptyText.Visibility = focusRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BrowserEntertainmentEmptyText.Visibility = playRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BrowserRulesGrid.ItemsSource = s.BrowserRules;
        }

        BrowserBridgeStatusText.Text = _snapshot.BrowserBridgeHealthy
            ? $"✓ {_snapshot.CurrentBrowser} đã kết nối"
            : "Extension chưa kết nối";
        BrowserCurrentPageText.Text = _snapshot.CurrentBrowserTitle is "" or "—"
            ? "Trang hiện tại: —"
            : _snapshot.CurrentBrowserTitle;
        BrowserCurrentUrlText.Text = string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserUrl) ? "—" : _snapshot.CurrentBrowserUrl;
        BrowserCurrentRuleText.Text = _snapshot.CurrentBrowserRule is "" or "—"
            ? "Chưa khớp quy tắc nào"
            : $"{FriendlyCategory(_snapshot.CurrentBrowserCategory)} · {_snapshot.CurrentBrowserRule}{(_snapshot.CurrentBrowserBlocked ? " · Đang khóa" : "")}";
        BrowserFocusSignalText.Text = !_snapshot.BrowserBridgeHealthy
            ? "Website Focus 2.0: Extension chưa gửi dữ liệu"
            : _snapshot.CurrentBrowserCategory.Equals("Focus", StringComparison.OrdinalIgnoreCase)
                ? _snapshot.BrowserFocusQualified
                    ? _snapshot.BrowserMediaProgressing
                        ? "✓ Đang cộng Focus · video/audio đang phát thật"
                        : $"✓ Đang cộng Focus · {_snapshot.BrowserActivityEventsLastMinute} hoạt động web/phút"
                    : "⏸ Chưa cộng Focus · click/gõ/cuộn để mở 90 giây đọc chủ động, hoặc phát video/audio"
                : _snapshot.CurrentBrowserBlocked
                    ? "🔒 Website giải trí đang bị khóa"
                    : "Website Focus: trang hiện tại không thuộc nhóm học";

        BrowserChargeStatusText.Text = _snapshot.CurrentBrowserCategory.Equals("Giải trí", StringComparison.OrdinalIgnoreCase)
            ? $"Giải trí web · Profile: {_snapshot.CurrentBrowserProfile} · {_snapshot.CurrentBrowserAccess} · Ví {Format(s.EntertainmentBalanceSeconds)}" +
              (_snapshot.CurrentBrowserAllowanceRemainingSeconds > 0 ? $" · Allowance còn {Format(_snapshot.CurrentBrowserAllowanceRemainingSeconds)}" : "")
            : "Giải trí web: —";

        var keys = s.Keys.OrderByDescending(k => k.CreatedUtc).ToList();
        var activeKeys = keys.Where(k => !k.IsRedeemed && !k.IsExpired && !k.Revoked).ToList();
        var keysFingerprint = string.Join("|", keys.Select(k => $"{k.Id}:{k.RedeemedUtc:O}:{k.Revoked}:{k.ExpiresUtc:O}:{k.IsExpired}:{k.RemainingLabel}"));
        if (!string.Equals(keysFingerprint, _keysFingerprint, StringComparison.Ordinal))
        {
            _keysFingerprint = keysFingerprint;
            KeysGrid.ItemsSource = keys;
            ActiveRewardItems.ItemsSource = activeKeys;
        }
        ActiveRewardCountText.Text = $"{activeKeys.Count} phần thưởng";
        NoRewardsText.Visibility = activeKeys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeRewardText.Text = activeKeys.Count == 0 ? "0 khả dụng" : $"{activeKeys.Count} khả dụng";

        AuditGrid.ItemsSource = s.AuditLog.OrderByDescending(a => a.AtUtc).ToList();
        SessionsGrid.ItemsSource = s.SessionHistory.OrderByDescending(x => x.StartedUtc).Take(100).ToList();

        var newestKey = keys.FirstOrDefault();
        var newest = newestKey?.Code;
        if (_lastNewestKey is not null && newestKey is not null && newest != _lastNewestKey)
            FooterText.Text = $"🎁 Có phần thưởng mới: {newestKey.RewardLabel}";
        _lastNewestKey = newest;

        if (!_settingsLoaded && _snapshot.ServiceOnline)
        {
            LoadSettingsToUi(s.Settings);
            StartupRegistration.Apply(s.Settings.StartWithWindows);
            _settingsLoaded = true;
        }

        RefreshStatistics();
        UpdateOnboardingCounts();
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
        StatsFocusPercentText.Text = $"{period.FocusPercent:0}% của Focus/Play";
        StatsPlayPercentText.Text = $"{period.EntertainmentPercent:0}% của Focus/Play";
        StatsKeyText.Text = $"{period.KeysGenerated} tạo · {period.KeysRedeemed} dùng";
        StatsRewardText.Text = $"{period.KeysExpired} hết hạn · +{FormatLong(period.RewardSecondsGranted)}";
        StatsStreakText.Text = $"🔥 {analytics.CurrentStreakDays} ngày";
        StatsBestStreakText.Text = $"Kỷ lục {analytics.BestStreakDays} ngày";
        StatsSuspiciousText.Text = $"Thời gian bị đánh dấu nghi ngờ: {FormatLong(period.SuspiciousSeconds)}";

        HomeTodayFocusText.Text = FormatLong(analytics.Today.FocusSeconds);
        HomeTodayPlayText.Text = FormatLong(analytics.Today.EntertainmentSeconds);
        HomeStreakText.Text = $"{analytics.CurrentStreakDays} ngày";

        StatsAppsGrid.ItemsSource = period.Apps.Take(30).Select(x => new AppStatsView
        {
            AppName = x.AppName,
            Category = FriendlyCategory(x.Category),
            Duration = FormatLong(x.Seconds)
        }).ToList();

        var maxSeconds = Math.Max(1L, analytics.Last7Days.SelectMany(x => new[] { x.FocusSeconds, x.EntertainmentSeconds }).DefaultIfEmpty(1L).Max());
        const double maxWidth = 320;
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
        try
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
            var target = _snapshot.CurrentFocusRewardTargetSeconds > 0
                ? _snapshot.CurrentFocusRewardTargetSeconds
                : Math.Max(60, state.Settings.FocusMinutesPerKey * 60);
            if (!_snapshot.ServiceOnline)
            {
                _bubble.Update("⚠ CẦN KIỂM TRA", "--:--", "FocusLock Guard chưa sẵn sàng");
                return;
            }

            var browserEntertainmentActive = _snapshot.BrowserForegroundActive &&
                                             _snapshot.CurrentBrowserCategory.Equals("Giải trí", StringComparison.OrdinalIgnoreCase);
            if (_snapshot.EntertainmentSessionActive || browserEntertainmentActive)
            {
                var fromBrowserFallback = !_snapshot.EntertainmentSessionActive && browserEntertainmentActive;
                var access = fromBrowserFallback ? _snapshot.CurrentBrowserAccess : _snapshot.EntertainmentAccessMode;
                var profile = fromBrowserFallback ? _snapshot.CurrentBrowserProfile : _snapshot.EntertainmentProfileName;
                var allowance = fromBrowserFallback ? _snapshot.CurrentBrowserAllowanceRemainingSeconds : _snapshot.EntertainmentAllowanceRemainingSeconds;
                var wallet = _snapshot.EntertainmentWalletRemainingSeconds;
                var cooldownRemaining = fromBrowserFallback
                    ? _snapshot.CurrentBrowserCooldownRemainingSeconds
                    : _snapshot.EntertainmentCooldownRemainingSeconds;
                var locked = access.Contains("khóa", StringComparison.OrdinalIgnoreCase);
                var free = access.Contains("tự do", StringComparison.OrdinalIgnoreCase) ||
                           access.Contains("miễn phí", StringComparison.OrdinalIgnoreCase);

                string title;
                string timeText;
                string detail;

                if (locked)
                {
                    if (cooldownRemaining > 0)
                    {
                        title = "⏸ COOLDOWN";
                        timeText = Format(cooldownRemaining);
                        detail = $"{profile} · nghỉ bắt buộc trước khi giải trí lại";
                    }
                    else
                    {
                        title = "🔒 HẾT THỜI GIAN";
                        timeText = "00:00";
                        detail = $"{profile} · quyền giải trí hiện đang bị khóa";
                    }
                }
                else
                {
                    var dailyBudgetRemaining = fromBrowserFallback
                        ? _snapshot.CurrentBrowserDailyBudgetRemainingSeconds
                        : _snapshot.EntertainmentDailyBudgetRemainingSeconds;
                    var hasDailyBudget = dailyBudgetRemaining != int.MaxValue;

                    // Free skips wallet/allowance payment, but it does NOT bypass
                    // a configured daily budget.
                    if (free && !hasDailyBudget)
                    {
                        title = "🎮 GIẢI TRÍ";
                        timeText = "TỰ DO";
                        var currentTarget = !string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserHost) && _snapshot.CurrentBrowserHost != "—"
                            ? _snapshot.CurrentBrowserHost
                            : _snapshot.CurrentApp;
                        detail = $"{profile} · {currentTarget}";
                        _bubble.Update(title, timeText, detail);
                        return;
                    }

                    var sourceRemain = free
                        ? int.MaxValue
                        : fromBrowserFallback
                            ? access.Contains("allowance", StringComparison.OrdinalIgnoreCase)
                                ? SafeUiRemainingSum(allowance, wallet)
                                : Math.Max(0, wallet)
                            : Math.Max(0, _snapshot.EntertainmentUsableRemainingSeconds);

                    var remain = hasDailyBudget
                        ? Math.Min(sourceRemain, Math.Max(0, dailyBudgetRemaining))
                        : sourceRemain;

                    var warning = Math.Clamp(state.Settings.LockCountdownWarningSeconds, 5, 600);
                    var critical = Math.Clamp(
                        state.Settings.LockCountdownCriticalSeconds,
                        1,
                        Math.Max(1, warning - 1));

                    if (state.Settings.LockCountdownEnabled && remain <= critical)
                    {
                        title = "⚠ KHÓA SAU";
                        detail = $"{profile} · còn {remain} giây trước khi Guard khóa giải trí";
                    }
                    else if (state.Settings.LockCountdownEnabled && remain <= warning)
                    {
                        title = "⏳ SẮP KHÓA";
                        detail = $"{profile} · sắp hết quyền giải trí";
                    }
                    else
                    {
                        title = "🎮 GIẢI TRÍ";
                        if (free && hasDailyBudget)
                        {
                            detail = $"Ngân sách ngày còn {Format(dailyBudgetRemaining)} · {profile}";
                        }
                        else
                        {
                            detail = access.Contains("allowance", StringComparison.OrdinalIgnoreCase)
                                ? $"Allowance {Format(allowance)} + ví {Format(wallet)} · {profile}"
                                : $"Ví Focus còn {Format(wallet)} · {profile}";

                            if (hasDailyBudget)
                                detail += $" · trần ngày còn {Format(dailyBudgetRemaining)}";
                        }
                    }

                    timeText = Format(remain);
                }

                _bubble.Update(title, timeText, detail);
                return;
            }

            var sessionActive = state.ControlPolicy.FocusSessionActive;
            var focusRemainingSeconds = sessionActive
                ? Math.Max(0, state.ControlPolicy.FocusSessionRemainingSeconds)
                : Math.Max(
                    0,
                    target - (_snapshot.CurrentFocusRewardTargetSeconds > 0
                        ? _snapshot.CurrentFocusRewardProgressSeconds
                        : state.FocusProgressSeconds));
            var focusTime = TimeSpan.FromSeconds(focusRemainingSeconds);
            var mode = _snapshot.CurrentMode ?? "";
            var browserFocusActive =
                _snapshot.BrowserForegroundActive &&
                _snapshot.CurrentBrowserCategory.Equals("Focus", StringComparison.OrdinalIgnoreCase) &&
                _snapshot.BrowserFocusQualified;
            var desktopFocusActive =
                mode.Contains("Đang học / làm việc", StringComparison.OrdinalIgnoreCase);
            var focusActive = !_snapshot.IsIdle && (browserFocusActive || desktopFocusActive);

            if (focusActive)
            {
                var detailTarget = browserFocusActive && !string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserHost)
                    ? _snapshot.CurrentBrowserHost
                    : _snapshot.CurrentApp;
                var sessionBoundToOtherProfile =
                    sessionActive &&
                    !string.IsNullOrWhiteSpace(state.ControlPolicy.FocusSessionProfileId) &&
                    !string.Equals(
                        state.ControlPolicy.FocusSessionProfileId,
                        _snapshot.CurrentFocusRewardProfileId,
                        StringComparison.Ordinal);

                if (sessionBoundToOtherProfile)
                {
                    _bubble.Update(
                        "⏸ FOCUS SESSION · SAI PROFILE",
                        focusTime,
                        $"Phiên cần nguồn Focus thuộc {state.ControlPolicy.FocusSessionProfileName}; hiện tại là {_snapshot.CurrentFocusRewardProfileName}");
                }
                else
                {
                    var rewardSeconds = _snapshot.CurrentFocusRewardSecondsPerKey > 0
                        ? _snapshot.CurrentFocusRewardSecondsPerKey
                        : Math.Max(60, state.Settings.RewardMinutesPerKey * 60);

                    _bubble.Update(
                        sessionActive ? "● FOCUS SESSION" : "● ĐANG TẬP TRUNG",
                        focusTime,
                        sessionActive
                            ? $"Còn Focus thực · hoàn thành nhận key +{Format(state.ControlPolicy.FocusSessionRewardSeconds)} · {detailTarget}"
                            : $"{_snapshot.CurrentFocusRewardProfileName} · đủ mốc nhận key +{Format(rewardSeconds)} · {detailTarget}");
                }
            }
            else
            {
                var pauseReason = _snapshot.IsIdle
                    ? "Không có hoạt động · thời gian không được cộng"
                    : mode.Contains("hoạt động quá thấp", StringComparison.OrdinalIgnoreCase)
                        ? "Hoạt động quá thấp · Focus đang tạm dừng"
                        : mode.Contains("chờ tương tác", StringComparison.OrdinalIgnoreCase)
                            ? "Website cần click/gõ/cuộn hoặc media đang phát"
                            : $"{FriendlyMode(mode, _snapshot.IsIdle)} · {_snapshot.CurrentApp}";
                _bubble.Update(
                    sessionActive ? "⏸ FOCUS SESSION TẠM DỪNG" : "⏸ TẠM DỪNG",
                    focusTime,
                    sessionActive ? $"Phiên không tiến · {pauseReason}" : pauseReason);
            }
        }
        catch (Exception ex)
        {
            // A bubble/XAML problem must never be allowed to close the main app.
            AppCrashLogger.Exception("RefreshBubble", ex);
            try { _bubble?.Close(); } catch { }
            _bubble = null;
        }
    }

    // Navigation -----------------------------------------------------------------
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out var index)) NavigateTo(index);
    }

    private void NavigateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var index)) NavigateTo(index);
    }

    private void NavigateTo(int index)
    {
        if (index < 0 || index >= MainTabs.Items.Count) return;
        MainTabs.SelectedIndex = index;
        var nav = new[] { NavHome, NavApps, NavWeb, NavRewards, NavStats, NavControl, NavSettings };
        if (index < nav.Length) nav[index].IsChecked = true;
        PageTitleText.Text = _pages[index].Title;
        PageSubtitleText.Text = _pages[index].Subtitle;
    }

    // Applications ----------------------------------------------------------------
    private void AddFocusApp_Click(object sender, RoutedEventArgs e) => _ = AddAppAsync(AppCategory.Focus);
    private void AddEntertainmentApp_Click(object sender, RoutedEventArgs e) => _ = AddAppAsync(AppCategory.Entertainment);

    private async Task AddAppAsync(AppCategory category)
    {
        var dlg = new OpenFileDialog { Filter = "Ứng dụng Windows (*.exe)|*.exe", Multiselect = false, Title = category == AppCategory.Focus ? "Chọn ứng dụng học/làm việc" : "Chọn ứng dụng giải trí" };
        if (dlg.ShowDialog(this) != true) return;
        await AddAppPathAsync(dlg.FileName, category);
    }

    private async Task AddAppPathAsync(string path, AppCategory category)
    {
        var full = Path.GetFullPath(path);
        var app = TrackedApp.FromPath(full, category, FileHashService.TrySha256(full));
        var response = await _client.SendAsync(new PipeRequest { Command = "addApp", App = app });
        ApplyResponse(response);
        FooterText.Text = response.Ok ? $"Đã thêm {app.Name} vào {FriendlyCategory(category.ToString())}." : response.Message;
        if (!response.Ok)
            MessageBox.Show(this, response.Message, "Không thể thêm ứng dụng", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AddRunningFocusApp_Click(object sender, RoutedEventArgs e) => _ = AddRunningAppAsync(AppCategory.Focus);
    private void AddRunningEntertainmentApp_Click(object sender, RoutedEventArgs e) => _ = AddRunningAppAsync(AppCategory.Entertainment);

    private async Task AddRunningAppAsync(AppCategory category)
    {
        var picker = new RunningAppsWindow(category) { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
        await AddAppPathAsync(picker.SelectedPath, category);
    }

    private async void ToggleAppCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: TrackedApp app }) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "toggleApp", AppId = app.Id }));
    }

    private async void CycleAppBlockAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: TrackedApp app }) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "cycleAppLock", AppId = app.Id });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void CycleAppProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: TrackedApp app }) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "cycleAppProfile", AppId = app.Id });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void EditAppPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || sender is not Button { CommandParameter: TrackedApp app }) return;
        var dialog = new AppPolicyWindow(app, _snapshot.State.BlockProfiles) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = app.Id, BlockProfileId = dialog.SelectedProfileId }));
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "setAppBlockAction", AppId = app.Id, UseCustomBlockAction = dialog.UseCustomBlockAction, BlockAction = dialog.SelectedBlockAction
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private void OpenProfileCenter_Click(object sender, RoutedEventArgs e)
    {
        var center = new ProfileCenterWindow { Owner = this };
        center.ShowDialog();
    }

    private async void AddBlockProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = NewBlockProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Nhập tên profile, ví dụ Game, Mạng xã hội hoặc Video.", "Block Profiles");
            return;
        }
        var access = ProfileAccessPolicy.EarnedTime;
        if (NewBlockProfileModeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<ProfileAccessPolicy>(tag, true, out var parsedAccess))
            access = parsedAccess;

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "addBlockProfile",
            BlockProfile = new BlockProfile
            {
                Name = name, PolicyVersion = 2, DefaultAccessPolicy = access,
                ScheduledAccessPolicy = ProfileAccessPolicy.Block, DefaultBlockAction = EntertainmentBlockAction.Close
            }
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
        if (response.Ok) NewBlockProfileNameBox.Clear();
    }

    private async void ToggleBlockProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "toggleBlockProfile", BlockProfileId = profile.Id });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void RemoveBlockProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        var confirm = MessageBox.Show(this, $"Xóa profile {profile.Name}? Ứng dụng trong profile sẽ tự chuyển sang profile khác.", "Xóa Block Profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "removeBlockProfile", BlockProfileId = profile.Id });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void EditBlockProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || sender is not Button { CommandParameter: BlockProfile profile }) return;
        var editor = new ProfileEditorWindow(profile, _snapshot.State.Apps, _snapshot.State.BrowserRules) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var response = await _client.SendAsync(new PipeRequest { Command = "updateBlockProfile", BlockProfile = editor.EditedProfile });
        ApplyResponse(response);
        if (!response.Ok) { FooterText.Text = response.Message; return; }

        var otherProfile = _snapshot?.State.BlockProfiles.FirstOrDefault(p => p.Id != profile.Id && string.Equals(p.Name, "Giải trí chung", StringComparison.OrdinalIgnoreCase))
                           ?? _snapshot?.State.BlockProfiles.FirstOrDefault(p => p.Id != profile.Id);
        foreach (var member in editor.AppMembers)
        {
            if (member.IsMember)
            {
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = member.Id, BlockProfileId = profile.Id }));
            }
            else if (member.WasMember && otherProfile is not null)
            {
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = member.Id, BlockProfileId = otherProfile.Id }));
            }
        }
        foreach (var member in editor.WebsiteMembers)
        {
            if (member.IsMember)
            {
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = member.Id, BlockProfileId = profile.Id }));
            }
            else if (member.WasMember && otherProfile is not null)
            {
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = member.Id, BlockProfileId = otherProfile.Id }));
            }
        }

        foreach (var source in editor.FocusAppSources)
        {
            if (source.IsMember)
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = source.Id, BlockProfileId = profile.Id }));
            else if (source.WasMember)
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = source.Id, BlockProfileId = "" }));
        }

        foreach (var source in editor.FocusWebsiteSources)
        {
            if (source.IsMember)
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = source.Id, BlockProfileId = profile.Id }));
            else if (source.WasMember)
                ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = source.Id, BlockProfileId = "" }));
        }

        FooterText.Text = $"Đã lưu Profile {editor.EditedProfile.Name}: chính sách, giải trí và nguồn Focus.";
    }

    private async void CycleProfileMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        profile.Mode = profile.Mode switch
        {
            BlockProfileMode.EarnedTime => BlockProfileMode.AllowanceThenEarned,
            BlockProfileMode.AllowanceThenEarned => BlockProfileMode.ScheduleBlock,
            BlockProfileMode.ScheduleBlock => BlockProfileMode.ScheduleEarnedTime,
            BlockProfileMode.ScheduleEarnedTime => BlockProfileMode.AlwaysBlock,
            _ => BlockProfileMode.EarnedTime
        };
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "updateBlockProfile",
            BlockProfile = profile
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void CycleProfileBlockAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        profile.DefaultBlockAction = profile.DefaultBlockAction switch
        {
            EntertainmentBlockAction.Close => EntertainmentBlockAction.Suspend,
            EntertainmentBlockAction.Suspend => EntertainmentBlockAction.BlockLaunch,
            _ => EntertainmentBlockAction.Close
        };
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "updateBlockProfile",
            BlockProfile = profile
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void OpenWeeklySchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        var editor = new WeeklyScheduleWindow(profile);
        if (editor.ShowDialog() != true) return;

        profile.WeeklyScheduleMask = editor.ScheduleMask;
        profile.ScheduleEnabled = editor.ScheduleEnabled;

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "updateBlockProfile",
            BlockProfile = profile
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void SaveProfilePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BlockProfile profile }) return;
        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "updateBlockProfile",
            BlockProfile = profile
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void EnableSettingsTextProtection_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Bật bảo vệ cài đặt bằng đoạn văn? Từ lúc bật, mọi thay đổi app, website, profile và thời gian đều bị khóa cho tới khi bạn gõ đúng toàn bộ đoạn xác nhận.",
            "Bảo vệ cài đặt", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "enableSettingsTextProtection" });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private static string VisibleChar(char ch) => ch switch
    {
        ' ' => "·",
        '\n' => "↵",
        '\t' => "⇥",
        _ => ch.ToString()
    };

    private void RenderSettingsChallengeColoredPreview(string expected, string actual)
    {
        if (SettingsChallengeColoredPreview is null) return;

        SettingsChallengeColoredPreview.Inlines.Clear();

        var max = Math.Max(expected.Length, actual.Length);
        if (max == 0)
        {
            SettingsChallengeColoredPreview.Inlines.Add(
                new Run("Bắt đầu gõ để xem so sánh trực tiếp.")
                {
                    Foreground = System.Windows.Media.Brushes.DimGray
                });
            return;
        }

        for (var i = 0; i < max; i++)
        {
            if (i >= actual.Length)
            {
                // Expected but not typed yet.
                SettingsChallengeColoredPreview.Inlines.Add(
                    new Run(VisibleChar(expected[i]))
                    {
                        Foreground = System.Windows.Media.Brushes.DarkGray,});
                continue;
            }

            if (i >= expected.Length)
            {
                // Extra typed character.
                SettingsChallengeColoredPreview.Inlines.Add(
                    new Run(VisibleChar(actual[i]))
                    {
                        Foreground = System.Windows.Media.Brushes.Firebrick,
                        FontWeight = FontWeights.SemiBold
                    });
                continue;
            }

            var ok = SettingsChallengeComparer.CharsEqual(expected[i], actual[i]);
            SettingsChallengeColoredPreview.Inlines.Add(
                new Run(VisibleChar(actual[i]))
                {
                    Foreground = ok
                        ? System.Windows.Media.Brushes.SeaGreen
                        : System.Windows.Media.Brushes.Firebrick,
                    FontWeight = ok ? FontWeights.Normal : FontWeights.Bold
                });
        }
    }

    private void UpdateSettingsChallengeComparison()
    {
        if (SettingsChallengeCompareStatus is null ||
            SettingsChallengeCompareDetail is null ||
            SettingsChallengeColoredPreview is null ||
            UnlockSettingsTextProtectionButton is null ||
            SettingsChallengeInputBorder is null)
        {
            return;
        }

        var expected = SettingsChallengeComparer.Normalize(SettingsChallengeText?.Text ?? "");
        var actual = SettingsChallengeComparer.Normalize(SettingsChallengeInput?.Text ?? "");

        RenderSettingsChallengeColoredPreview(expected, actual);

        if (expected.Length == 0)
        {
            SettingsChallengeCompareStatus.Text = "○ Chưa có đoạn chuẩn";
            SettingsChallengeCompareStatus.Foreground = System.Windows.Media.Brushes.DimGray;
            SettingsChallengeCompareDetail.Text = "Chưa thể so sánh.";
            SettingsChallengeInputBorder.BorderBrush = FindResource("BorderBrush") as System.Windows.Media.Brush;
            UnlockSettingsTextProtectionButton.IsEnabled = false;
            return;
        }

        if (actual.Length == 0)
        {
            SettingsChallengeCompareStatus.Text = "○ Chưa nhập";
            SettingsChallengeCompareStatus.Foreground = System.Windows.Media.Brushes.DimGray;
            SettingsChallengeCompareDetail.Text = "Xanh = đúng · Đỏ = sai · Xám = chưa gõ.";
            SettingsChallengeInputBorder.BorderBrush = FindResource("BorderBrush") as System.Windows.Media.Brush;
            UnlockSettingsTextProtectionButton.IsEnabled = false;
            return;
        }

        var diff = SettingsChallengeComparer.FirstDifference(expected, actual);
        if (diff < 0)
        {
            SettingsChallengeCompareStatus.Text = "✓ KHỚP 100% · Có thể mở khóa";
            SettingsChallengeCompareStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
            SettingsChallengeCompareDetail.Text =
                "Toàn bộ nội dung đã khớp. App và Guard đang dùng cùng một bộ so sánh.";
            SettingsChallengeInputBorder.BorderBrush = System.Windows.Media.Brushes.SeaGreen;
            SettingsChallengeInputBorder.BorderThickness = new Thickness(2);
            UnlockSettingsTextProtectionButton.IsEnabled = true;
            return;
        }

        SettingsChallengeCompareStatus.Text = $"✗ CHƯA KHỚP · lỗi đầu tiên tại vị trí {diff + 1}";
        SettingsChallengeCompareStatus.Foreground = System.Windows.Media.Brushes.Firebrick;

        if (actual.Length < expected.Length && diff == actual.Length)
        {
            SettingsChallengeCompareDetail.Text =
                $"Bạn còn thiếu {expected.Length - actual.Length} ký tự. Xem phần màu xám ở cuối.";
        }
        else if (actual.Length > expected.Length && diff == expected.Length)
        {
            SettingsChallengeCompareDetail.Text =
                $"Bạn đang thừa {actual.Length - expected.Length} ký tự ở cuối.";
        }
        else
        {
            SettingsChallengeCompareDetail.Text =
                "Ký tự màu đỏ là chỗ sai. Sửa đến khi toàn bộ đoạn chuyển sang màu xanh.";
        }

        SettingsChallengeInputBorder.BorderBrush = System.Windows.Media.Brushes.Firebrick;
        SettingsChallengeInputBorder.BorderThickness = new Thickness(2);
        UnlockSettingsTextProtectionButton.IsEnabled = false;
    }

    private void SettingsChallengeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSettingsChallengeComparison();
    }

    private async void UnlockSettingsTextProtection_Click(object sender, RoutedEventArgs e)
    {
        var typed = SettingsChallengeInput.Text;
        if (string.IsNullOrWhiteSpace(typed))
        {
            MessageBox.Show(this, "Hãy tự gõ đầy đủ đoạn xác nhận trước.", "Bảo vệ cài đặt");
            return;
        }

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "unlockSettingsTextProtection",
            TextValue = typed
        });

        ApplyResponse(response);
        FooterText.Text = response.Message;

        if (!response.Ok)
        {
            MessageBox.Show(
                this,
                response.Message,
                "Chưa mở khóa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SettingsChallengeInput.Clear();
        MessageBox.Show(
            this,
            "Đã mở khóa cấu hình. Bây giờ bạn có thể thêm/sửa phần mềm, website, profile và cài đặt.",
            "Đã mở khóa",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void EnableSettingsTimeProtection_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsProtectionStartDatePicker.SelectedDate is not DateTime startDate ||
            SettingsProtectionEndDatePicker.SelectedDate is not DateTime endDate ||
            !TimeOnly.TryParse(SettingsProtectionStartTimeBox.Text, out var startTime) ||
            !TimeOnly.TryParse(SettingsProtectionEndTimeBox.Text, out var endTime))
        {
            MessageBox.Show(this, "Hãy chọn ngày và nhập giờ theo dạng HH:mm.", "Bảo vệ cài đặt");
            return;
        }

        var startLocal = DateTime.SpecifyKind(startDate.Date.Add(startTime.ToTimeSpan()), DateTimeKind.Local);
        var endLocal = DateTime.SpecifyKind(endDate.Date.Add(endTime.ToTimeSpan()), DateTimeKind.Local);
        if (endLocal <= startLocal.AddMinutes(1))
        {
            MessageBox.Show(this, "Thời điểm kết thúc phải sau thời điểm bắt đầu ít nhất 2 phút.", "Bảo vệ cài đặt");
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Khóa thay đổi cấu hình từ {startLocal:dd/MM/yyyy HH:mm} tới {endLocal:dd/MM/yyyy HH:mm}? Khi khoảng khóa bắt đầu sẽ không có nút mở sớm.",
            "Bảo vệ cài đặt theo thời gian", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "enableSettingsTimeProtection",
            StartUtc = startLocal.ToUniversalTime(),
            UntilUtc = endLocal.ToUniversalTime()
        });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void EnableStrict_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(StrictDelayBox.Text, out var minutes) || minutes > 1440)
        {
            MessageBox.Show(this, "Thời gian chờ Strict phải từ 1 đến 1440 phút.", "Strict Mode");
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Bật Strict Mode? Sau khi bật, bạn không thể sửa cấu hình cho tới khi yêu cầu mở và chờ đủ {minutes} phút.",
            "Bật Strict Mode", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "enableStrict", DurationMinutes = minutes });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void RequestStrictUnlock_Click(object sender, RoutedEventArgs e)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "requestStrictUnlock" });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void DisableStrict_Click(object sender, RoutedEventArgs e)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "disableStrict" });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void StartFocusSessionPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, out var minutes))
            return;

        await StartFocusSessionAsync(minutes);
    }

    private async void StartFocusSessionCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(FocusSessionMinutesBox.Text, out var minutes) ||
            minutes < 5 || minutes > 1440)
        {
            MessageBox.Show(this, "Focus Session tùy chỉnh phải từ 5 đến 1440 phút.", "Focus Session");
            return;
        }

        await StartFocusSessionAsync(minutes);
    }

    private async Task StartFocusSessionAsync(int minutes)
    {
        if (_snapshot is null) return;

        var option = FocusSessionProfileCombo.SelectedItem as FocusSessionProfileOption;
        var rewardProfile = option?.Profile;
        var rewardSeconds = FocusSessionRewardCalculator.CalculateRewardSeconds(
            minutes,
            _snapshot.State.Settings,
            rewardProfile);

        var sourceLine = rewardProfile is null
            ? "• Nguồn: mọi app/website Focus hợp lệ · dùng công thức chung."
            : $"• Nguồn: chỉ app/website Focus thuộc Profile {rewardProfile.Name}.";

        var formulaLine = rewardProfile is { CustomRewardEnabled: true }
            ? $"• Công thức: {rewardProfile.RewardFocusMinutes} phút Focus → +{rewardProfile.RewardMinutes} phút."
            : $"• Công thức: {_snapshot.State.Settings.FocusMinutesPerKey} phút Focus → +{_snapshot.State.Settings.RewardMinutesPerKey} phút.";

        var confirm = MessageBox.Show(
            this,
            $"Bắt đầu Focus Session {minutes} phút?\n\n" +
            sourceLine + "\n" +
            formulaLine + "\n" +
            $"• Chỉ GIÂY FOCUS HỢP LỆ mới làm phiên tiến lên.\n" +
            $"• App giải trí đã khai báo bị khóa.\n" +
            $"• Browser chỉ cho website Học/Làm việc.\n" +
            $"• Idle hoặc nguồn Focus sai Profile → phiên tạm dừng tiến độ.\n" +
            $"• Hoàn thành → tạo key thưởng +{Format(rewardSeconds)}.\n" +
            $"• Bỏ giữa chừng → không có thưởng.",
            "Bắt đầu Focus Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "startFocusSession",
            DurationMinutes = minutes,
            BlockProfileId = option?.Id ?? ""
        });

        ApplyResponse(response);
        FooterText.Text = response.Message;

        if (!response.Ok)
            MessageBox.Show(this, response.Message, "Không thể bắt đầu Focus Session",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void AbandonFocusSession_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.State.ControlPolicy.FocusSessionActive != true) return;

        var policy = _snapshot.State.ControlPolicy;
        var confirm = MessageBox.Show(
            this,
            $"Bỏ Focus Session hiện tại?\n\n" +
            $"Tiến độ: {Format(policy.FocusSessionQualifiedSeconds)} / {Format(policy.FocusSessionTargetSeconds)}\n" +
            "Phiên chưa hoàn thành sẽ KHÔNG tạo phần thưởng.",
            "Bỏ Focus Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "abandonFocusSession"
        });

        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private void FocusSessionMinutesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFocusSessionRewardPreview();
    }

    private void FocusSessionProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshFocusSessionRewardPreview();
    }

    private void RefreshFocusSessionProfileOptions(AppState state)
    {
        if (FocusSessionProfileCombo is null) return;

        var fingerprint =
            $"{state.Settings.FocusMinutesPerKey}:{state.Settings.RewardMinutesPerKey}|" +
            string.Join(
                "|",
                state.BlockProfiles
                    .Where(p => p.Enabled)
                    .OrderBy(p => p.CreatedUtc)
                    .Select(p =>
                        $"{p.Id}:{p.Name}:{p.CustomRewardEnabled}:{p.RewardFocusMinutes}:{p.RewardMinutes}:" +
                        $"{state.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id)}:" +
                        $"{state.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id)}"));

        if (fingerprint == _focusSessionProfileFingerprint &&
            FocusSessionProfileCombo.ItemsSource is not null)
            return;

        _focusSessionProfileFingerprint = fingerprint;
        var previousId = (FocusSessionProfileCombo.SelectedItem as FocusSessionProfileOption)?.Id ?? "";

        var options = new List<FocusSessionProfileOption>
        {
            new()
            {
                Id = "",
                Profile = null,
                Label = $"Toàn bộ Focus · công thức chung {state.Settings.FocusMinutesPerKey}→+{state.Settings.RewardMinutesPerKey} phút"
            }
        };

        options.AddRange(
            state.BlockProfiles
                .Where(p => p.Enabled)
                .OrderBy(p => p.CreatedUtc)
                .Select(p =>
                {
                    var focusCount =
                        state.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id) +
                        state.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id);
                    var formula = p.CustomRewardEnabled
                        ? $"{p.RewardFocusMinutes}→+{p.RewardMinutes} phút"
                        : $"chung {state.Settings.FocusMinutesPerKey}→+{state.Settings.RewardMinutesPerKey}";
                    return new FocusSessionProfileOption
                    {
                        Id = p.Id,
                        Profile = p,
                        Label = $"{p.Name} · {formula} · {focusCount} nguồn Focus"
                    };
                }));

        FocusSessionProfileCombo.ItemsSource = options;

        var wantedId = state.ControlPolicy.FocusSessionActive
            ? state.ControlPolicy.FocusSessionProfileId
            : previousId;

        FocusSessionProfileCombo.SelectedItem =
            options.FirstOrDefault(x => x.Id == wantedId) ?? options[0];
    }

    private void RefreshFocusSessionRewardPreview()
    {
        if (FocusSessionPresetRewardPreviewText is null || _snapshot is null) return;

        var settings = _snapshot.State.Settings;
        var option = FocusSessionProfileCombo?.SelectedItem as FocusSessionProfileOption;
        var profile = option?.Profile;

        var r25 = FocusSessionRewardCalculator.CalculateRewardSeconds(25, settings, profile);
        var r50 = FocusSessionRewardCalculator.CalculateRewardSeconds(50, settings, profile);
        var r90 = FocusSessionRewardCalculator.CalculateRewardSeconds(90, settings, profile);

        var custom = TryPositiveInt(FocusSessionMinutesBox?.Text ?? "", out var customMinutes) &&
                     customMinutes >= 5 && customMinutes <= 1440
            ? $" · Tùy chỉnh {customMinutes}p → +{Format(FocusSessionRewardCalculator.CalculateRewardSeconds(customMinutes, settings, profile))}"
            : "";

        var focusMinutes = FocusSessionRewardCalculator.ResolveFocusMinutes(profile, settings);
        var rewardMinutes = FocusSessionRewardCalculator.ResolveRewardMinutes(profile, settings);
        var source = profile is null ? "Toàn bộ Focus" : profile.Name;

        FocusSessionPresetRewardPreviewText.Text =
            $"{source} · tỷ lệ {focusMinutes}p Focus → +{rewardMinutes}p: " +
            $"25p → +{Format(r25)} · 50p → +{Format(r50)} · 90p → +{Format(r90)}{custom}";
    }

    private async void StartLockedSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(LockedSessionMinutesBox.Text, out var minutes) || minutes > 1440)
        {
            MessageBox.Show(this, "Locked Session phải từ 1 đến 1440 phút.", "Locked Session");
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Bắt đầu Locked Session {minutes} phút? Phiên này không thể hủy và sẽ khóa mọi app/website giải trí đã khai báo.",
            "Locked Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "startLockedSession", DurationMinutes = minutes });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void StartWhitelistSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(WhitelistMinutesBox.Text, out var minutes) || minutes > 1440)
        {
            MessageBox.Show(this, "Focus-only phải từ 1 đến 1440 phút.", "Focus-only");
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Bắt đầu Focus-only {minutes} phút? Phiên không thể hủy. Trên browser chỉ rule Học/Làm việc được phép.",
            "Focus-only / Whitelist", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "startWhitelistSession", DurationMinutes = minutes });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private void RefreshControlPolicy(AppState state)
    {
        var policy = state.ControlPolicy ?? new ControlPolicy();
        var now = DateTime.UtcNow;

        var textProtection = policy.SettingsTextProtectionActive;
        var timeProtection = policy.SettingsTimeProtectionActive;
        var timeConfigured = policy.SettingsProtectionMode == SettingsProtectionMode.TimeWindow &&
                             policy.SettingsProtectionStartUtc is DateTime && policy.SettingsProtectionUntilUtc is DateTime;

        if (textProtection)
            SettingsProtectionStatusText.Text = "🔒 Đang bảo vệ · muốn sửa phải gõ đoạn xác nhận";
        else if (timeProtection && policy.SettingsProtectionUntilUtc is DateTime protectionUntil)
            SettingsProtectionStatusText.Text = $"🔒 Không thể sửa tới {protectionUntil.ToLocalTime():dd/MM HH:mm:ss}";
        else if (timeConfigured && policy.SettingsProtectionStartUtc is DateTime protectionStart && policy.SettingsProtectionUntilUtc is DateTime scheduledUntil && scheduledUntil > now)
            SettingsProtectionStatusText.Text = $"◷ Đã lên lịch {protectionStart.ToLocalTime():dd/MM HH:mm} → {scheduledUntil.ToLocalTime():dd/MM HH:mm}";
        else
            SettingsProtectionStatusText.Text = "○ Chưa bảo vệ cấu hình";

        SettingsChallengePanel.Visibility = textProtection ? Visibility.Visible : Visibility.Collapsed;
        if (textProtection)
        {
            SettingsChallengeText.Text = policy.SettingsUnlockChallenge;
            UpdateSettingsChallengeComparison();
        }
        else
        {
            SettingsChallengeInput.Clear();
            UnlockSettingsTextProtectionButton.IsEnabled = false;
        }

        var protectionConfiguredAndFuture = textProtection || timeProtection ||
                                            (timeConfigured && policy.SettingsProtectionUntilUtc is DateTime futureUntil && futureUntil > now);
        EnableSettingsTextProtectionButton.IsEnabled = !protectionConfiguredAndFuture;
        EnableSettingsTimeProtectionButton.IsEnabled = !protectionConfiguredAndFuture;

        if (!SettingsProtectionStartDatePicker.IsKeyboardFocusWithin && !SettingsProtectionStartTimeBox.IsKeyboardFocusWithin &&
            !SettingsProtectionEndDatePicker.IsKeyboardFocusWithin && !SettingsProtectionEndTimeBox.IsKeyboardFocusWithin)
        {
            var startLocal = policy.SettingsProtectionStartUtc?.ToLocalTime() ?? DateTime.Now;
            var endLocal = policy.SettingsProtectionUntilUtc?.ToLocalTime() ?? DateTime.Now.AddHours(2);
            SettingsProtectionStartDatePicker.SelectedDate = startLocal.Date;
            SettingsProtectionStartTimeBox.Text = startLocal.ToString("HH:mm");
            SettingsProtectionEndDatePicker.SelectedDate = endLocal.Date;
            SettingsProtectionEndTimeBox.Text = endLocal.ToString("HH:mm");
        }

        if (!StrictDelayBox.IsKeyboardFocusWithin)
            StrictDelayBox.Text = Math.Max(1, policy.StrictUnlockDelayMinutes).ToString();

        if (!policy.StrictModeEnabled)
        {
            StrictStatusText.Text = "○ Strict Mode đang tắt";
        }
        else if (policy.StrictUnlockReady)
        {
            StrictStatusText.Text = "✓ Đã hết thời gian chờ · có thể tắt Strict Mode";
        }
        else if (policy.StrictUnlockAvailableUtc is DateTime ready)
        {
            StrictStatusText.Text = $"🔒 Strict Mode · có thể tắt sau {ready.ToLocalTime():dd/MM HH:mm:ss}";
        }
        else
        {
            StrictStatusText.Text = $"🔒 Strict Mode · muốn tắt phải yêu cầu mở và chờ {policy.StrictUnlockDelayMinutes} phút";
        }
        EnableStrictButton.IsEnabled = !policy.StrictModeEnabled;
        RequestStrictUnlockButton.IsEnabled = policy.StrictModeEnabled && policy.StrictUnlockRequestedUtc is null;
        DisableStrictButton.IsEnabled = policy.StrictUnlockReady;
        RefreshFocusSessionProfileOptions(state);

        var focusSessionActive = policy.FocusSessionActive;
        var configurationLocked = policy.SettingsProtectionActive || policy.StrictModeEnabled || focusSessionActive || policy.LockedSessionActive || policy.WhitelistSessionActive;
        BasicSettingsPanel.IsEnabled = !configurationLocked;
        ProfilePolicyItems.IsEnabled = !configurationLocked;
        var cooldownRestoreLocked = state.BlockProfiles.Any(x => x.CooldownActive);
        RestoreBackupButton.IsEnabled = !configurationLocked && !cooldownRestoreLocked;
        RestoreBackupButton.ToolTip = cooldownRestoreLocked
            ? "Restore bị khóa khi một Profile đang Cooldown."
            : configurationLocked
                ? "Restore bị khóa trong khi FocusLock đang có chế độ bảo vệ/cam kết không thể thay đổi."
                : "Khôi phục toàn bộ dữ liệu từ file .focuslockbackup.";

        if (focusSessionActive)
        {
            var targetSeconds = Math.Max(1, policy.FocusSessionTargetSeconds);
            var qualifiedSeconds = Math.Clamp(policy.FocusSessionQualifiedSeconds, 0, targetSeconds);
            var remain = Math.Max(0, targetSeconds - qualifiedSeconds);

            var sessionProfile = string.IsNullOrWhiteSpace(policy.FocusSessionProfileName)
                ? "Toàn bộ Focus"
                : policy.FocusSessionProfileName;
            FocusSessionStatusText.Text =
                $"● Đang chạy · {sessionProfile} · còn {HumanDuration(remain)} Focus thực";
            FocusSessionProgressBar.Maximum = targetSeconds;
            FocusSessionProgressBar.Value = qualifiedSeconds;
            FocusSessionProgressDetailText.Text =
                $"{Format(qualifiedSeconds)} / {Format(targetSeconds)}";
            FocusSessionRewardText.Text =
                $"Hoàn thành → key +{Format(policy.FocusSessionRewardSeconds)}";
            FocusSessionStartPanel.IsEnabled = false;
            AbandonFocusSessionButton.IsEnabled = true;
        }
        else
        {
            FocusSessionStatusText.Text = "○ Chưa có Focus Session";
            FocusSessionProgressBar.Maximum = 1;
            FocusSessionProgressBar.Value = 0;
            FocusSessionProgressDetailText.Text = "00:00 / 00:00";
            FocusSessionRewardText.Text = "Hoàn thành sẽ tạo key thưởng";
            FocusSessionStartPanel.IsEnabled = true;
            AbandonFocusSessionButton.IsEnabled = false;
        }

        RefreshFocusSessionRewardPreview();

        LockedSessionStatusText.Text = policy.LockedSessionUntilUtc is DateTime lockedUntil && lockedUntil > now
            ? $"🔒 Đang khóa tới {lockedUntil.ToLocalTime():dd/MM HH:mm:ss} · còn {HumanDuration((int)Math.Ceiling((lockedUntil - now).TotalSeconds))}"
            : "○ Chưa có Locked Session";

        WhitelistStatusText.Text = policy.WhitelistSessionUntilUtc is DateTime whiteUntil && whiteUntil > now
            ? $"✓ Focus-only tới {whiteUntil.ToLocalTime():dd/MM HH:mm:ss} · còn {HumanDuration((int)Math.Ceiling((whiteUntil - now).TotalSeconds))}"
            : "○ Focus-only chưa chạy";
    }

    private async void RemoveAppCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: TrackedApp app }) return;
        var confirm = MessageBox.Show(this, $"Xóa {app.Name} khỏi FocusLock?", "Xóa ứng dụng", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "removeApp", AppId = app.Id }));
    }

    // Legacy hidden handlers kept for compatibility.
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

    // Browser ---------------------------------------------------------------------
    private void AddBrowserFocusRule_Click(object sender, RoutedEventArgs e) => _ = AddBrowserRuleAsync(AppCategory.Focus);
    private void AddBrowserEntertainmentRule_Click(object sender, RoutedEventArgs e) => _ = AddBrowserRuleAsync(AppCategory.Entertainment);
    private void AddSimpleBrowserFocus_Click(object sender, RoutedEventArgs e) => _ = AddSimpleBrowserRuleAsync(AppCategory.Focus);
    private void AddSimpleBrowserEntertainment_Click(object sender, RoutedEventArgs e) => _ = AddSimpleBrowserRuleAsync(AppCategory.Entertainment);

    private async Task AddSimpleBrowserRuleAsync(AppCategory category)
    {
        var pattern = NormalizeHost(SimpleBrowserRuleBox.Text);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(this, "Nhập website, ví dụ youtube.com hoặc coursera.org.", "Thêm website");
            return;
        }

        var rule = new BrowserRule
        {
            Name = pattern,
            Pattern = pattern,
            MatchType = BrowserRuleMatchType.HostSuffix,
            Category = category,
            Enabled = true
        };
        var response = await _client.SendAsync(new PipeRequest { Command = "addBrowserRule", BrowserRule = rule });
        ApplyResponse(response);
        FooterText.Text = response.Message;
        if (response.Ok)
            SimpleBrowserRuleBox.Clear();
        else
            MessageBox.Show(this, response.Message, "Không thể thêm website", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task AddBrowserRuleAsync(AppCategory category)
    {
        var pattern = BrowserRulePatternBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(this, "Hãy nhập domain, URL hoặc tiêu đề cần phân loại.", "Thêm quy tắc nâng cao");
            return;
        }

        var rule = new BrowserRule
        {
            Name = BrowserRuleNameBox.Text.Trim(),
            Pattern = pattern,
            MatchType = GetBrowserMatchType(),
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
        else
        {
            MessageBox.Show(this, response.Message, "Không thể thêm website", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BrowserRuleMatchType GetBrowserMatchType()
    {
        if (BrowserMatchTypeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<BrowserRuleMatchType>(tag, true, out var type)) return type;
        return BrowserRuleMatchType.HostSuffix;
    }

    private void UseCurrentSimpleBrowserPage_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserHost) || _snapshot.CurrentBrowserHost == "—")
        {
            MessageBox.Show(this, "Chưa nhận được website hiện tại. Hãy mở Chrome/Edge và kiểm tra Extension.", "FocusLock");
            return;
        }
        SimpleBrowserRuleBox.Text = _snapshot.CurrentBrowserHost;
    }

    private void UseCurrentBrowserPage_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserUrl))
        {
            MessageBox.Show(this, "Chưa nhận được trang hiện tại từ Browser Extension.", "FocusLock");
            return;
        }

        var type = GetBrowserMatchType();
        BrowserRulePatternBox.Text = BrowserRuleUrlHelper.PatternFromCurrentPage(
            type,
            _snapshot.CurrentBrowserUrl,
            _snapshot.CurrentBrowserHost,
            _snapshot.CurrentBrowserTitle);
        if (string.IsNullOrWhiteSpace(BrowserRuleNameBox.Text))
            BrowserRuleNameBox.Text = _snapshot.CurrentBrowserHost == "—" ? _snapshot.CurrentBrowserTitle : _snapshot.CurrentBrowserHost;
    }

    private async void ToggleBrowserRuleCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BrowserRule rule }) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "toggleBrowserRule", BrowserRuleId = rule.Id }));
    }

    private async void EditBrowserProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || sender is not Button { CommandParameter: BrowserRule rule }) return;
        var dialog = new ProfileAssignmentWindow(rule.DisplayName, rule.BlockProfileId, _snapshot.State.BlockProfiles) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = rule.Id, BlockProfileId = dialog.SelectedProfileId });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void CycleBrowserProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BrowserRule rule }) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "cycleBrowserProfile", BrowserRuleId = rule.Id });
        ApplyResponse(response);
        FooterText.Text = response.Message;
    }

    private async void RemoveBrowserRuleCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: BrowserRule rule }) return;
        var confirm = MessageBox.Show(this, $"Xóa quy tắc {rule.DisplayName}?", "Xóa website", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        ApplyResponse(await _client.SendAsync(new PipeRequest { Command = "removeBrowserRule", BrowserRuleId = rule.Id }));
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
        var path = Path.Combine(OneDirBootstrapper.GetRootDirectory(), "BrowserExtension");
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "Không tìm thấy BrowserExtension trong thư mục FocusLock OneDir.", "FocusLock");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // Rewards ---------------------------------------------------------------------
    private async void RedeemKeyCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: RewardKey key }) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "redeem", KeyCode = key.Code });
        RedeemResultText.Text = response.Message;
        ApplyResponse(response);
        if (response.Ok) FooterText.Text = $"Đã cộng {key.RewardLabel} vào ví giải trí.";
    }

    private async void RedeemKey_Click(object sender, RoutedEventArgs e)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "redeem", KeyCode = RedeemKeyTextBox.Text.Trim() });
        RedeemResultText.Text = response.Message;
        if (response.Ok) RedeemKeyTextBox.Clear();
        ApplyResponse(response);
    }

    // Backup / Restore ------------------------------------------------------------
    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Tạo FocusLock Backup",
            Filter = "FocusLock Backup (*.focuslockbackup)|*.focuslockbackup|All files (*.*)|*.*",
            DefaultExt = ".focuslockbackup",
            AddExtension = true,
            FileName = $"FocusLock-Backup-{DateTime.Now:yyyyMMdd-HHmmss}.focuslockbackup"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            CreateBackupButton.IsEnabled = false;
            BackupRestoreStatusText.Text = "Đang tạo backup…";
            var response = await _client.SendAsync(new PipeRequest
            {
                Command = "createBackup",
                FilePath = dialog.FileName
            });
            ApplyResponse(response);
            BackupRestoreStatusText.Text = response.Ok
                ? $"✓ Backup thành công · {dialog.FileName}"
                : $"✕ Backup thất bại · {response.Message}";
            FooterText.Text = response.Message;
            if (response.Ok)
                MessageBox.Show(this, "Đã tạo backup thành công. Hãy giữ file .focuslockbackup ở nơi riêng tư.", "FocusLock Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, response.Message, "Không thể tạo Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("CreateBackup", ex);
            BackupRestoreStatusText.Text = "✕ Backup thất bại.";
            MessageBox.Show(this, ex.Message, "Không thể tạo Backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CreateBackupButton.IsEnabled = true;
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore FocusLock Backup",
            Filter = "FocusLock Backup (*.focuslockbackup)|*.focuslockbackup|All files (*.*)|*.*",
            DefaultExt = ".focuslockbackup",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            this,
            "Restore sẽ thay toàn bộ dữ liệu FocusLock bằng dữ liệu trong file backup, gồm Profile, app/website, cài đặt, phần thưởng, thống kê và trạng thái bảo vệ.\n\nFocusLock sẽ tự tạo một safety backup của dữ liệu hiện tại trước khi thay thế.\n\nTiếp tục Restore?",
            "Xác nhận Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            RestoreBackupButton.IsEnabled = false;
            BackupRestoreStatusText.Text = "Đang kiểm tra và Restore backup…";
            var response = await _client.SendAsync(new PipeRequest
            {
                Command = "restoreBackup",
                FilePath = dialog.FileName
            });

            if (response.Ok)
            {
                // Force all restored settings to repaint even if this window was already loaded.
                _settingsLoaded = false;
                _appsFingerprint = "";
                _profilesFingerprint = "";
                _profilePolicyFingerprint = "";
                _rulesFingerprint = "";
                _keysFingerprint = "";
                _focusSessionProfileFingerprint = "";
            }
            ApplyResponse(response);
            BackupRestoreStatusText.Text = response.Ok
                ? "✓ Restore thành công · dữ liệu đã được nạp lại."
                : $"✕ Restore bị từ chối · {response.Message}";
            FooterText.Text = response.Message;

            MessageBox.Show(
                this,
                response.Message,
                response.Ok ? "Restore hoàn tất" : "Không thể Restore",
                MessageBoxButton.OK,
                response.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppCrashLogger.Exception("RestoreBackup", ex);
            BackupRestoreStatusText.Text = "✕ Restore thất bại; dữ liệu hiện tại được giữ nguyên hoặc đã rollback bằng safety backup.";
            MessageBox.Show(this, ex.Message, "Restore thất bại", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_snapshot is not null)
            {
                var p = _snapshot.State.ControlPolicy;
                RestoreBackupButton.IsEnabled = !(p.SettingsProtectionActive || p.StrictModeEnabled || p.FocusSessionActive || p.LockedSessionActive || p.WhitelistSessionActive || _snapshot.State.BlockProfiles.Any(x => x.CooldownActive));
            }
            else
            {
                RestoreBackupButton.IsEnabled = true;
            }
        }
    }

    // Settings --------------------------------------------------------------------
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositiveInt(FocusMinutesBox.Text, out var focus) ||
            !TryPositiveInt(RewardMinutesBox.Text, out var reward) ||
            !TryPositiveInt(KeyExpiryBox.Text, out var expiryHours) ||
            !TryPositiveInt(IdleSecondsBox.Text, out var idle) ||
            !TryPositiveInt(MaxBalanceBox.Text, out var max) ||
            !TryPositiveInt(ActivityEventsBox.Text, out var eventsPerMinute) ||
            !TryPositiveInt(HeartbeatBox.Text, out var heartbeat) ||
            !TryPositiveInt(ClockToleranceBox.Text, out var clockTolerance) ||
            !TryPositiveInt(StreakGoalBox.Text, out var streakGoal) ||
            !TryPositiveInt(RetentionDaysBox.Text, out var retentionDays) ||
            !TryPositiveInt(SessionLimitBox.Text, out var sessionLimit) ||
            !TryPositiveInt(BrowserContextTimeoutBox.Text, out var browserTimeout) ||
            !TryPositiveInt(LockCountdownWarningBox.Text, out var countdownWarning) ||
            !TryPositiveInt(LockCountdownCriticalBox.Text, out var countdownCritical))
        {
            MessageBox.Show(this, "Các thông số phải là số nguyên dương.", "FocusLock");
            return;
        }

        if (expiryHours < 24 || expiryHours > int.MaxValue / 60)
        {
            MessageBox.Show(this, "Thời hạn key tối thiểu là 24 giờ.", "FocusLock");
            return;
        }

        if (countdownWarning < 5 || countdownWarning > 600)
        {
            MessageBox.Show(this, "Countdown cảnh báo phải từ 5 đến 600 giây.", "FocusLock");
            return;
        }

        if (countdownCritical < 1 || countdownCritical >= countdownWarning)
        {
            MessageBox.Show(this, "Mốc cảnh báo đỏ phải từ 1 giây và nhỏ hơn mốc cảnh báo.", "FocusLock");
            return;
        }

        var settings = new UserSettings
        {
            FocusMinutesPerKey = focus,
            RewardMinutesPerKey = reward,
            KeyExpiryMinutes = checked(expiryHours * 60),
            IdleThresholdSeconds = idle,
            MaxEntertainmentMinutes = max,
            AntiCheatEnabled = AntiCheatCheck.IsChecked == true,
            MinimumActivityEventsPerMinute = eventsPerMinute,
            AgentHeartbeatTimeoutSeconds = heartbeat,
            ClockRollbackToleranceSeconds = clockTolerance,
            VerifyExecutableHash = VerifyHashCheck.IsChecked == true,
            BubbleEnabled = BubbleEnabledCheck.IsChecked == true,
            LockCountdownEnabled = LockCountdownEnabledCheck.IsChecked == true,
            LockCountdownWarningSeconds = countdownWarning,
            LockCountdownCriticalSeconds = countdownCritical,
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            MinimizeToTray = MinimizeToTrayCheck.IsChecked == true,
            StreakMinimumFocusMinutes = streakGoal,
            StatisticsRetentionDays = retentionDays,
            SessionHistoryLimit = sessionLimit,
            BrowserRulesEnabled = BrowserRulesEnabledCheck.IsChecked == true,
            BrowserContextTimeoutSeconds = browserTimeout,
            OnboardingCompleted = _snapshot?.State.Settings.OnboardingCompleted ?? true
        };
        var response = await _client.SendAsync(new PipeRequest { Command = "settings", Settings = settings });
        if (response.Ok) StartupRegistration.Apply(settings.StartWithWindows);
        ApplyResponse(response);
        FooterText.Text = response.Ok ? "Đã lưu cài đặt." : response.Message;
    }

    private void LoadSettingsToUi(UserSettings s)
    {
        if (FocusMinutesBox.IsKeyboardFocusWithin) return;
        FocusMinutesBox.Text = s.FocusMinutesPerKey.ToString();
        RewardMinutesBox.Text = s.RewardMinutesPerKey.ToString();
        KeyExpiryBox.Text = Math.Max(24, (int)Math.Ceiling(s.KeyExpiryMinutes / 60.0)).ToString();
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
        LockCountdownEnabledCheck.IsChecked = s.LockCountdownEnabled;
        LockCountdownWarningBox.Text = Math.Clamp(s.LockCountdownWarningSeconds, 5, 600).ToString();
        var critical = Math.Clamp(s.LockCountdownCriticalSeconds, 1, Math.Max(1, s.LockCountdownWarningSeconds - 1));
        LockCountdownCriticalBox.Text = critical.ToString();
        StartWithWindowsCheck.IsChecked = s.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = s.MinimizeToTray;
        BrowserRulesEnabledCheck.IsChecked = s.BrowserRulesEnabled;
    }

    // Onboarding ------------------------------------------------------------------
    private void ShowOnboarding()
    {
        _onboardingStep = 0;
        if (_snapshot is not null)
        {
            OnboardingFocusMinutesBox.Text = _snapshot.State.Settings.FocusMinutesPerKey.ToString();
            OnboardingRewardMinutesBox.Text = _snapshot.State.Settings.RewardMinutesPerKey.ToString();
        }
        OnboardingOverlay.Visibility = Visibility.Visible;
        UpdateOnboardingUi();
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        _ = OnboardingNextAsync();
    }

    private async Task OnboardingNextAsync()
    {
        if (_snapshot is null) return;
        if (_onboardingStep == 1 && !_snapshot.State.Apps.Any(a => a.Category == AppCategory.Focus))
        {
            MessageBox.Show(this, "Hãy chọn ít nhất một ứng dụng học/làm việc. Bạn cũng có thể bấm Bỏ qua và cấu hình sau.", "Thiết lập FocusLock");
            return;
        }
        if (_onboardingStep == 2 && !_snapshot.State.Apps.Any(a => a.Category == AppCategory.Entertainment))
        {
            MessageBox.Show(this, "Hãy chọn ít nhất một ứng dụng giải trí. Bạn cũng có thể bấm Bỏ qua và cấu hình sau.", "Thiết lập FocusLock");
            return;
        }
        if (_onboardingStep < 3)
        {
            _onboardingStep++;
            UpdateOnboardingUi();
            return;
        }

        if (!TryPositiveInt(OnboardingFocusMinutesBox.Text, out var focus) || !TryPositiveInt(OnboardingRewardMinutesBox.Text, out var reward))
        {
            MessageBox.Show(this, "Thời gian phải là số nguyên dương.", "Thiết lập FocusLock");
            return;
        }

        var settings = CloneSettings(_snapshot.State.Settings);
        settings.FocusMinutesPerKey = focus;
        settings.RewardMinutesPerKey = reward;
        settings.OnboardingCompleted = true;
        var response = await _client.SendAsync(new PipeRequest { Command = "settings", Settings = settings });
        ApplyResponse(response);
        if (!response.Ok)
        {
            MessageBox.Show(this, response.Message, "Không thể hoàn tất thiết lập");
            return;
        }
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        LoadSettingsToUi(settings);
        FooterText.Text = "Thiết lập ban đầu hoàn tất. Bạn có thể bắt đầu Focus.";
    }

    private void OnboardingBack_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep <= 0) return;
        _onboardingStep--;
        UpdateOnboardingUi();
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e)
    {
        _ = SkipOnboardingAsync();
    }

    private async Task SkipOnboardingAsync()
    {
        if (_snapshot is null)
        {
            OnboardingOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var settings = CloneSettings(_snapshot.State.Settings);
        settings.OnboardingCompleted = true;
        var response = await _client.SendAsync(new PipeRequest { Command = "settings", Settings = settings });
        ApplyResponse(response);
        OnboardingOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateOnboardingUi()
    {
        OnboardingStep0.Visibility = _onboardingStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep1.Visibility = _onboardingStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep2.Visibility = _onboardingStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep3.Visibility = _onboardingStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingBackButton.Visibility = _onboardingStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStepLabel.Text = $"Bước {_onboardingStep + 1} / 4";
        OnboardingNextButton.Content = _onboardingStep switch
        {
            0 => "Bắt đầu →",
            3 => "Hoàn tất ✓",
            _ => "Tiếp tục →"
        };
        UpdateOnboardingCounts();
    }

    private void UpdateOnboardingCounts()
    {
        if (_snapshot is null) return;
        var focus = _snapshot.State.Apps.Count(a => a.Category == AppCategory.Focus);
        var play = _snapshot.State.Apps.Count(a => a.Category == AppCategory.Entertainment);
        OnboardingFocusCountText.Text = $"{focus} ứng dụng học tập đã chọn";
        OnboardingEntertainmentCountText.Text = $"{play} ứng dụng giải trí đã chọn";
    }

    // Helpers ---------------------------------------------------------------------
    private Brush BrushOf(string key) => (Brush)FindResource(key);

    private static string FriendlyMode(string mode, bool isIdle)
    {
        if (isIdle) return "⏸ Đã tạm dừng";
        if (mode.Contains("giải trí", StringComparison.OrdinalIgnoreCase)) return "🎮 Đang giải trí";
        if (mode.Contains("focus", StringComparison.OrdinalIgnoreCase) || mode.Contains("học", StringComparison.OrdinalIgnoreCase)) return "● Đang tập trung";
        return "Sẵn sàng";
    }

    private static string FriendlyCategory(string category)
    {
        if (category.Contains("Entertainment", StringComparison.OrdinalIgnoreCase) || category.Contains("giải trí", StringComparison.OrdinalIgnoreCase)) return "Giải trí";
        if (category.Contains("Focus", StringComparison.OrdinalIgnoreCase) || category.Contains("học", StringComparison.OrdinalIgnoreCase)) return "Học / Làm việc";
        return category;
    }

    private static string NormalizeHost(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.Trim('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return host;
        }
        return input.Trim().Trim('/').ToLowerInvariant();
    }

    private static UserSettings CloneSettings(UserSettings s) => new()
    {
        FocusMinutesPerKey = s.FocusMinutesPerKey,
        RewardMinutesPerKey = s.RewardMinutesPerKey,
        KeyExpiryMinutes = s.KeyExpiryMinutes,
        IdleThresholdSeconds = s.IdleThresholdSeconds,
        MaxEntertainmentMinutes = s.MaxEntertainmentMinutes,
        BubbleEnabled = s.BubbleEnabled,
        LockCountdownEnabled = s.LockCountdownEnabled,
        LockCountdownWarningSeconds = s.LockCountdownWarningSeconds,
        LockCountdownCriticalSeconds = s.LockCountdownCriticalSeconds,
        StartWithWindows = s.StartWithWindows,
        MinimizeToTray = s.MinimizeToTray,
        AntiCheatEnabled = s.AntiCheatEnabled,
        MinimumActivityEventsPerMinute = s.MinimumActivityEventsPerMinute,
        AgentHeartbeatTimeoutSeconds = s.AgentHeartbeatTimeoutSeconds,
        ClockRollbackToleranceSeconds = s.ClockRollbackToleranceSeconds,
        VerifyExecutableHash = s.VerifyExecutableHash,
        StreakMinimumFocusMinutes = s.StreakMinimumFocusMinutes,
        StatisticsRetentionDays = s.StatisticsRetentionDays,
        SessionHistoryLimit = s.SessionHistoryLimit,
        BrowserRulesEnabled = s.BrowserRulesEnabled,
        BrowserContextTimeoutSeconds = s.BrowserContextTimeoutSeconds,
        OnboardingCompleted = s.OnboardingCompleted
    };

    private static bool TryPositiveInt(string text, out int value) => int.TryParse(text, out value) && value > 0;
    private static int SafeUiRemainingSum(int left, int right)
    {
        var sum = (long)Math.Max(0, left) + Math.Max(0, right);
        return sum >= int.MaxValue ? int.MaxValue : (int)sum;
    }

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

    private static string HumanDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} giờ {t.Minutes} phút";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} phút {t.Seconds} giây";
        return $"{t.Seconds} giây";
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
