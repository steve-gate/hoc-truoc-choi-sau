namespace FocusLock.Shared.Models;

public sealed class ControlPolicy
{
    public bool StrictModeEnabled { get; set; }
    public int StrictUnlockDelayMinutes { get; set; } = 30;
    public DateTime? StrictUnlockRequestedUtc { get; set; }

    // V7.5 settings/configuration protection. This is enforced by Guard, not only the UI.
    public SettingsProtectionMode SettingsProtectionMode { get; set; } = SettingsProtectionMode.Off;
    public string SettingsUnlockChallenge { get; set; } = "";
    public DateTime? SettingsProtectionStartUtc { get; set; }
    public DateTime? SettingsProtectionUntilUtc { get; set; }

    public bool SettingsTextProtectionActive =>
        SettingsProtectionMode == SettingsProtectionMode.TypingChallenge &&
        !string.IsNullOrWhiteSpace(SettingsUnlockChallenge);

    public bool SettingsTimeProtectionActive =>
        SettingsProtectionMode == SettingsProtectionMode.TimeWindow &&
        SettingsProtectionStartUtc is DateTime start &&
        SettingsProtectionUntilUtc is DateTime until &&
        DateTime.UtcNow >= start && DateTime.UtcNow < until;

    public bool SettingsProtectionActive => SettingsTextProtectionActive || SettingsTimeProtectionActive;

    // V7.7.4 Focus Session.
    // Progress is QUALIFIED Focus time, not wall-clock time. This means idle,
    // neutral apps and low-activity pages do not advance the session.
    public DateTime? FocusSessionStartedUtc { get; set; }
    public int FocusSessionTargetSeconds { get; set; }
    public int FocusSessionQualifiedSeconds { get; set; }
    public int FocusSessionRewardSeconds { get; set; }

    // Optional Profile binding. Empty = any Focus source + global reward formula.
    // Non-empty = only Focus sources assigned to that Profile advance the session,
    // and the session reward uses that Profile's formula.
    public string FocusSessionProfileId { get; set; } = "";
    public string FocusSessionProfileName { get; set; } = "";

    public bool FocusSessionActive =>
        FocusSessionStartedUtc is not null &&
        FocusSessionTargetSeconds > 0 &&
        FocusSessionQualifiedSeconds < FocusSessionTargetSeconds;

    public int FocusSessionRemainingSeconds =>
        FocusSessionActive
            ? Math.Max(0, FocusSessionTargetSeconds - FocusSessionQualifiedSeconds)
            : 0;

    // Locked Session: entertainment is blocked regardless of wallet/allowance.
    public DateTime? LockedSessionUntilUtc { get; set; }

    // Focus-only whitelist session: tracked entertainment apps are blocked and
    // browser tabs must match a Focus rule. Untracked Windows/system apps are
    // intentionally left alone for safety.
    public DateTime? WhitelistSessionUntilUtc { get; set; }

    public bool LockedSessionActive => LockedSessionUntilUtc is DateTime until && until > DateTime.UtcNow;
    public bool WhitelistSessionActive => WhitelistSessionUntilUtc is DateTime until && until > DateTime.UtcNow;

    public DateTime? StrictUnlockAvailableUtc =>
        StrictUnlockRequestedUtc is DateTime requested
            ? requested.AddMinutes(Math.Max(1, StrictUnlockDelayMinutes))
            : null;

    public bool StrictUnlockReady =>
        StrictModeEnabled &&
        StrictUnlockAvailableUtc is DateTime ready &&
        ready <= DateTime.UtcNow;
}
