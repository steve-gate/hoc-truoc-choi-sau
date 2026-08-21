namespace FocusLock.Shared.Models;

public enum BrowserRuleMatchType
{
    HostSuffix,
    UrlPrefix,
    UrlContains,
    TitleContains
}

public sealed class BrowserRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public BrowserRuleMatchType MatchType { get; set; } = BrowserRuleMatchType.HostSuffix;
    public AppCategory Category { get; set; }
    public bool Enabled { get; set; } = true;

    public string CategoryLabel => Category == AppCategory.Focus ? "Học tập / Làm việc" : "Giải trí";
    public string MatchTypeLabel => MatchType switch
    {
        BrowserRuleMatchType.HostSuffix => "Domain",
        BrowserRuleMatchType.UrlPrefix => "URL bắt đầu bằng",
        BrowserRuleMatchType.UrlContains => "URL chứa",
        BrowserRuleMatchType.TitleContains => "Tiêu đề chứa",
        _ => MatchType.ToString()
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Pattern : Name;
}
