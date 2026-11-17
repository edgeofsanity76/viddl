using Viddl.Core.Settings;

namespace Viddl.Core.Browsing;

/// <summary>One address-bar suggestion.</summary>
public sealed record Suggestion(SiteEntry Entry, bool IsFavorite)
{
    public string Title => Entry.DisplayTitle;
    public string Url => Entry.Url;
    public string Glyph => IsFavorite ? "★" : "🕘";
}

/// <summary>Builds address-bar suggestions from favorites and history.</summary>
public static class SuggestionEngine
{
    /// <summary>
    /// Favorites first, then history. Matches anywhere in the title or address. With text, history is ranked
    /// by host-prefix matches then visit count; without text, the most recent sites are shown.
    /// </summary>
    public static IReadOnlyList<Suggestion> Build(string? text, IEnumerable<SiteEntry> favorites, IEnumerable<SiteEntry> history, int max = 10)
    {
        text = text?.Trim() ?? "";
        bool Match(SiteEntry e) => text.Length == 0
            || e.Url.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (e.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

        var favs = favorites.Where(Match).Take(text.Length == 0 ? 5 : 6).Select(f => new Suggestion(f, true)).ToList();
        var favKeys = favs.Select(f => AppSettings.Key(f.Entry.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hist = history.Where(h => Match(h) && !favKeys.Contains(AppSettings.Key(h.Url)));
        if (text.Length > 0)
            hist = hist.OrderByDescending(h => h.Url.Contains("://" + text, StringComparison.OrdinalIgnoreCase)
                                               || h.Url.Contains("://www." + text, StringComparison.OrdinalIgnoreCase))
                       .ThenByDescending(h => h.Visits);
        return favs.Concat(hist.Select(h => new Suggestion(h, false))).Take(max).ToList();
    }
}

/// <summary>Turns what the user typed in the address bar into a URL.</summary>
public static class AddressInput
{
    public const string SearchUrl = "https://www.bing.com/search?q=";

    public static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    /// <summary>A full URL as-is, "example.com" as https, anything else as a web search. Null for blank input.</summary>
    public static string? ToUrl(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (IsHttpUrl(text)) return text;
        if (!text.Contains(' ') && text.Contains('.') && IsHttpUrl("https://" + text)) return "https://" + text;
        return SearchUrl + Uri.EscapeDataString(text);
    }

    /// <summary>First http(s) URL in dropped/pasted text, or null.</summary>
    public static string? FirstUrl(string? text)
    {
        var first = text?.Trim().Split('\n')[0].Trim().TrimEnd('\0');
        return IsHttpUrl(first) ? first : null;
    }
}
