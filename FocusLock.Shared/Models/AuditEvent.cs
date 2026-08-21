namespace FocusLock.Shared.Models;

public sealed class AuditEvent
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "Info";
    public string Message { get; set; } = "";
}
