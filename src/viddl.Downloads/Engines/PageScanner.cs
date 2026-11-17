using System.Net;
using System.Text.RegularExpressions;
using Viddl.Core.Models;
using Viddl.Core.Net;
using Viddl.Core.Streams;

namespace Viddl.Downloads.Engines;

/// <summary>
/// Fallback extractor: downloads a page's HTML and looks for stream URLs (.m3u8 / .mpd / .mp4...) in the markup,
/// inline scripts and JSON. Players that build the URL in JavaScript need the built-in browser or yt-dlp instead.
/// </summary>
public sealed class PageScanner
{
    private static readonly Regex UrlRx = new(
        @"(?<url>(?:https?:)?//[^\s""'<>()\\]+?\.(?:m3u8|mpd|mp4|webm|m4v|mov)(?:\?[^\s""'<>()\\]*)?)(?=[""'\s<>()\\,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttrSrcRx = new(
        @"<(?:video|source)[^>]+src\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaVideoRx = new(
        @"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:video(?::url|:secure_url)?|twitter:player:stream)[""'][^>]+content\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRx = new(
        @"<meta[^>]+property\s*=\s*[""']og:title[""'][^>]+content\s*=\s*[""'](?<t>[^""']+)[""']|<title[^>]*>(?<t>[^<]+)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpHandlerFactory? _handlers;

    public PageScanner(HttpHandlerFactory? handlers = null) => _handlers = handlers;

    public async Task<ScanResult> ScanAsync(string pageUrl, CancellationToken ct, string? userAgent = null, string? cookies = null)
    {
        var pageUri = new Uri(pageUrl);
        using var http = HttpClients.CreatePageClient(userAgent, cookies, _handlers);
        using var resp = await http.GetAsync(pageUri, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized)
            throw new PageBlockedException((int)resp.StatusCode, resp.Headers.Server.ToString());
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(html, pageUri);
    }

    /// <summary>Extracts the title and stream URLs from HTML.</summary>
    public static ScanResult Parse(string html, Uri pageUri)
    {
        string? title = null;
        if (TitleRx.Match(html) is { Success: true } tm) title = WebUtility.HtmlDecode(tm.Groups["t"].Value).Trim();

        // Undo common JSON/JS escaping so URLs inside scripts become visible.
        var text = html.Replace("\\/", "/").Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                       .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase).Replace("&amp;", "&");

        var found = new List<string>();
        found.AddRange(MetaVideoRx.Matches(text).Select(m => m.Groups["url"].Value));
        found.AddRange(AttrSrcRx.Matches(text).Select(m => m.Groups["url"].Value));
        found.AddRange(UrlRx.Matches(text).Select(m => m.Groups["url"].Value));

        var streams = new List<DetectedStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in found)
        {
            var s = raw.Trim();
            if (s.StartsWith("blob:") || s.StartsWith("data:")) continue;
            if (s.StartsWith("//")) s = pageUri.Scheme + ":" + s;
            if (!Uri.TryCreate(pageUri, s, out var abs) || (abs.Scheme != "http" && abs.Scheme != "https")) continue;
            var kind = StreamClassifier.ClassifyUrl(abs.ToString());
            if (kind == StreamKind.WebPage) continue;
            if (seen.Add(abs.ToString())) streams.Add(new DetectedStream(abs, kind));
        }

        // Best candidates first: HLS (master playlists first), then DASH, then whole files.
        var ordered = streams
            .OrderBy(s => s.Kind switch { StreamKind.Hls => 0, StreamKind.Dash => 1, _ => 2 })
            .ThenByDescending(s => s.Uri.AbsolutePath.Contains("master", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ScanResult(title, ordered);
    }
}
