using Viddl.Core.Models;

namespace Viddl.Core.Streams;

/// <summary>Decides what kind of resource a URL (and optionally its content type) is.</summary>
public static class StreamClassifier
{
    private static readonly string[] DirectExtensions = { ".mp4", ".webm", ".m4v", ".mov", ".mkv", ".ts", ".flv" };

    /// <summary>Classification from the URL alone.</summary>
    public static StreamKind ClassifyUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : lower;
        if (path.EndsWith(".m3u8") || lower.Contains(".m3u8?") || lower.Contains("format=m3u8")) return StreamKind.Hls;
        if (path.EndsWith(".mpd") || lower.Contains(".mpd?")) return StreamKind.Dash;
        if (DirectExtensions.Any(path.EndsWith)) return StreamKind.Direct;
        return StreamKind.WebPage;
    }

    /// <summary>
    /// Classification of a network response seen by the browser. Returns null for anything that isn't
    /// a whole stream (pages, images, and the individual segments of HLS/DASH streams).
    /// </summary>
    public static StreamKind? ClassifyResponse(string url, string? contentType)
    {
        contentType = contentType?.ToLowerInvariant() ?? "";
        if (contentType.Contains("mpegurl")) return StreamKind.Hls;
        if (contentType.Contains("dash+xml")) return StreamKind.Dash;

        var kind = ClassifyUrl(url);
        if (kind is StreamKind.Hls or StreamKind.Dash) return kind;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".ts") || path.EndsWith(".m4s") || path.EndsWith(".m4a") || contentType.Contains("mp2t")) return null;
        bool videoType = contentType.StartsWith("video/mp4") || contentType.StartsWith("video/webm") || contentType.StartsWith("video/quicktime");
        bool videoExt = path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".m4v") || path.EndsWith(".mov");
        return videoType || (videoExt && !contentType.StartsWith("text/")) ? StreamKind.Direct : null;
    }

    /// <summary>Total size from "Content-Range: bytes 0-1023/52428800", or Content-Length for a full (200) response.</summary>
    public static long? TotalSize(string? contentRange, string? contentLength, int statusCode)
    {
        var slash = contentRange?.LastIndexOf('/') ?? -1;
        if (slash > 0 && long.TryParse(contentRange![(slash + 1)..], out var total)) return total;
        if (statusCode == 200 && long.TryParse(contentLength, out var len)) return len;
        return null;
    }
}
