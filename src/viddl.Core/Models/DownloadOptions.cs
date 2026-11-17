namespace Viddl.Core.Models;

/// <summary>Everything needed to download one video.</summary>
public sealed class DownloadOptions
{
    public required string Url { get; init; }
    public required string OutputFolder { get; init; }

    /// <summary>File name without extension. Null = derive it from the page or stream.</summary>
    public string? FileName { get; init; }

    /// <summary>Referer header (many CDNs refuse requests without the page URL as referer).</summary>
    public string? Referer { get; init; }

    /// <summary>User-Agent to send (e.g. the built-in browser's). Null = default.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Raw "Cookie" header value (e.g. from the built-in browser).</summary>
    public string? Cookies { get; init; }

    /// <summary>Maximum video height. Null = best available, 0 = lowest available.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Parallel segment downloads for HLS.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>For yt-dlp: read cookies from an installed browser (firefox, edge, chrome...). Null = off.</summary>
    public string? CookiesFromBrowser { get; init; }

    /// <summary>File size if already known (e.g. seen by the built-in browser); used when the server doesn't send one.</summary>
    public long? ExpectedBytes { get; init; }

    /// <summary>Conversion to run once the video is saved (a conversion preset id). Null = keep the MP4 as it is.</summary>
    public string? ConversionPresetId { get; init; }

    /// <summary>Keep the downloaded file after converting it (false = keep only the converted file).</summary>
    public bool KeepOriginal { get; init; } = true;

    /// <summary>Returns a copy pointing at a different URL (used when a page resolves to a stream).</summary>
    public DownloadOptions WithUrl(string url, string? fileName, string? referer) => new()
    {
        Url = url,
        OutputFolder = OutputFolder,
        FileName = fileName,
        Referer = referer,
        UserAgent = UserAgent,
        Cookies = Cookies,
        MaxHeight = MaxHeight,
        Concurrency = Concurrency,
        CookiesFromBrowser = CookiesFromBrowser,
        ExpectedBytes = ExpectedBytes,
        ConversionPresetId = ConversionPresetId,
        KeepOriginal = KeepOriginal,
    };
}
