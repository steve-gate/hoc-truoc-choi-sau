namespace FocusLock.Shared.Models;

public enum BrowserRuleMatchType
{
    HostSuffix,
    UrlPrefix,
    UrlContains,
    TitleContains,
    ExactUrl
}

public sealed class BrowserRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public BrowserRuleMatchType MatchType { get; set; } = BrowserRuleMatchType.HostSuffix;
    public AppCategory Category { get; set; }
    public bool Enabled { get; set; } = true;

    // V7.1: entertainment websites can share the same Block Profiles as apps,
    // so schedule/allowance applies consistently across desktop + browser.
    public string BlockProfileId { get; set; } = "";
    public string BlockProfileName { get; set; } = "Giải trí chung";

    public string CategoryLabel => Category == AppCategory.Focus ? "Học tập / Làm việc" : "Giải trí";
    public string MatchTypeLabel => MatchType switch
    {
        BrowserRuleMatchType.HostSuffix => "Domain",
        BrowserRuleMatchType.UrlPrefix => "URL bắt đầu bằng",
        BrowserRuleMatchType.UrlContains => "URL chứa",
        BrowserRuleMatchType.TitleContains => "Tiêu đề chứa",
        BrowserRuleMatchType.ExactUrl => "Chính xác URL",
        _ => MatchType.ToString()
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Pattern : Name;
    public string BlockProfileLabel => Category == AppCategory.Focus
        ? string.IsNullOrWhiteSpace(BlockProfileId)
            ? "Công thức chung"
            : string.IsNullOrWhiteSpace(BlockProfileName) ? "Profile Focus" : BlockProfileName
        : string.IsNullOrWhiteSpace(BlockProfileName) ? "Giải trí chung" : BlockProfileName;
}
