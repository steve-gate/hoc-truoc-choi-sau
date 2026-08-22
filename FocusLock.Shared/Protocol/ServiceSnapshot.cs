using FocusLock.Shared.Models;

namespace FocusLock.Shared.Protocol;

public sealed class ServiceSnapshot
{
    public bool ServiceOnline { get; set; } = true;
    public string ServiceStatus { get; set; } = "Guard đang chạy";
    public string CurrentMode { get; set; } = "Sẵn sàng";
    public string CurrentApp { get; set; } = "—";
    public bool IsIdle { get; set; }
    public int ActivityEventsLastMinute { get; set; }
    public bool HeartbeatHealthy { get; set; }
    public bool InputMonitorHealthy { get; set; }
    public bool BrowserBridgeHealthy { get; set; }
    public string CurrentBrowser { get; set; } = "—";
    public string CurrentBrowserHost { get; set; } = "—";
    public string CurrentBrowserTitle { get; set; } = "—";
    public string CurrentBrowserUrl { get; set; } = "";
    public string CurrentBrowserCategory { get; set; } = "Neutral";
    public string CurrentBrowserRule { get; set; } = "—";
    public bool CurrentBrowserBlocked { get; set; }
    public string CurrentBrowserProfile { get; set; } = "—";
    public string CurrentBrowserAccess { get; set; } = "—";
    public int CurrentBrowserAllowanceRemainingSeconds { get; set; }
    public bool BrowserForegroundActive { get; set; }

    // V7.4: explicit entertainment session state for the always-on bubble.
    // Do not infer this from translated CurrentMode text.
    public bool EntertainmentSessionActive { get; set; }
    public string EntertainmentAccessMode { get; set; } = "—";
    public string EntertainmentProfileName { get; set; } = "—";
    public int EntertainmentAllowanceRemainingSeconds { get; set; }
    public int EntertainmentWalletRemainingSeconds { get; set; }
    public int EntertainmentUsableRemainingSeconds { get; set; }

    // V7 Website Focus 2.0 diagnostics.
    public bool BrowserDocumentVisible { get; set; }
    public bool BrowserMediaPlaying { get; set; }
    public bool BrowserMediaProgressing { get; set; }
    public bool BrowserFocusQualified { get; set; }
    public int BrowserActivityEventsLastMinute { get; set; }

    public DateTime SnapshotUtc { get; set; } = DateTime.UtcNow;
    public AppState State { get; set; } = new();
    public AnalyticsSnapshot Analytics { get; set; } = new();
}
