namespace Viddl.Core.Blocking;

/// <summary>Helpers for comparing sites (registrable domains).</summary>
public static class SiteKey
{
    /// <summary>
    /// "sub.example.co.uk" -> "example.co.uk". A good-enough registrable domain without a public-suffix list:
    /// two-letter country codes with a short second level (co.uk, com.au, ne.jp) keep three labels.
    /// </summary>
    public static string SiteOf(string host)
    {
        host = host.ToLowerInvariant();
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        bool secondLevelSuffix = parts[^2].Length <= 3 && parts[^1].Length == 2;
        return string.Join('.', parts[^(secondLevelSuffix ? 3 : 2)..]);
    }

    /// <summary>The site of an http(s) URL, or null for anything else.</summary>
    public static string? SiteOfUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? SiteOf(u.Host) : null;

    /// <summary>True when <paramref name="url"/> belongs to a different site than the http(s) page.</summary>
    public static bool IsThirdParty(string url, string pageUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && SiteOfUrl(pageUrl) is { } pageSite
        && !string.Equals(SiteOf(u.Host), pageSite, StringComparison.OrdinalIgnoreCase);

    /// <summary>Host of a URL for display; "scheme:" for URLs without a host (e.g. "ms-settings:").</summary>
    public static string DisplayHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.Length > 0 ? u.Host : u.Scheme + ":") : url;
}
