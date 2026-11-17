namespace Viddl.Core.Models;

/// <summary>What kind of resource a URL points at.</summary>
public enum StreamKind
{
    Hls,
    Dash,
    Direct,
    WebPage,
}

/// <summary>A stream URL found in a page.</summary>
public sealed record DetectedStream(Uri Uri, StreamKind Kind)
{
    /// <summary>Short, readable label such as "[HLS]  cdn.example.com  …/720p/index.m3u8" (no query string).</summary>
    public override string ToString()
    {
        var segments = Uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tail = segments.Length <= 2 ? Uri.AbsolutePath : "…/" + string.Join('/', segments[^2..]);
        return $"[{Kind.ToString().ToUpperInvariant()}]  {Uri.Host}  {tail}";
    }
}

/// <summary>Result of scanning a page's HTML for streams.</summary>
public sealed record ScanResult(string? Title, IReadOnlyList<DetectedStream> Streams);

/// <summary>The site refused a plain HTTP request (bot protection, login required, rate limiting).</summary>
public sealed class PageBlockedException : Exception
{
    public int StatusCode { get; }

    public PageBlockedException(int statusCode, string? server)
        : base($"The website refused the request (HTTP {statusCode}{(string.IsNullOrWhiteSpace(server) ? "" : ", " + server)}). " +
               "It probably uses bot protection or needs a login. Play the video in the built-in browser and " +
               "download the stream it detects instead.")
    {
        StatusCode = statusCode;
    }
}
