using System.Windows;
using System.Windows.Controls;
using FocusLock.App.Services;
using FocusLock.Shared.Models;
using FocusLock.Shared.Protocol;

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
            Members = $"{s.Apps.Count(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)} app · {s.BrowserRules.Count(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)} website",
            ShortPolicy = p.PolicySummary
        }).ToList();

        ProfilesList.ItemsSource = rows;
        ProfilesList.SelectedItem = rows.FirstOrDefault(x => x.Profile.Id == selectProfileId) ?? rows.FirstOrDefault();
        RenderSelected();
        StatusText.Text = $"{rows.Count} Profile · mọi App/Website giải trí được áp policy từ Profile đang chứa chúng.";
    }

    private void RenderSelected()
    {
        var row = SelectedRow;
        if (row is null || _snapshot is null)
        {
            SelectedNameText.Text = "Chọn một Profile";
            SelectedStatusText.Text = PolicyText.Text = ScheduleText.Text = AllowanceText.Text = BlockActionText.Text = "—";
            AppsMembersList.ItemsSource = WebMembersList.ItemsSource = null;
            return;
        }

        var p = row.Profile;
        SelectedNameText.Text = p.Name;
        SelectedStatusText.Text = p.StatusLabel;
        PolicyText.Text = $"Ngoài lịch: {p.DefaultAccessLabel}\nTrong lịch: {p.ScheduledAccessLabel}";
        ScheduleText.Text = p.ScheduleLabel;
        AllowanceText.Text = p.AllowanceLabel;
        BlockActionText.Text = p.DefaultBlockActionLabel;

        var apps = _snapshot.State.Apps
            .Where(a => a.Category == AppCategory.Entertainment && a.BlockProfileId == p.Id)
            .OrderBy(a => a.Name)
            .Select(a => $"▣  {a.Name}   ·   {a.BlockActionPolicyLabel}")
            .ToList();
        var web = _snapshot.State.BrowserRules
            .Where(r => r.Category == AppCategory.Entertainment && r.BlockProfileId == p.Id)
            .OrderBy(r => r.DisplayName)
            .Select(r => $"◎  {r.DisplayName}   ·   {r.Pattern}")
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

        await RefreshAsync(profile.Id);
        StatusText.Text = $"Đã áp dụng Profile {editor.EditedProfile.Name} cho App + Website thành viên.";
    }

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
