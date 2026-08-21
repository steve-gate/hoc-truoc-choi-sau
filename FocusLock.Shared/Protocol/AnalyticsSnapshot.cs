namespace FocusLock.Shared.Protocol;

public sealed class AnalyticsSnapshot
{
    public PeriodAnalytics Today { get; set; } = new();
    public PeriodAnalytics Week { get; set; } = new();
    public PeriodAnalytics Month { get; set; } = new();
    public int CurrentStreakDays { get; set; }
    public int BestStreakDays { get; set; }
    public int StreakGoalMinutes { get; set; } = 30;
    public List<DailyChartPoint> Last7Days { get; set; } = new();
}

public sealed class PeriodAnalytics
{
    public string Label { get; set; } = "";
    public long FocusSeconds { get; set; }
    public long EntertainmentSeconds { get; set; }
    public long SuspiciousSeconds { get; set; }
    public int KeysGenerated { get; set; }
    public int KeysRedeemed { get; set; }
    public int KeysExpired { get; set; }
    public long RewardSecondsGranted { get; set; }
    public double FocusPercent { get; set; }
    public double EntertainmentPercent { get; set; }
    public List<AppAnalyticsRow> Apps { get; set; } = new();
}

public sealed class AppAnalyticsRow
{
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Category { get; set; } = "";
    public long Seconds { get; set; }
}

public sealed class DailyChartPoint
{
    public string DateKey { get; set; } = "";
    public string DayLabel { get; set; } = "";
    public long FocusSeconds { get; set; }
    public long EntertainmentSeconds { get; set; }
}
