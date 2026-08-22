namespace FocusLock.Shared.Models;

public sealed class BlockProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Giải trí chung";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // V7.3 professional policy model. The rule outside the selected weekly
    // calendar and the rule inside it are configured independently.
    public int PolicyVersion { get; set; } = 2;
    public ProfileAccessPolicy DefaultAccessPolicy { get; set; } = ProfileAccessPolicy.EarnedTime;
    public ProfileAccessPolicy ScheduledAccessPolicy { get; set; } = ProfileAccessPolicy.Block;

    // Legacy V7.1/V7.2 mode retained only so older state files can migrate.
    public BlockProfileMode Mode { get; set; } = BlockProfileMode.AllowanceThenEarned;

    // Default desktop behavior. Apps can opt into their own custom action.
    public EntertainmentBlockAction DefaultBlockAction { get; set; } = EntertainmentBlockAction.Close;

    // Legacy switch retained for migration; V7.3 uses TrackedApp.UseCustomBlockAction.
    public bool OverrideAppBlockAction { get; set; } = true;

    // 7 days x 48 half-hour slots = 336 chars. Sun..Sat, '1' = selected.
    public bool ScheduleEnabled { get; set; }
    public string WeeklyScheduleMask { get; set; } = "";

    // Legacy schedule fields retained for migration/backward compatibility.
    public string ScheduleDays { get; set; } = "Mon,Tue,Wed,Thu,Fri";
    public string ScheduleStart { get; set; } = "08:00";
    public string ScheduleEnd { get; set; } = "12:00";
    public bool ScheduleAbsoluteBlock { get; set; } = true;

    // Daily free allowance shared by apps + websites in this profile.
    public int DailyAllowanceMinutes { get; set; }
    public string AllowanceDateKey { get; set; } = "";
    public int AllowanceUsedSeconds { get; set; }

    public string StatusLabel => Enabled ? "Đang bật" : "Đang tạm tắt";
    public string DefaultAccessLabel => AccessPolicyLabel(DefaultAccessPolicy);
    public string ScheduledAccessLabel => AccessPolicyLabel(ScheduledAccessPolicy);

    public string ModeLabel => PolicyVersion >= 2
        ? $"Bình thường: {DefaultAccessLabel}"
        : Mode switch
        {
            BlockProfileMode.EarnedTime => "Chỉ dùng thời gian đã kiếm",
            BlockProfileMode.AllowanceThenEarned => "Allowance → ví Focus",
            BlockProfileMode.ScheduleBlock => "Khóa theo lịch",
            BlockProfileMode.ScheduleEarnedTime => "Theo lịch mới dùng ví",
            BlockProfileMode.AlwaysBlock => "Khóa luôn",
            _ => Mode.ToString()
        };

    public string DefaultBlockActionLabel => DefaultBlockAction switch
    {
        EntertainmentBlockAction.Suspend => "Tạm dừng & tự tiếp tục",
        EntertainmentBlockAction.BlockLaunch => "Chặn mở lại",
        _ => "Đóng ứng dụng"
    };

    public int SelectedScheduleSlots => IsValidMask(WeeklyScheduleMask)
        ? WeeklyScheduleMask.Count(c => c == '1')
        : 0;

    public string ScheduleLabel
    {
        get
        {
            if (!ScheduleEnabled || SelectedScheduleSlots == 0) return "Không dùng lịch tuần";
            var minutes = SelectedScheduleSlots * 30;
            return $"{SelectedScheduleSlots} ô · {minutes / 60.0:0.#} giờ/tuần";
        }
    }

    public int AllowanceRemainingSeconds
    {
        get
        {
            if (DailyAllowanceMinutes <= 0) return 0;
            var today = DateTime.Now.ToString("yyyyMMdd");
            var used = string.Equals(AllowanceDateKey, today, StringComparison.Ordinal)
                ? Math.Max(0, AllowanceUsedSeconds)
                : 0;
            return Math.Max(0, DailyAllowanceMinutes * 60 - used);
        }
    }

    public string AllowanceLabel
    {
        get
        {
            if (DailyAllowanceMinutes <= 0) return "Không có allowance";
            var t = TimeSpan.FromSeconds(AllowanceRemainingSeconds);
            var hours = (int)t.TotalHours;
            return $"Còn {hours:00}:{t.Minutes:00}:{t.Seconds:00} / {DailyAllowanceMinutes} phút hôm nay";
        }
    }

    public string PolicySummary
    {
        get
        {
            var normal = $"Ngoài lịch: {DefaultAccessLabel}";
            var selected = ScheduleEnabled && SelectedScheduleSlots > 0
                ? $" · Trong lịch: {ScheduledAccessLabel}"
                : " · Không dùng lịch";
            return $"{normal}{selected} · App mặc định: {DefaultBlockActionLabel}";
        }
    }

    public static string AccessPolicyLabel(ProfileAccessPolicy policy) => policy switch
    {
        ProfileAccessPolicy.Free => "Dùng tự do",
        ProfileAccessPolicy.EarnedTime => "Chỉ dùng ví Focus",
        ProfileAccessPolicy.AllowanceThenEarned => "Allowance → ví Focus",
        ProfileAccessPolicy.Block => "Khóa tuyệt đối",
        _ => policy.ToString()
    };

    public static bool IsValidMask(string? mask) =>
        mask is { Length: 336 } && mask.All(c => c is '0' or '1');
}
