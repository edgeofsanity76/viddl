using Viddl.Core.Models;

namespace Viddl.Core.Streams;

/// <summary>How the preview player should open something.</summary>
public enum PreviewKind
{
    /// <summary>An HLS or DASH stream, played with adaptive streaming.</summary>
    Adaptive,

    /// <summary>A whole video file on the web.</summary>
    Progressive,

    /// <summary>A file on this PC.</summary>
    LocalFile,

    /// <summary>Not something the player can open (a web page, or a missing file).</summary>
    Unsupported,
}

/// <summary>Something to play in the preview tab, with the headers a site needs to serve it.</summary>
/// <param name="Source">A stream URL or a local file path.</param>
/// <param name="Title">Shown above the player.</param>
public sealed record PreviewRequest(string Source, string Title, string? Referer = null, string? UserAgent = null, string? Cookies = null)
{
    public PreviewKind Kind => KindOf(Source);

    /// <summary>Decides how a URL or file path can be previewed.</summary>
    public static PreviewKind KindOf(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return PreviewKind.Unsupported;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            return StreamClassifier.ClassifyUrl(source) switch
            {
                StreamKind.Hls or StreamKind.Dash => PreviewKind.Adaptive,
                StreamKind.Direct => PreviewKind.Progressive,
                _ => PreviewKind.Unsupported,
            };
        }
        return Path.IsPathRooted(source) && File.Exists(source) ? PreviewKind.LocalFile : PreviewKind.Unsupported;
    }

    /// <summary>Why a request can't be previewed, for the message shown instead of the player.</summary>
    public string UnsupportedReason => Uri.TryCreate(Source, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
        ? "This is a web page rather than a video stream, so it can't be previewed. Preview a detected stream instead."
        : "The file no longer exists.";
}
