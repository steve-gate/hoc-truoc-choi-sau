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
}
