namespace FocusLock.Shared.Models;

public sealed class TrackedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public AppCategory Category { get; set; }
    public bool Enabled { get; set; } = true;

    public string CategoryLabel => Category == AppCategory.Focus ? "Học tập / Làm việc" : "Giải trí";
    public string DisplayPath => string.IsNullOrWhiteSpace(ExePath) ? ProcessName : ExePath;

    public static TrackedApp FromPath(string path, AppCategory category, string sha256)
    {
        var full = Path.GetFullPath(path);
        return new TrackedApp
        {
            Name = Path.GetFileNameWithoutExtension(full),
            ExePath = full,
            ProcessName = Path.GetFileNameWithoutExtension(full),
            Category = category,
            Sha256 = sha256,
            Enabled = true
        };
    }
}
