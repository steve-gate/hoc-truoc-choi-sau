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
