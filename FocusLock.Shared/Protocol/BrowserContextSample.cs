namespace FocusLock.Shared.Protocol;

public sealed class BrowserContextSample
{
    public string Browser { get; set; } = ""; // chrome | edge
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Host { get; set; } = "";
    public bool WindowFocused { get; set; }
    public string ExtensionVersion { get; set; } = "";
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
}
