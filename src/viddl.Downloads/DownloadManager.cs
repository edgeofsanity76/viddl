using Viddl.Core.Models;
using Viddl.Core.Streams;
using Viddl.Downloads.Engines;

namespace Viddl.Downloads;

public enum DownloadEngine
{
    /// <summary>Stream URLs use the built-in engines; web pages use yt-dlp, falling back to a page scan.</summary>
    Auto,

    /// <summary>Never use yt-dlp: built-in HLS/DASH/file engines plus page scanning.</summary>
    BuiltIn,

    /// <summary>Always hand the URL to yt-dlp.</summary>
    YtDlp,
}

/// <summary>Downloads a URL with whichever engine fits it.</summary>
public interface IDownloader
{
    Task<string> DownloadAsync(DownloadOptions opt, DownloadEngine engine, IProgress<DownloadProgress> progress,
        Action<string> log, CancellationToken ct);
}

/// <summary>Chooses the right engine for a URL and runs it.</summary>
public sealed class DownloadManager : IDownloader
{
    private readonly DownloadEnvironment _env;

    public DownloadManager(DownloadEnvironment env) => _env = env;

    public async Task<string> DownloadAsync(DownloadOptions opt, DownloadEngine engine, IProgress<DownloadProgress> progress,
        Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(opt.OutputFolder);
        if (engine == DownloadEngine.YtDlp)
            return await new YtDlpDownloader(_env).DownloadAsync(opt, progress, log, ct).ConfigureAwait(false);

        var kind = StreamClassifier.ClassifyUrl(opt.Url);
        log($"URL type: {kind}");
        switch (kind)
        {
            case StreamKind.Hls:
                return await new HlsDownloader(opt, progress, _env).DownloadAsync(ct).ConfigureAwait(false);

            case StreamKind.Dash:
                return await new DirectDownloader(_env).DownloadDashAsync(opt, progress, ct).ConfigureAwait(false);

            case StreamKind.Direct:
                try
                {
                    return await new DirectDownloader(_env).DownloadFileAsync(opt, progress, ct).ConfigureAwait(false);
                }
                catch (RedirectToHlsException)
                {
                    log("The server returned an HLS playlist - switching to the HLS engine.");
                    return await new HlsDownloader(opt, progress, _env).DownloadAsync(ct).ConfigureAwait(false);
                }

            default:
                if (engine == DownloadEngine.Auto && _env.Tools.FindYtDlp() != null)
                {
                    try
                    {
                        log("Web page - extracting with yt-dlp...");
                        return await new YtDlpDownloader(_env).DownloadAsync(opt, progress, log, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log($"yt-dlp failed: {ex.Message}");
                        log("Falling back to scanning the page for stream URLs...");
                    }
                }
                return await DownloadFromPageScanAsync(opt, progress, log, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> DownloadFromPageScanAsync(DownloadOptions opt, IProgress<DownloadProgress> progress,
        Action<string> log, CancellationToken ct)
    {
        progress.Report(new DownloadProgress(null, "Scanning page for video streams..."));
        var scan = await new PageScanner(_env.HttpHandlers).ScanAsync(opt.Url, ct, opt.UserAgent, opt.Cookies).ConfigureAwait(false);
        if (scan.Streams.Count == 0)
            throw new InvalidOperationException(
                "No video stream was found in the page. Play the video in the built-in browser and download the stream it detects.");

        foreach (var s in scan.Streams) log($"Found: {s}");
        var best = scan.Streams[0];
        log($"Using: {best.Uri}");
        var sub = opt.WithUrl(best.Uri.ToString(), opt.FileName ?? scan.Title, opt.Referer ?? opt.Url);
        return await DownloadAsync(sub, DownloadEngine.BuiltIn, progress, log, ct).ConfigureAwait(false);
    }
}
