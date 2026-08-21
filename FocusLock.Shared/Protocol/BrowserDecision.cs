namespace FocusLock.Shared.Protocol;

public sealed class BrowserDecision
{
    public bool BridgeOnline { get; set; }
    public bool Matched { get; set; }
    public bool Blocked { get; set; }
    public string Category { get; set; } = "Neutral";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Host { get; set; } = "";
    public string Url { get; set; } = "";
    public string Message { get; set; } = "";
    public int EntertainmentBalanceSeconds { get; set; }
    public int FocusProgressSeconds { get; set; }
}
