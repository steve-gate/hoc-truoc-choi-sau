using System.Windows;
using System.Windows.Controls;
using FocusLock.Shared.Models;

namespace FocusLock.App;

public partial class AppPolicyWindow : Window
{
    private sealed record ActionOption(bool UseCustom, EntertainmentBlockAction Value, string Label, string Help);
    private readonly List<ActionOption> _actions = new()
    {
        new(false, EntertainmentBlockAction.Close, "Theo mặc định của Profile (khuyên dùng)", "Profile quyết định cách khóa. Dễ quản lý và nhất quán nhất."),
        new(true, EntertainmentBlockAction.Close, "Riêng: Đóng ứng dụng", "Đóng process khi hết quyền dùng. Khuyên dùng cho game và app có anti-cheat."),
        new(true, EntertainmentBlockAction.Suspend, "Riêng: Tạm dừng & tự tiếp tục", "Đóng băng process nhưng giữ phiên. Khi được phép lại, FocusLock tự resume."),
    };

    public string SelectedProfileId { get; private set; } = "";
    public bool UseCustomBlockAction { get; private set; }
    public EntertainmentBlockAction SelectedBlockAction { get; private set; }

    public AppPolicyWindow(TrackedApp app, IEnumerable<BlockProfile> profiles)
    {
        InitializeComponent();
        TitleText.Text = app.Name;
        var list = profiles.OrderBy(p => p.CreatedUtc).ToList();
        ProfileCombo.ItemsSource = list;
        ProfileCombo.SelectedItem = list.FirstOrDefault(p => p.Id == app.BlockProfileId) ?? list.FirstOrDefault();
        ActionCombo.ItemsSource = _actions;
        ActionCombo.SelectedItem = _actions.FirstOrDefault(x => x.UseCustom == app.UseCustomBlockAction && (!x.UseCustom || x.Value == app.BlockAction)) ?? _actions[0];
        ActionCombo.SelectionChanged += (_, _) => RefreshHelp();
        RefreshHelp();
    }

    private void RefreshHelp() => ActionHelpText.Text = (ActionCombo.SelectedItem as ActionOption)?.Help ?? "";
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not BlockProfile profile || ActionCombo.SelectedItem is not ActionOption action) return;
        SelectedProfileId = profile.Id;
        UseCustomBlockAction = action.UseCustom;
        SelectedBlockAction = action.Value;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
