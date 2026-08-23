using FocusLock.Shared.Models;

namespace FocusLock.Shared.Protocol;

public sealed class PipeRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = "snapshot";
    public ActivitySample? Activity { get; set; }
    public TrackedApp? App { get; set; }
    public string? AppId { get; set; }
    public string? KeyCode { get; set; }
    public UserSettings? Settings { get; set; }
    public AppState? LegacyState { get; set; }
    public BrowserContextSample? BrowserContext { get; set; }
    public BrowserRule? BrowserRule { get; set; }
    public string? BrowserRuleId { get; set; }

    // V7 profiles / per-app block mode.
    public BlockProfile? BlockProfile { get; set; }
    public string? BlockProfileId { get; set; }
    public EntertainmentBlockAction BlockAction { get; set; }
    public bool UseCustomBlockAction { get; set; }

    // V7.1 strict blocking controls.
    public int DurationMinutes { get; set; }


    // V7.5 settings-protection controls.
    public string? TextValue { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? UntilUtc { get; set; }

    // V7.7.8 Backup / Restore. The desktop app chooses the path; Guard performs the signed state operation.
    public string? FilePath { get; set; }
}
