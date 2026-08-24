using System.Windows;
using System.Windows.Controls;
using FocusLock.Shared.Models;

namespace FocusLock.App;

public partial class ProfileEditorWindow : Window
{
    public sealed class MemberChoice
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool IsMember { get; set; }
        public bool WasMember { get; init; }
    }

    private sealed record PolicyOption(ProfileAccessPolicy Value, string Label, string Help);
    private sealed record ActionOption(EntertainmentBlockAction Value, string Label, string Help);

    private readonly BlockProfile _working;
    private readonly List<MemberChoice> _apps;
    private readonly List<MemberChoice> _websites;
    private readonly List<MemberChoice> _focusApps;
    private readonly List<MemberChoice> _focusWebsites;
    private readonly List<PolicyOption> _policies = new()
    {
        new(ProfileAccessPolicy.EarnedTime, "Chỉ dùng ví Focus", "Mỗi giây sử dụng sẽ trừ ví thời gian bạn đã kiếm bằng Focus. Hết ví thì khóa."),
        new(ProfileAccessPolicy.AllowanceThenEarned, "Allowance → ví Focus", "Dùng allowance miễn phí trước; khi hết allowance sẽ chuyển sang trừ ví Focus."),
        new(ProfileAccessPolicy.Free, "Dùng tự do", "Không trừ allowance và cũng không trừ ví Focus trong trạng thái này."),
        new(ProfileAccessPolicy.Block, "Khóa tuyệt đối", "Không cho sử dụng bất kể allowance hoặc ví Focus còn bao nhiêu.")
    };
    private readonly List<ActionOption> _actions = new()
    {
        new(EntertainmentBlockAction.Close, "Đóng ứng dụng", "Phù hợp game/ứng dụng có anti-cheat. Khi bị khóa, FocusLock đóng process."),
        new(EntertainmentBlockAction.Suspend, "Tạm dừng & tự tiếp tục", "Giữ nguyên phiên ứng dụng nhưng đóng băng process. Khi được phép lại, FocusLock tự tiếp tục."),
    };

    public BlockProfile EditedProfile => _working;
    public IReadOnlyList<MemberChoice> AppMembers => _apps;
    public IReadOnlyList<MemberChoice> WebsiteMembers => _websites;
    public IReadOnlyList<MemberChoice> FocusAppSources => _focusApps;
    public IReadOnlyList<MemberChoice> FocusWebsiteSources => _focusWebsites;

    public ProfileEditorWindow(BlockProfile source, IEnumerable<TrackedApp> apps, IEnumerable<BrowserRule> rules)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _working = Clone(source);
        _apps = apps.Where(a => a.Category == AppCategory.Entertainment).OrderBy(a => a.Name).Select(a => new MemberChoice
        {
            Id = a.Id,
            Name = a.Name,
            Detail = $"{a.ProcessName} · hiện ở {a.BlockProfileName}",
            IsMember = a.BlockProfileId == source.Id,
            WasMember = a.BlockProfileId == source.Id
        }).ToList();
        _websites = rules.Where(r => r.Category == AppCategory.Entertainment).OrderBy(r => r.DisplayName).Select(r => new MemberChoice
        {
            Id = r.Id,
            Name = r.DisplayName,
            Detail = $"{r.Pattern} · hiện ở {r.BlockProfileName}",
            IsMember = r.BlockProfileId == source.Id,
            WasMember = r.BlockProfileId == source.Id
        }).ToList();

        _focusApps = apps.Where(a => a.Category == AppCategory.Focus).OrderBy(a => a.Name).Select(a => new MemberChoice
        {
            Id = a.Id,
            Name = a.Name,
            Detail = string.IsNullOrWhiteSpace(a.BlockProfileId)
                ? $"{a.ProcessName} · công thức chung"
                : $"{a.ProcessName} · hiện ở {a.BlockProfileName}",
            IsMember = a.BlockProfileId == source.Id,
            WasMember = a.BlockProfileId == source.Id
        }).ToList();

        _focusWebsites = rules.Where(r => r.Category == AppCategory.Focus).OrderBy(r => r.DisplayName).Select(r => new MemberChoice
        {
            Id = r.Id,
            Name = r.DisplayName,
            Detail = string.IsNullOrWhiteSpace(r.BlockProfileId)
                ? $"{r.Pattern} · công thức chung"
                : $"{r.Pattern} · hiện ở {r.BlockProfileName}",
            IsMember = r.BlockProfileId == source.Id,
            WasMember = r.BlockProfileId == source.Id
        }).ToList();

        NameBox.Text = _working.Name;
        EnabledCheck.IsChecked = _working.Enabled;
        AllowanceBox.Text = _working.DailyAllowanceMinutes.ToString();
        DailyBudgetEnabledCheck.IsChecked = _working.DailyBudgetMinutes > 0;
        DailyBudgetBox.Text = (_working.DailyBudgetMinutes > 0 ? _working.DailyBudgetMinutes : 60).ToString();
        DailyBudgetTodayText.Text = _working.DailyBudgetLabel;

        CooldownEnabledCheck.IsChecked = _working.CooldownEnabled;
        CooldownAfterMinutesBox.Text = Math.Max(1, _working.CooldownAfterMinutes).ToString();
        CooldownMinutesBox.Text = Math.Max(1, _working.CooldownMinutes).ToString();
        CooldownStatusText.Text = _working.CooldownLabel;

        CustomRewardEnabledCheck.IsChecked = _working.CustomRewardEnabled;
        RewardFocusMinutesBox.Text = Math.Max(1, _working.RewardFocusMinutes).ToString();
        RewardMinutesBox.Text = Math.Max(1, _working.RewardMinutes).ToString();
        RewardProgressText.Text = _working.RewardProgressLabel;
        FocusAppsList.ItemsSource = _focusApps;
        FocusWebsitesList.ItemsSource = _focusWebsites;
        RefreshFocusSourceCount();

        ScheduleEnabledCheck.IsChecked = _working.ScheduleEnabled;
        AppsList.ItemsSource = _apps;
        WebsitesList.ItemsSource = _websites;
        MemberCountText.Text = $"{_apps.Count(x => x.IsMember)} app · {_websites.Count(x => x.IsMember)} website";

        DefaultPolicyCombo.ItemsSource = _policies;
        ScheduledPolicyCombo.ItemsSource = _policies;
        DefaultBlockActionCombo.ItemsSource = _actions;
        DefaultPolicyCombo.SelectedItem = _policies.First(x => x.Value == _working.DefaultAccessPolicy);
        ScheduledPolicyCombo.SelectedItem = _policies.First(x => x.Value == _working.ScheduledAccessPolicy);
        DefaultBlockActionCombo.SelectedItem = _actions.FirstOrDefault(x => x.Value == _working.DefaultBlockAction) ?? _actions[0];
        DefaultPolicyCombo.SelectionChanged += (_, _) => RefreshHelp();
        ScheduledPolicyCombo.SelectionChanged += (_, _) => RefreshHelp();
        DefaultBlockActionCombo.SelectionChanged += (_, _) => RefreshHelp();
        AppsList.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => RefreshMemberCount()));
        WebsitesList.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => RefreshMemberCount()));
        FocusAppsList.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => RefreshFocusSourceCount()));
        FocusWebsitesList.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => RefreshFocusSourceCount()));
        CooldownEnabledCheck.Click += (_, _) => RefreshCooldownPreview();
        CooldownAfterMinutesBox.TextChanged += (_, _) => RefreshCooldownPreview();
        CooldownMinutesBox.TextChanged += (_, _) => RefreshCooldownPreview();
        CustomRewardEnabledCheck.Click += (_, _) => RefreshRewardPreview();
        RewardFocusMinutesBox.TextChanged += (_, _) => RefreshRewardPreview();
        RewardMinutesBox.TextChanged += (_, _) => RefreshRewardPreview();
        RefreshHelp();
        RefreshCooldownPreview();
        RefreshRewardPreview();
        RefreshScheduleSummary();
    }

    private void RefreshHelp()
    {
        DefaultPolicyHelp.Text = (DefaultPolicyCombo.SelectedItem as PolicyOption)?.Help ?? "";
        ScheduledPolicyHelp.Text = (ScheduledPolicyCombo.SelectedItem as PolicyOption)?.Help ?? "";
        BlockActionHelp.Text = (DefaultBlockActionCombo.SelectedItem as ActionOption)?.Help ?? "";
    }

    private void RefreshMemberCount() => MemberCountText.Text = $"{_apps.Count(x => x.IsMember)} app · {_websites.Count(x => x.IsMember)} website";

    private void RefreshFocusSourceCount()
    {
        FocusSourceCountText.Text =
            $"{_focusApps.Count(x => x.IsMember)} app · {_focusWebsites.Count(x => x.IsMember)} website Focus";
    }

    private void RefreshCooldownPreview()
    {
        if (CooldownEnabledCheck.IsChecked != true)
        {
            CooldownStatusText.Text = _working.CooldownActive
                ? _working.CooldownLabel + " · tắt chỉ áp dụng cho chu kỳ tiếp theo"
                : "Cooldown đang tắt.";
            return;
        }

        var afterOk = int.TryParse(CooldownAfterMinutesBox.Text.Trim(), out var afterMinutes) &&
                      afterMinutes >= 1 && afterMinutes <= 1440;
        var restOk = int.TryParse(CooldownMinutesBox.Text.Trim(), out var restMinutes) &&
                     restMinutes >= 1 && restMinutes <= 1440;

        if (!afterOk || !restOk)
        {
            CooldownStatusText.Text = "Nhập 1–1440 phút cho cả mốc giải trí và thời gian nghỉ.";
            return;
        }

        if (_working.CooldownActive)
            CooldownStatusText.Text = _working.CooldownLabel + " · chỉnh cấu hình không hủy lần nghỉ đang chạy";
        else
            CooldownStatusText.Text = $"Sau {afterMinutes} phút giải trí tích lũy → khóa Profile {restMinutes} phút.";
    }

    private void RefreshRewardPreview()
    {
        var enabled = CustomRewardEnabledCheck.IsChecked == true;
        RewardFormulaPanel.IsEnabled = enabled;

        if (!enabled)
        {
            RewardFormulaPreviewText.Text = "Nguồn Focus gán vào Profile vẫn dùng công thức thưởng chung.";
            return;
        }

        var focusOk = int.TryParse(RewardFocusMinutesBox.Text.Trim(), out var focusMinutes) &&
                      focusMinutes >= 1 && focusMinutes <= 1440;
        var rewardOk = int.TryParse(RewardMinutesBox.Text.Trim(), out var rewardMinutes) &&
                       rewardMinutes >= 1 && rewardMinutes <= 1440;

        RewardFormulaPreviewText.Text = focusOk && rewardOk
            ? $"{focusMinutes} phút Focus hợp lệ → tạo key +{rewardMinutes} phút giải trí"
            : "Nhập 1–1440 phút cho cả mốc Focus và phần thưởng.";
    }

    private void RefreshScheduleSummary() => ScheduleSummaryText.Text = _working.ScheduleLabel;

    private void OpenSchedule_Click(object sender, RoutedEventArgs e)
    {
        _working.ScheduleEnabled = ScheduleEnabledCheck.IsChecked == true;
        var editor = new WeeklyScheduleWindow(_working) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _working.WeeklyScheduleMask = editor.ScheduleMask;
        _working.ScheduleEnabled = editor.ScheduleEnabled;
        ScheduleEnabledCheck.IsChecked = editor.ScheduleEnabled;
        RefreshScheduleSummary();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show(this, "Tên profile không được để trống.", "FocusLock"); return; }
        if (!int.TryParse(AllowanceBox.Text.Trim(), out var allowance) || allowance < 0 || allowance > 1440)
        { MessageBox.Show(this, "Allowance phải từ 0 đến 1440 phút/ngày.", "FocusLock"); return; }

        var budgetEnabled = DailyBudgetEnabledCheck.IsChecked == true;
        var dailyBudget = 0;
        if (budgetEnabled &&
            (!int.TryParse(DailyBudgetBox.Text.Trim(), out dailyBudget) || dailyBudget < 1 || dailyBudget > 1440))
        {
            MessageBox.Show(this, "Ngân sách giải trí phải từ 1 đến 1440 phút/ngày.", "FocusLock");
            return;
        }

        var cooldownEnabled = CooldownEnabledCheck.IsChecked == true;
        if (!int.TryParse(CooldownAfterMinutesBox.Text.Trim(), out var cooldownAfterMinutes) || cooldownAfterMinutes < 1 || cooldownAfterMinutes > 1440)
        {
            MessageBox.Show(this, "Mốc giải trí trước cooldown phải từ 1 đến 1440 phút.", "FocusLock");
            return;
        }
        if (!int.TryParse(CooldownMinutesBox.Text.Trim(), out var cooldownMinutes) || cooldownMinutes < 1 || cooldownMinutes > 1440)
        {
            MessageBox.Show(this, "Thời gian cooldown phải từ 1 đến 1440 phút.", "FocusLock");
            return;
        }

        var customReward = CustomRewardEnabledCheck.IsChecked == true;
        if (!int.TryParse(RewardFocusMinutesBox.Text.Trim(), out var rewardFocusMinutes) ||
            rewardFocusMinutes < 1 || rewardFocusMinutes > 1440)
        {
            MessageBox.Show(this, "Mốc Focus của công thức thưởng phải từ 1 đến 1440 phút.", "FocusLock");
            return;
        }
        if (!int.TryParse(RewardMinutesBox.Text.Trim(), out var rewardMinutes) ||
            rewardMinutes < 1 || rewardMinutes > 1440)
        {
            MessageBox.Show(this, "Số phút thưởng phải từ 1 đến 1440 phút.", "FocusLock");
            return;
        }

        if (DefaultPolicyCombo.SelectedItem is not PolicyOption normal || ScheduledPolicyCombo.SelectedItem is not PolicyOption scheduled || DefaultBlockActionCombo.SelectedItem is not ActionOption action) return;

        _working.Name = name;
        _working.Enabled = EnabledCheck.IsChecked == true;
        _working.PolicyVersion = 2;
        _working.DefaultAccessPolicy = normal.Value;
        _working.ScheduledAccessPolicy = scheduled.Value;
        _working.DefaultBlockAction = action.Value;
        _working.DailyAllowanceMinutes = allowance;
        _working.DailyBudgetMinutes = budgetEnabled ? dailyBudget : 0;
        _working.CooldownEnabled = cooldownEnabled;
        _working.CooldownAfterMinutes = cooldownAfterMinutes;
        _working.CooldownMinutes = cooldownMinutes;
        _working.CustomRewardEnabled = customReward;
        _working.RewardFocusMinutes = rewardFocusMinutes;
        _working.RewardMinutes = rewardMinutes;
        _working.ScheduleEnabled = ScheduleEnabledCheck.IsChecked == true;
        if (!BlockProfile.IsValidMask(_working.WeeklyScheduleMask)) _working.WeeklyScheduleMask = new string('0', 336);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static BlockProfile Clone(BlockProfile p) => new()
    {
        Id = p.Id, Name = p.Name, Enabled = p.Enabled, CreatedUtc = p.CreatedUtc,
        PolicyVersion = 2, DefaultAccessPolicy = p.DefaultAccessPolicy, ScheduledAccessPolicy = p.ScheduledAccessPolicy,
        Mode = p.Mode, DefaultBlockAction = p.DefaultBlockAction, OverrideAppBlockAction = p.OverrideAppBlockAction,
        ScheduleEnabled = p.ScheduleEnabled, WeeklyScheduleMask = p.WeeklyScheduleMask,
        ScheduleDays = p.ScheduleDays, ScheduleStart = p.ScheduleStart, ScheduleEnd = p.ScheduleEnd,
        ScheduleAbsoluteBlock = p.ScheduleAbsoluteBlock, DailyAllowanceMinutes = p.DailyAllowanceMinutes,
        AllowanceDateKey = p.AllowanceDateKey, AllowanceUsedSeconds = p.AllowanceUsedSeconds,
        DailyBudgetMinutes = p.DailyBudgetMinutes,
        EntertainmentUsageDateKey = p.EntertainmentUsageDateKey,
        EntertainmentUsedSecondsToday = p.EntertainmentUsedSecondsToday,
        CooldownEnabled = p.CooldownEnabled,
        CooldownAfterMinutes = p.CooldownAfterMinutes,
        CooldownMinutes = p.CooldownMinutes,
        CooldownProgressSeconds = p.CooldownProgressSeconds,
        CooldownUntilUtc = p.CooldownUntilUtc,
        CustomRewardEnabled = p.CustomRewardEnabled,
        RewardFocusMinutes = p.RewardFocusMinutes,
        RewardMinutes = p.RewardMinutes,
        RewardProgressSeconds = p.RewardProgressSeconds
    };
}
