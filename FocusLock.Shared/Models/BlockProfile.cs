using System.Text.Json.Serialization;

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

    // V7.7.3: hard daily entertainment ceiling shared by every app + website
    // in this profile. 0 = unlimited. Usage is tracked even while unlimited,
    // so enabling a budget mid-day does not erase what was already consumed.
    public int DailyBudgetMinutes { get; set; }
    public string EntertainmentUsageDateKey { get; set; } = "";
    public int EntertainmentUsedSecondsToday { get; set; }

    // V7.7.7: enforced recovery cycle for entertainment in this Profile.
    // After N accumulated entertainment minutes, the whole Profile enters a
    // cooldown for M minutes. The timer is persisted so restarting FocusLock
    // does not bypass the break.
    public bool CooldownEnabled { get; set; }
    public int CooldownAfterMinutes { get; set; } = 30;
    public int CooldownMinutes { get; set; } = 10;
    public int CooldownProgressSeconds { get; set; }
    public DateTime? CooldownUntilUtc { get; set; }

    [JsonIgnore]
    public int CooldownTargetSeconds => Math.Max(1, CooldownAfterMinutes) * 60;

    [JsonIgnore]
    public bool CooldownActive => CooldownUntilUtc is DateTime until && until > DateTime.UtcNow;

    [JsonIgnore]
    public int CooldownRemainingSeconds => !CooldownActive
        ? 0
        : Math.Max(1, (int)Math.Ceiling((CooldownUntilUtc!.Value - DateTime.UtcNow).TotalSeconds));

    [JsonIgnore]
    public string CooldownLabel
    {
        get
        {
            if (CooldownActive)
            {
                var left = TimeSpan.FromSeconds(CooldownRemainingSeconds);
                return $"Cooldown đang chạy · còn {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            }
            if (!CooldownEnabled) return "Cooldown: tắt";

            var target = Math.Max(60, CooldownTargetSeconds);
            var progress = Math.Clamp(CooldownProgressSeconds, 0, Math.Max(0, target - 1));
            var leftToBreak = Math.Max(0, target - progress);
            var untilBreak = TimeSpan.FromSeconds(leftToBreak);
            return $"Sau {Math.Max(1, CooldownAfterMinutes)} phút giải trí → nghỉ {Math.Max(1, CooldownMinutes)} phút · còn {(int)untilBreak.TotalHours:00}:{untilBreak.Minutes:00}:{untilBreak.Seconds:00} tới lần nghỉ";
        }
    }

    // V7.7.6: optional reward formula for Focus sources assigned to this Profile.
    // When disabled, assigned Focus sources fall back to the global reward rule.
    public bool CustomRewardEnabled { get; set; }
    public int RewardFocusMinutes { get; set; } = 30;
    public int RewardMinutes { get; set; } = 10;
    public int RewardProgressSeconds { get; set; }

    public int RewardTargetSeconds => Math.Max(1, RewardFocusMinutes) * 60;
    public int RewardSecondsPerKey => Math.Max(1, RewardMinutes) * 60;

    public string RewardRuleLabel => CustomRewardEnabled
        ? $"{Math.Max(1, RewardFocusMinutes)} phút Focus → +{Math.Max(1, RewardMinutes)} phút"
        : "Dùng công thức thưởng chung";

    public string RewardProgressLabel
    {
        get
        {
            if (!CustomRewardEnabled) return "Tiến độ theo công thức chung";
            var target = RewardTargetSeconds;
            var progress = Math.Clamp(RewardProgressSeconds, 0, Math.Max(0, target - 1));
            var left = Math.Max(0, target - progress);
            var p = TimeSpan.FromSeconds(progress);
            var l = TimeSpan.FromSeconds(left);
            return $"{(int)p.TotalHours:00}:{p.Minutes:00}:{p.Seconds:00} / {RewardFocusMinutes} phút · còn {(int)l.TotalHours:00}:{l.Minutes:00}:{l.Seconds:00}";
        }
    }

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

    public int EntertainmentUsedTodaySeconds
    {
        get
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            return string.Equals(EntertainmentUsageDateKey, today, StringComparison.Ordinal)
                ? Math.Max(0, EntertainmentUsedSecondsToday)
                : 0;
        }
    }

    public int DailyBudgetRemainingSeconds =>
        DailyBudgetMinutes <= 0
            ? int.MaxValue
            : Math.Max(0, DailyBudgetMinutes * 60 - EntertainmentUsedTodaySeconds);

    public string DailyBudgetLabel
    {
        get
        {
            var used = TimeSpan.FromSeconds(EntertainmentUsedTodaySeconds);
            var usedHours = (int)used.TotalHours;
            var usedText = $"{usedHours:00}:{used.Minutes:00}:{used.Seconds:00}";

            if (DailyBudgetMinutes <= 0)
                return $"Không giới hạn/ngày · hôm nay đã dùng {usedText}";

            var remain = TimeSpan.FromSeconds(DailyBudgetRemainingSeconds);
            var remainHours = (int)remain.TotalHours;
            return $"Hôm nay {usedText} / {DailyBudgetMinutes} phút · còn {remainHours:00}:{remain.Minutes:00}:{remain.Seconds:00}";
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
            var budget = DailyBudgetMinutes > 0
                ? $" · Trần ngày: {DailyBudgetMinutes} phút"
                : "";
            var cooldown = CooldownEnabled
                ? $" · Cooldown: {Math.Max(1, CooldownAfterMinutes)}→nghỉ {Math.Max(1, CooldownMinutes)} phút"
                : "";
            var reward = CustomRewardEnabled
                ? $" · Thưởng: {RewardFocusMinutes}→+{RewardMinutes} phút"
                : "";
            return $"{normal}{selected}{budget}{cooldown}{reward} · App mặc định: {DefaultBlockActionLabel}";
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
