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
    public DateTime SnapshotUtc { get; set; } = DateTime.UtcNow;
    public AppState State { get; set; } = new();
    public AnalyticsSnapshot Analytics { get; set; } = new();
}
