using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusLock.Shared.Models;

namespace FocusLock.App;

public partial class RunningAppsWindow : Window
{
    private readonly AppCategory _category;
    private readonly List<RunningAppItem> _allApps;
    public string? SelectedPath { get; private set; }

    public RunningAppsWindow(AppCategory category)
    {
        _category = category;
        InitializeComponent();
        PickerTitleText.Text = category == AppCategory.Focus ? "Chọn ứng dụng học / làm việc" : "Chọn ứng dụng giải trí";
        PickerSubtitleText.Text = category == AppCategory.Focus
            ? "Chọn ứng dụng bạn đang dùng để học hoặc làm việc."
            : "Chọn game hoặc ứng dụng bạn muốn giới hạn thời gian.";
        _allApps = ReadRunningApps();
        ApplyFilter();
    }

    private static List<RunningAppItem> ReadRunningApps()
    {
        var result = new Dictionary<string, RunningAppItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    if (Path.GetFileName(path).Equals("FocusLock.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    result.TryAdd(path, new RunningAppItem
                    {
                        Name = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? process.ProcessName : process.MainWindowTitle,
                        ProcessName = process.ProcessName,
                        Path = path
                    });
                }
            }
            catch { }
        }
        return result.Values.OrderBy(x => x.Name).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = SearchBox?.Text?.Trim() ?? "";
        var items = string.IsNullOrWhiteSpace(q)
            ? _allApps
            : _allApps.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        AppsList.ItemsSource = items;
        CountText.Text = $"{items.Count} ứng dụng đang mở";
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => ChooseSelected();
    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ChooseSelected();

    private void ChooseSelected()
    {
        if (AppsList.SelectedItem is not RunningAppItem item)
        {
            MessageBox.Show(this, "Hãy chọn một ứng dụng trong danh sách.", "FocusLock");
            return;
        }
        SelectedPath = item.Path;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class RunningAppItem
    {
        public string Name { get; init; } = "";
        public string ProcessName { get; init; } = "";
        public string Path { get; init; } = "";
    }
}
