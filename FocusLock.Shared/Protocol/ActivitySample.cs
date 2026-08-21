namespace FocusLock.Shared.Protocol;

public sealed class ActivitySample
{
    public string AgentInstanceId { get; set; } = "";
    public long Sequence { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public long IdleMilliseconds { get; set; }
    public bool InputChanged { get; set; }
    public int HumanInputEvents { get; set; }
    public bool InputMonitorHealthy { get; set; }
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
}
