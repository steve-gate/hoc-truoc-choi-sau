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

    // V7 Website Focus 2.0 signals from the active tab content script.
    public bool DocumentVisible { get; set; }
    public long InteractionCounter { get; set; }
    public long LastUserActivityUnixMs { get; set; }
    public bool MediaPlaying { get; set; }
    public bool MediaProgressing { get; set; }

    // V7.3: elapsed foreground+visible time measured by the browser extension.
    // Guard caps this value and uses it instead of relying on MV3 service-worker timer gaps.
    public int ActiveElapsedMilliseconds { get; set; }
}
