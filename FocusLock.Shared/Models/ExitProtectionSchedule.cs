using System.Text.Json.Serialization;

namespace FocusLock.Shared.Models;

public enum ExitProtectionScheduleType
{
    OneTime = 0,
    Daily = 1,
    Weekly = 2
}

public sealed class ExitProtectionSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Không thể tắt FocusLock";
    public bool Enabled { get; set; } = true;
    public ExitProtectionScheduleType Type { get; set; } = ExitProtectionScheduleType.Daily;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // One-time window is persisted in UTC so changing locale/DST display settings
    // does not silently move an already committed interval.
    public DateTime? OneTimeStartUtc { get; set; }
    public DateTime? OneTimeEndUtc { get; set; }

    // Recurring schedules use local wall clock. Start == End is invalid and is
    // rejected by the Guard rather than being interpreted as a 24-hour lock.
    public string StartTime { get; set; } = "08:00";
    public string EndTime { get; set; } = "17:00";

    // Sunday..Saturday; '1' means that start-day is selected. For overnight
    // windows (for example Mon 22:00 -> Tue 06:00), the early-Tuesday portion
    // belongs to Monday's selected occurrence.
    public string WeeklyDaysMask { get; set; } = "0111110";

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        ExitProtectionScheduleType.OneTime => "Một lần",
        ExitProtectionScheduleType.Daily => "Hàng ngày",
        ExitProtectionScheduleType.Weekly => "Theo thứ",
        _ => Type.ToString()
    };

    [JsonIgnore]
    public bool IsActiveNow => IsActive(DateTime.Now, DateTime.UtcNow);

    [JsonIgnore]
    public DateTime? ActiveUntilLocal => GetActiveUntilLocal(DateTime.Now, DateTime.UtcNow);

    [JsonIgnore]
    public string DaysLabel
    {
        get
        {
            if (Type != ExitProtectionScheduleType.Weekly) return "—";
            var names = new[] { "CN", "T2", "T3", "T4", "T5", "T6", "T7" };
            var selected = Enumerable.Range(0, 7)
                .Where(IsDaySelected)
                .Select(i => names[i])
                .ToArray();
            return selected.Length == 0 ? "Chưa chọn ngày" : string.Join(", ", selected);
        }
    }

    [JsonIgnore]
    public string ScheduleLabel
    {
        get
        {
            if (Type == ExitProtectionScheduleType.OneTime)
            {
                if (OneTimeStartUtc is not DateTime start || OneTimeEndUtc is not DateTime end)
                    return "Chưa đủ thời gian";
                return $"{start.ToLocalTime():dd/MM/yyyy HH:mm} → {end.ToLocalTime():dd/MM/yyyy HH:mm}";
            }

            var prefix = Type == ExitProtectionScheduleType.Weekly ? DaysLabel + " · " : "Mỗi ngày · ";
            return $"{prefix}{StartTime} → {EndTime}";
        }
    }

    [JsonIgnore]
    public string StatusLabel
    {
        get
        {
            if (!Enabled) return "Đang tắt";
            if (IsActiveNow)
            {
                var until = ActiveUntilLocal;
                return until is DateTime value
                    ? $"ĐANG KHÓA THOÁT · tới {value:dd/MM HH:mm}"
                    : "ĐANG KHÓA THOÁT";
            }
            if (Type == ExitProtectionScheduleType.OneTime && OneTimeEndUtc is DateTime end && end <= DateTime.UtcNow)
                return "Đã kết thúc";
            return "Đã bật";
        }
    }

    public bool IsActive(DateTime localNow, DateTime utcNow)
    {
        if (!Enabled) return false;

        if (Type == ExitProtectionScheduleType.OneTime)
            return OneTimeStartUtc is DateTime start &&
                   OneTimeEndUtc is DateTime end &&
                   start < end &&
                   utcNow >= start && utcNow < end;

        if (!TryParseClock(StartTime, out var startClock) ||
            !TryParseClock(EndTime, out var endClock) ||
            startClock == endClock)
            return false;

        var time = TimeOnly.FromDateTime(localNow);
        if (startClock < endClock)
        {
            if (time < startClock || time >= endClock) return false;
            return Type == ExitProtectionScheduleType.Daily || IsDaySelected((int)localNow.DayOfWeek);
        }

        // Overnight occurrence.
        if (time >= startClock)
            return Type == ExitProtectionScheduleType.Daily || IsDaySelected((int)localNow.DayOfWeek);

        if (time < endClock)
        {
            var previous = (int)localNow.AddDays(-1).DayOfWeek;
            return Type == ExitProtectionScheduleType.Daily || IsDaySelected(previous);
        }

        return false;
    }

    public DateTime? GetActiveUntilLocal(DateTime localNow, DateTime utcNow)
    {
        if (!IsActive(localNow, utcNow)) return null;

        if (Type == ExitProtectionScheduleType.OneTime)
            return OneTimeEndUtc?.ToLocalTime();

        if (!TryParseClock(StartTime, out var startClock) || !TryParseClock(EndTime, out var endClock))
            return null;

        var time = TimeOnly.FromDateTime(localNow);
        if (startClock < endClock)
            return localNow.Date.Add(endClock.ToTimeSpan());

        // Overnight. If we are in the after-midnight part, end is today;
        // otherwise end is tomorrow.
        return time < endClock
            ? localNow.Date.Add(endClock.ToTimeSpan())
            : localNow.Date.AddDays(1).Add(endClock.ToTimeSpan());
    }

    public static bool TryParseClock(string? value, out TimeOnly time) =>
        TimeOnly.TryParse(value?.Trim() ?? "", out time);

    public bool IsDaySelected(int dayIndex) =>
        WeeklyDaysMask is { Length: 7 } &&
        dayIndex is >= 0 and <= 6 &&
        WeeklyDaysMask[dayIndex] == '1';

    public static bool IsValidWeeklyMask(string? mask) =>
        mask is { Length: 7 } && mask.All(c => c is '0' or '1') && mask.Contains('1');
}
