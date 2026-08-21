namespace FocusLock.Shared.Models;

public sealed class AppUsageStat
{
    public string DateKey { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public AppCategory Category { get; set; }
    public long ActiveSeconds { get; set; }

    public string CategoryLabel => Category == AppCategory.Focus ? "Học tập / Làm việc" : "Giải trí";
}
