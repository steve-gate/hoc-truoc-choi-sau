namespace FocusLock.Shared.Models;

public sealed class UsageSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public AppCategory Category { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime LastActiveUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public long ActiveSeconds { get; set; }
    public string EndReason { get; set; } = "";

    public string CategoryLabel => Category == AppCategory.Focus ? "Focus" : "Giải trí";
    public string DurationLabel
    {
        get
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, ActiveSeconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }
}
