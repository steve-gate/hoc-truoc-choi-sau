using System.Windows;
using FocusLock.Shared.Models;

namespace FocusLock.App;

public partial class ProfileAssignmentWindow : Window
{
    public string SelectedProfileId { get; private set; } = "";
    public ProfileAssignmentWindow(string itemName, string currentProfileId, IEnumerable<BlockProfile> profiles)
    {
        InitializeComponent();
        TitleText.Text = itemName;
        var list = profiles.OrderBy(p => p.CreatedUtc).ToList();
        ProfileCombo.ItemsSource = list;
        ProfileCombo.SelectedItem = list.FirstOrDefault(p => p.Id == currentProfileId) ?? list.FirstOrDefault();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not BlockProfile profile) return;
        SelectedProfileId = profile.Id;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
