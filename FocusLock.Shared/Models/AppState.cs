namespace FocusLock.Shared.Models;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 12;
    public List<TrackedApp> Apps { get; set; } = new();
    public List<RewardKey> Keys { get; set; } = new();
    public List<AuditEvent> AuditLog { get; set; } = new();
    public UserSettings Settings { get; set; } = new();

    // V4 analytics
    public List<DailyUsageStat> DailyUsage { get; set; } = new();
    public List<AppUsageStat> AppUsage { get; set; } = new();
    public List<UsageSession> SessionHistory { get; set; } = new();

    // V5 browser classification rules
    public List<BrowserRule> BrowserRules { get; set; } = new();

    // V7 Cold-Turkey-style block groups for entertainment apps.
    public List<BlockProfile> BlockProfiles { get; set; } = new();

    // V7.1 strict blocking / locked sessions / Focus-only whitelist.
    public ControlPolicy ControlPolicy { get; set; } = new();

    public int FocusProgressSeconds { get; set; }
    public int EntertainmentBalanceSeconds { get; set; }
    public long TotalFocusSeconds { get; set; }
    public long TotalEntertainmentSeconds { get; set; }
    public long SuspiciousSeconds { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public bool ClockRollbackDetected { get; set; }
    public bool IntegrityIssueDetected { get; set; }
}
