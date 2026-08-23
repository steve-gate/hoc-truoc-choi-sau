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

    // V7: per-app entertainment policy. Focus apps ignore these values.
    public EntertainmentBlockAction BlockAction { get; set; } = EntertainmentBlockAction.Close;
    // V7.3: normally the profile decides the block action. Enable this only for a special app.
    public bool UseCustomBlockAction { get; set; }
    public string BlockProfileId { get; set; } = "";
    public string BlockProfileName { get; set; } = "Giải trí chung";

    public string CategoryLabel => Category == AppCategory.Focus ? "Học tập / Làm việc" : "Giải trí";
    public string DisplayPath => string.IsNullOrWhiteSpace(ExePath) ? ProcessName : ExePath;
    public string BlockActionLabel => BlockAction switch
    {
        EntertainmentBlockAction.Suspend => "Tạm dừng & tự tiếp tục",
        EntertainmentBlockAction.BlockLaunch => "Chặn mở lại",
        _ => "Đóng ứng dụng"
    };
    public string BlockProfileLabel => Category == AppCategory.Focus
        ? string.IsNullOrWhiteSpace(BlockProfileId)
            ? "Công thức chung"
            : string.IsNullOrWhiteSpace(BlockProfileName) ? "Profile Focus" : BlockProfileName
        : string.IsNullOrWhiteSpace(BlockProfileName) ? "Giải trí chung" : BlockProfileName;
    public string BlockActionPolicyLabel => UseCustomBlockAction ? $"Riêng: {BlockActionLabel}" : "Theo Profile";

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
            Enabled = true,
            BlockAction = EntertainmentBlockAction.Close
        };
    }
}
