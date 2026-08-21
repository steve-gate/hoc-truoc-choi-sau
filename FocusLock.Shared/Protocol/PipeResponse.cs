namespace FocusLock.Shared.Protocol;

public sealed class PipeResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public ServiceSnapshot? Snapshot { get; set; }
    public BrowserDecision? BrowserDecision { get; set; }
}
