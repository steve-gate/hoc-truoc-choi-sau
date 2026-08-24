using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using FocusLock.App.Services;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;
using FocusLock.Shared.Utilities;

namespace FocusLock.App;

public partial class ProfileCenterWindow : Window
{
    private sealed class ProfileRow
    {
        public required BlockProfile Profile { get; init; }
        public required string Name { get; init; }
        public required string Status { get; init; }
        public required string Members { get; init; }
        public required string ShortPolicy { get; init; }
    }

    private readonly ServiceClient _client = new();
    private ServiceSnapshot? _snapshot;

    public ProfileCenterWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private ProfileRow? SelectedRow => ProfilesList.SelectedItem as ProfileRow;

    private async Task RefreshAsync(string? selectProfileId = null)
    {
        var response = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        if (!response.Ok || response.Snapshot is null)
        {
            StatusText.Text = response.Message;
            return;
        }

        _snapshot = response.Snapshot;
        var s = _snapshot.State;
        var rows = s.BlockProfiles.OrderBy(p => p.CreatedUtc).Select(p => new ProfileRow
        {
            Profile = p,
            Name = p.Name,
            Status = p.StatusLabel,
            Members =
                $"Giải trí {s.Apps.Count(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)} app/{s.BrowserRules.Count(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)} web · " +
                $"Focus {s.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id)} app/{s.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id)} web",
            ShortPolicy = p.PolicySummary
        }).ToList();

        ProfilesList.ItemsSource = rows;
        ProfilesList.SelectedItem = rows.FirstOrDefault(x => x.Profile.Id == selectProfileId) ?? rows.FirstOrDefault();
        RenderSelected();
        StatusText.Text = $"{rows.Count} Profile · mỗi Profile có policy, cooldown, nguồn Focus và công thức thưởng riêng.";
    }

    private void RenderSelected()
    {
        var row = SelectedRow;
        if (row is null || _snapshot is null)
        {
            SelectedNameText.Text = "Chọn một Profile";
            SelectedStatusText.Text = PolicyText.Text = ScheduleText.Text = AllowanceText.Text = DailyBudgetText.Text = CooldownText.Text = RewardPolicyText.Text = BlockActionText.Text = "—";
            AppsMembersList.ItemsSource = WebMembersList.ItemsSource = null;
            return;
        }

        var p = row.Profile;
        SelectedNameText.Text = p.Name;
        SelectedStatusText.Text = p.StatusLabel;
        PolicyText.Text = $"Ngoài lịch: {p.DefaultAccessLabel}\nTrong lịch: {p.ScheduledAccessLabel}";
        ScheduleText.Text = p.ScheduleLabel;
        AllowanceText.Text = p.AllowanceLabel;
        DailyBudgetText.Text = "Ngân sách ngày: " + p.DailyBudgetLabel;
        CooldownText.Text = p.CooldownLabel;
        var focusSourceCount =
            _snapshot.State.Apps.Count(a => a.Category == AppCategory.Focus && a.BlockProfileId == p.Id) +
            _snapshot.State.BrowserRules.Count(r => r.Category == AppCategory.Focus && r.BlockProfileId == p.Id);
        RewardPolicyText.Text = $"Thưởng: {p.RewardRuleLabel} · {focusSourceCount} nguồn Focus · {p.RewardProgressLabel}";
        BlockActionText.Text = p.DefaultBlockActionLabel;

        var appHint = !string.IsNullOrWhiteSpace(_snapshot.LastExternalAppPath)
            ? $"App vừa dùng: {_snapshot.LastExternalAppName}"
            : "App vừa dùng: chưa có dữ liệu";
        var webHint = !string.IsNullOrWhiteSpace(_snapshot.CurrentBrowserHost) &&
                      _snapshot.CurrentBrowserHost != "—"
            ? $"Website: {_snapshot.CurrentBrowserHost}"
            : "Website: chưa có dữ liệu";
        QuickAddContextText.Text = $"{appHint} · {webHint}";

        var apps = _snapshot.State.Apps
            .Where(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)
            .OrderBy(a => a.Name)
            .Select(a => $"▣  {a.Name}   ·   {a.BlockActionPolicyLabel}")
            .ToList();
        var web = _snapshot.State.BrowserRules
            .Where(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)
            .OrderBy(r => r.DisplayName)
            .Select(r => $"◎  {r.DisplayName}   ·   {r.MatchTypeLabel}   ·   {r.Pattern}")
            .ToList();

        AppsHeaderText.Text = $"Ứng dụng giải trí ({apps.Count})";
        WebHeaderText.Text = $"Website giải trí ({web.Count})";
        AppsMembersList.ItemsSource = apps;
        WebMembersList.ItemsSource = web;
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelected();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(SelectedRow?.Profile.Id);

    private async void CreateProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = NewProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Nhập tên Profile, ví dụ Game, Social, Video.", "FocusLock");
            return;
        }

        var response = await _client.SendAsync(new PipeRequest
        {
            Command = "addBlockProfile",
            BlockProfile = new BlockProfile
            {
                Name = name,
                PolicyVersion = 2,
                DefaultAccessPolicy = ProfileAccessPolicy.EarnedTime,
                ScheduledAccessPolicy = ProfileAccessPolicy.Block,
                DefaultBlockAction = EntertainmentBlockAction.Close
            }
        });
        StatusText.Text = response.Message;
        if (!response.Ok) return;

        NewProfileNameBox.Clear();
        var id = response.Snapshot?.State.BlockProfiles
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
        await RefreshAsync(id);
    }

    private async void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || _snapshot is null) return;
        var profile = row.Profile;
        var editor = new ProfileEditorWindow(profile, _snapshot.State.Apps, _snapshot.State.BrowserRules) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var response = await _client.SendAsync(new PipeRequest { Command = "updateBlockProfile", BlockProfile = editor.EditedProfile });
        if (!response.Ok)
        {
            StatusText.Text = response.Message;
            return;
        }

        var fallback = _snapshot.State.BlockProfiles.FirstOrDefault(p => p.Id != profile.Id && string.Equals(p.Name, "Giải trí chung", StringComparison.OrdinalIgnoreCase))
                       ?? _snapshot.State.BlockProfiles.FirstOrDefault(p => p.Id != profile.Id);

        foreach (var member in editor.AppMembers)
        {
            if (member.IsMember)
                await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = member.Id, BlockProfileId = profile.Id });
            else if (member.WasMember && fallback is not null)
                await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = member.Id, BlockProfileId = fallback.Id });
        }

        foreach (var member in editor.WebsiteMembers)
        {
            if (member.IsMember)
                await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = member.Id, BlockProfileId = profile.Id });
            else if (member.WasMember && fallback is not null)
                await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = member.Id, BlockProfileId = fallback.Id });
        }

        foreach (var source in editor.FocusAppSources)
        {
            if (source.IsMember)
                await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = source.Id, BlockProfileId = profile.Id });
            else if (source.WasMember)
                await _client.SendAsync(new PipeRequest { Command = "setAppProfile", AppId = source.Id, BlockProfileId = "" });
        }

        foreach (var source in editor.FocusWebsiteSources)
        {
            if (source.IsMember)
                await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = source.Id, BlockProfileId = profile.Id });
            else if (source.WasMember)
                await _client.SendAsync(new PipeRequest { Command = "setBrowserProfile", BrowserRuleId = source.Id, BlockProfileId = "" });
        }

        await RefreshAsync(profile.Id);
        StatusText.Text = $"Đã áp dụng Profile {editor.EditedProfile.Name}: giải trí + cooldown + nguồn Focus + công thức thưởng.";
    }

    private async void QuickAddLastApp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        // Refresh immediately so Quick Add uses Guard's newest remembered external app.
        var latest = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        if (!latest.Ok || latest.Snapshot is null)
        {
            MessageBox.Show(this, latest.Message, "Quick Add",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _snapshot = latest.Snapshot;
        var path = _snapshot.LastExternalAppPath;
        var processName = _snapshot.LastExternalAppName;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this,
                "FocusLock chưa ghi nhận được app bên ngoài vừa dùng. Hãy mở app cần thêm, sử dụng nó vài giây rồi quay lại Profile Center.",
                "Quick Add · App",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (IsBrowserExecutable(processName, path))
        {
            MessageBox.Show(this,
                $"App vừa dùng là trình duyệt ({Path.GetFileName(path)}). Không nên thêm cả trình duyệt vào Profile giải trí vì sẽ khóa mọi website.\n\nHãy dùng nút “+ Website đang mở” bên cạnh.",
                "Quick Add · Trình duyệt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await AddAppToSelectedProfileAsync(path, quickAdd: true);
    }

    private async void QuickAddCurrentWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null) return;

        var latest = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        if (!latest.Ok || latest.Snapshot is null)
        {
            MessageBox.Show(this, latest.Message, "Quick Add",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _snapshot = latest.Snapshot;
        var type = GetQuickWebsiteMatchType();
        var pattern = BrowserRuleUrlHelper.PatternFromCurrentPage(
            type,
            _snapshot.CurrentBrowserUrl,
            _snapshot.CurrentBrowserHost,
            _snapshot.CurrentBrowserTitle);

        if (string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(this,
                "FocusLock chưa có URL website gần nhất từ Browser Bridge. Hãy mở website cần thêm, chờ FocusLock nhận ra nó rồi quay lại bấm Quick Add.",
                "Quick Add · Website",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await AddWebsiteToSelectedProfileAsync(pattern, type, quickAdd: true);
    }

    private BrowserRuleMatchType GetQuickWebsiteMatchType()
    {
        if (QuickWebsiteScopeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<BrowserRuleMatchType>(tag, true, out var type))
            return type;

        return BrowserRuleMatchType.HostSuffix;
    }

    private static bool IsBrowserExecutable(string? processName, string? path)
    {
        var name = (processName ?? Path.GetFileNameWithoutExtension(path ?? "")).Trim();
        return name.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("browser", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("coccoc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);
    }

    private async void AddExeApp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Ứng dụng Windows (*.exe)|*.exe",
            Multiselect = false,
            Title = $"Thêm ứng dụng giải trí vào {row.Profile.Name}"
        };
        if (dlg.ShowDialog(this) != true) return;

        await AddAppToSelectedProfileAsync(dlg.FileName);
    }

    private async void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null) return;

        var picker = new RunningAppsWindow(AppCategory.Entertainment) { Owner = this };
        if (picker.ShowDialog() != true || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;

        await AddAppToSelectedProfileAsync(picker.SelectedPath);
    }

    private async Task AddAppToSelectedProfileAsync(string path, bool quickAdd = false)
    {
        if (SelectedRow is not { } row || _snapshot is null) return;

        try
        {
            var full = Path.GetFullPath(path);
            var existing = _snapshot.State.Apps.FirstOrDefault(a =>
                a.Category == AppCategory.Entertainment &&
                !string.IsNullOrWhiteSpace(a.ExePath) &&
                string.Equals(
                    Path.GetFullPath(a.ExePath).TrimEnd('\\'),
                    full.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase));

            PipeResponse response;
            if (existing is not null)
            {
                response = await _client.SendAsync(new PipeRequest
                {
                    Command = "setAppProfile",
                    AppId = existing.Id,
                    BlockProfileId = row.Profile.Id
                });
            }
            else
            {
                var app = TrackedApp.FromPath(full, AppCategory.Entertainment, FileHashService.TrySha256(full));
                app.BlockProfileId = row.Profile.Id;
                app.BlockProfileName = row.Profile.Name;

                response = await _client.SendAsync(new PipeRequest
                {
                    Command = "addApp",
                    App = app
                });
            }

            if (!response.Ok)
            {
                MessageBox.Show(this, response.Message, "Không thể thêm ứng dụng",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = response.Message;
                return;
            }

            var displayName = existing?.Name ?? Path.GetFileNameWithoutExtension(full);
            await RefreshAsync(row.Profile.Id);
            StatusText.Text = existing is null
                ? $"✓ Đã thêm {displayName} vào Profile {row.Profile.Name}."
                : $"✓ Đã chuyển {displayName} sang Profile {row.Profile.Name}.";

            if (quickAdd)
                QuickAddContextText.Text = $"✓ App: {displayName} → {row.Profile.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Không thể thêm ứng dụng",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void UseCurrentWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null) return;

        var latest = await _client.SendAsync(new PipeRequest { Command = "snapshot" });
        if (!latest.Ok || latest.Snapshot is null)
        {
            MessageBox.Show(this, latest.Message, "Website hiện tại",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _snapshot = latest.Snapshot;
        var type = GetSelectedWebsiteMatchType();
        var pattern = BrowserRuleUrlHelper.PatternFromCurrentPage(
            type,
            _snapshot.CurrentBrowserUrl,
            _snapshot.CurrentBrowserHost,
            _snapshot.CurrentBrowserTitle);

        if (string.IsNullOrWhiteSpace(pattern))
        {
            MessageBox.Show(this,
                "FocusLock chưa nhận được website/URL hiện tại phù hợp với phạm vi đã chọn.",
                "Website hiện tại",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        NewWebsiteBox.Text = pattern;
        StatusText.Text = $"Đã lấy trang đang mở theo phạm vi: {WebsiteMatchTypeLabel(type)}. Kiểm tra rồi bấm + Thêm vào Profile.";
    }

    private async void AddWebsite_Click(object sender, RoutedEventArgs e)
    {
        await AddWebsiteToSelectedProfileAsync(NewWebsiteBox.Text);
    }

    private Task AddWebsiteToSelectedProfileAsync(string raw)
        => AddWebsiteToSelectedProfileAsync(raw, GetSelectedWebsiteMatchType(), quickAdd: false);

    private async Task AddWebsiteToSelectedProfileAsync(
        string raw,
        BrowserRuleMatchType type,
        bool quickAdd)
    {
        if (SelectedRow is not { } row || _snapshot is null) return;

        var pattern = BrowserRuleUrlHelper.NormalizePattern(raw, type);

        if (string.IsNullOrWhiteSpace(pattern))
        {
            var example = type == BrowserRuleMatchType.HostSuffix
                ? "youtube.com"
                : "https://www.youtube.com/@kenh-hoc";
            MessageBox.Show(this,
                $"Nội dung không hợp lệ cho phạm vi {WebsiteMatchTypeLabel(type)}. Ví dụ: {example}",
                "Thêm website",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var conflict = _snapshot.State.BrowserRules.FirstOrDefault(r =>
            r.MatchType == type &&
            string.Equals(
                BrowserRuleUrlHelper.NormalizePattern(r.Pattern, r.MatchType),
                pattern,
                StringComparison.OrdinalIgnoreCase));

        if (conflict is not null && conflict.Category != AppCategory.Entertainment)
        {
            MessageBox.Show(this,
                $"Rule này hiện đang được phân loại là {conflict.CategoryLabel}. Hãy sửa/xóa rule đó ở trang Website trước khi chuyển nó thành Giải trí.",
                "Rule đang tồn tại",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        PipeResponse response;
        if (conflict is not null)
        {
            response = await _client.SendAsync(new PipeRequest
            {
                Command = "setBrowserProfile",
                BrowserRuleId = conflict.Id,
                BlockProfileId = row.Profile.Id
            });
        }
        else
        {
            var display = type == BrowserRuleMatchType.HostSuffix
                ? pattern
                : BrowserRuleUrlHelper.NormalizeHost(pattern);

            var rule = new BrowserRule
            {
                Name = string.IsNullOrWhiteSpace(display) ? pattern : display,
                Pattern = pattern,
                MatchType = type,
                Category = AppCategory.Entertainment,
                Enabled = true,
                BlockProfileId = row.Profile.Id,
                BlockProfileName = row.Profile.Name
            };

            response = await _client.SendAsync(new PipeRequest
            {
                Command = "addBrowserRule",
                BrowserRule = rule
            });
        }

        if (!response.Ok)
        {
            MessageBox.Show(this, response.Message, "Không thể thêm website",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = response.Message;
            return;
        }

        if (!quickAdd)
            NewWebsiteBox.Clear();

        var displayRule = conflict?.DisplayName ??
                          (type == BrowserRuleMatchType.HostSuffix
                              ? pattern
                              : BrowserRuleUrlHelper.NormalizeHost(pattern));

        await RefreshAsync(row.Profile.Id);
        StatusText.Text = conflict is null
            ? $"✓ Đã thêm {displayRule} ({WebsiteMatchTypeLabel(type)}) vào Profile {row.Profile.Name}."
            : $"✓ Đã chuyển {displayRule} sang Profile {row.Profile.Name}.";

        if (quickAdd)
            QuickAddContextText.Text = $"✓ Website: {displayRule} → {row.Profile.Name}";
    }

    private BrowserRuleMatchType GetSelectedWebsiteMatchType()
    {
        if (WebsiteScopeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<BrowserRuleMatchType>(tag, true, out var type))
            return type;

        return BrowserRuleMatchType.HostSuffix;
    }

    private static string WebsiteMatchTypeLabel(BrowserRuleMatchType type) => type switch
    {
        BrowserRuleMatchType.HostSuffix => "Cả website / domain",
        BrowserRuleMatchType.UrlPrefix => "URL bắt đầu bằng",
        BrowserRuleMatchType.ExactUrl => "Chính xác trang / URL",
        _ => type.ToString()
    };

    private async void ToggleSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        var response = await _client.SendAsync(new PipeRequest { Command = "toggleBlockProfile", BlockProfileId = row.Profile.Id });
        StatusText.Text = response.Message;
        await RefreshAsync(row.Profile.Id);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        if (MessageBox.Show(this, $"Xóa Profile {row.Profile.Name}? Thành viên sẽ chuyển về Profile dự phòng.", "FocusLock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var response = await _client.SendAsync(new PipeRequest { Command = "removeBlockProfile", BlockProfileId = row.Profile.Id });
        StatusText.Text = response.Message;
        await RefreshAsync();
    }
}
