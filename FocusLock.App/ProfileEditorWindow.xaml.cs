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

        NameBox.Text = _working.Name;
        EnabledCheck.IsChecked = _working.Enabled;
        AllowanceBox.Text = _working.DailyAllowanceMinutes.ToString();
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
        RefreshHelp();
        RefreshScheduleSummary();
    }

    private void RefreshHelp()
    {
        DefaultPolicyHelp.Text = (DefaultPolicyCombo.SelectedItem as PolicyOption)?.Help ?? "";
        ScheduledPolicyHelp.Text = (ScheduledPolicyCombo.SelectedItem as PolicyOption)?.Help ?? "";
        BlockActionHelp.Text = (DefaultBlockActionCombo.SelectedItem as ActionOption)?.Help ?? "";
    }

    private void RefreshMemberCount() => MemberCountText.Text = $"{_apps.Count(x => x.IsMember)} app · {_websites.Count(x => x.IsMember)} website";
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
        if (DefaultPolicyCombo.SelectedItem is not PolicyOption normal || ScheduledPolicyCombo.SelectedItem is not PolicyOption scheduled || DefaultBlockActionCombo.SelectedItem is not ActionOption action) return;

        _working.Name = name;
        _working.Enabled = EnabledCheck.IsChecked == true;
        _working.PolicyVersion = 2;
        _working.DefaultAccessPolicy = normal.Value;
        _working.ScheduledAccessPolicy = scheduled.Value;
        _working.DefaultBlockAction = action.Value;
        _working.DailyAllowanceMinutes = allowance;
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
        AllowanceDateKey = p.AllowanceDateKey, AllowanceUsedSeconds = p.AllowanceUsedSeconds
    };
}
