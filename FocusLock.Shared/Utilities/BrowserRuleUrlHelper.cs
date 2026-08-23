using FocusLock.Shared.Models;

namespace FocusLock.Shared.Utilities;

public static class BrowserRuleUrlHelper
{
    public static string NormalizeHost(string? value)
    {
        var raw = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw == "—") return "";

        if (Uri.TryCreate(raw, UriKind.Absolute, out var direct) &&
            IsHttp(direct) &&
            !string.IsNullOrWhiteSpace(direct.Host))
        {
            return direct.Host.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        }

        if (Uri.TryCreate("https://" + raw, UriKind.Absolute, out var withScheme) &&
            !string.IsNullOrWhiteSpace(withScheme.Host))
        {
            return withScheme.Host.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
        }

        return raw.Split('/')[0].Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
    }

    public static string NormalizeAbsoluteUrl(string? value)
    {
        var raw = (value ?? "").Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !IsHttp(uri))
            return "";

        // Fragment (#...) is intentionally ignored:
        // SPA/hash navigation should not make the same page suddenly become another rule.
        var builder = new UriBuilder(uri)
        {
            Fragment = "",
            Host = uri.Host.ToLowerInvariant(),
            Scheme = uri.Scheme.ToLowerInvariant()
        };

        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    public static string NormalizePattern(string? pattern, BrowserRuleMatchType type)
    {
        var raw = (pattern ?? "").Trim();
        return type switch
        {
            BrowserRuleMatchType.HostSuffix => NormalizeHost(raw),
            BrowserRuleMatchType.UrlPrefix => NormalizeAbsoluteUrl(raw),
            BrowserRuleMatchType.ExactUrl => NormalizeAbsoluteUrl(raw),
            _ => raw
        };
    }

    public static string PatternFromCurrentPage(
        BrowserRuleMatchType type,
        string? currentUrl,
        string? currentHost,
        string? currentTitle)
    {
        return type switch
        {
            BrowserRuleMatchType.HostSuffix => NormalizeHost(currentHost),
            BrowserRuleMatchType.UrlPrefix => NormalizeAbsoluteUrl(currentUrl),
            BrowserRuleMatchType.ExactUrl => NormalizeAbsoluteUrl(currentUrl),
            BrowserRuleMatchType.TitleContains =>
                string.IsNullOrWhiteSpace(currentTitle) || currentTitle == "—"
                    ? ""
                    : currentTitle.Trim(),
            _ => (currentUrl ?? "").Trim()
        };
    }

    public static bool IsValidAbsoluteWebUrl(string? value) =>
        !string.IsNullOrWhiteSpace(NormalizeAbsoluteUrl(value));

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
