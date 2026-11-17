using System.Net;

namespace Viddl.Core.Net;

/// <summary>Creates the message handler behind an <see cref="HttpClient"/>. Replaceable for testing.</summary>
/// <param name="useCookies">Whether the handler should manage cookies itself.</param>
public delegate HttpMessageHandler HttpHandlerFactory(bool useCookies);

/// <summary>Creates HTTP clients that look like a normal desktop browser.</summary>
public static class HttpClients
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    /// <summary>The real network handler (decompression, redirects, optional cookie container).</summary>
    public static HttpMessageHandler DefaultHandler(bool useCookies) => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        UseCookies = useCookies,
    };

    /// <summary>Client for media requests (playlists, segments, files) - looks like a player's XHR.</summary>
    public static HttpClient Create(string? referer = null, string? userAgent = null, string? cookies = null,
        HttpHandlerFactory? handlerFactory = null)
    {
        var client = NewClient(userAgent, cookies, handlerFactory);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
        {
            client.DefaultRequestHeaders.Referrer = r;
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", r.GetLeftPart(UriPartial.Authority));
        }
        return client;
    }

    /// <summary>Client for loading an HTML page - sends the headers a browser sends for a normal navigation.</summary>
    public static HttpClient CreatePageClient(string? userAgent = null, string? cookies = null,
        HttpHandlerFactory? handlerFactory = null)
    {
        var client = NewClient(userAgent, cookies, handlerFactory);
        var h = client.DefaultRequestHeaders;
        h.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        h.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        h.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        h.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        h.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        if (userAgent == null) // only claim to be Chrome when we are using the Chrome user agent
        {
            h.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"");
            h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }
        return client;
    }

    private static HttpClient NewClient(string? userAgent, string? cookies, HttpHandlerFactory? handlerFactory)
    {
        // When cookies are supplied (from the built-in browser) they are sent as a raw header instead.
        var handler = (handlerFactory ?? DefaultHandler)(string.IsNullOrEmpty(cookies));
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        if (!string.IsNullOrEmpty(cookies))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookies);
        return client;
    }
}
