namespace FocusLock.Shared.Models;

public sealed class UserSettings
{
    public int FocusMinutesPerKey { get; set; } = 30;
    public int RewardMinutesPerKey { get; set; } = 10;
    public int KeyExpiryMinutes { get; set; } = 120;
    public int IdleThresholdSeconds { get; set; } = 60;
    public int MaxEntertainmentMinutes { get; set; } = 120;
    public bool BubbleEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    // V3 hardening retained in V4
    public bool AntiCheatEnabled { get; set; } = true;
    public int MinimumActivityEventsPerMinute { get; set; } = 2;
    public int AgentHeartbeatTimeoutSeconds { get; set; } = 5;
    public int ClockRollbackToleranceSeconds { get; set; } = 120;
    public bool VerifyExecutableHash { get; set; } = true;

    // V4 statistics
    public int StreakMinimumFocusMinutes { get; set; } = 30;
    public int StatisticsRetentionDays { get; set; } = 365;
    public int SessionHistoryLimit { get; set; } = 2000;

    // V5 browser bridge
    public bool BrowserRulesEnabled { get; set; } = true;
    public int BrowserContextTimeoutSeconds { get; set; } = 5;
}
