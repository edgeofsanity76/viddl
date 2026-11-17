using System.Diagnostics;
using Viddl.Core.Models;
using Viddl.Core.Net;
using Viddl.Core.Text;

namespace Viddl.Downloads.Engines;

/// <summary>
/// Downloads whole video files (.mp4, .webm, .mov...): MP4 is saved as-is, other containers are remuxed to MP4.
/// Also handles DASH (.mpd) manifests by letting ffmpeg fetch and mux the best video and audio.
/// </summary>
public sealed class DirectDownloader
{
    private readonly DownloadEnvironment _env;

    public DirectDownloader(DownloadEnvironment env) => _env = env;

    public async Task<string> DownloadFileAsync(DownloadOptions opt, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var uri = new Uri(opt.Url);
        var baseName = Formatting.SafeFileName(opt.FileName ?? Formatting.GuessName(uri, _env.Now()));
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var isMp4 = ext is ".mp4" or ".m4v" or "";

        Directory.CreateDirectory(opt.OutputFolder);
        var tempTarget = Path.Combine(opt.OutputFolder, baseName + ".part");
        var finalPath = Formatting.UniquePath(Path.Combine(opt.OutputFolder, baseName + ".mp4"));

        using var http = HttpClients.Create(opt.Referer, opt.UserAgent, opt.Cookies, _env.HttpHandlers);
        using (var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) throw new RedirectToHlsException();
            if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The URL returned a web page, not a video file.");
            isMp4 = isMp4 && (contentType is "" or "application/octet-stream" || contentType.Contains("mp4"));

            var total = resp.Content.Headers.ContentLength ?? opt.ExpectedBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tempTarget);
            var buffer = new byte[1 << 17];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            var clock = Stopwatch.StartNew();
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds <= 200) continue;
                lastReport = DateTime.UtcNow;
                double? fraction = total > 0 ? (double)done / total.Value : null;
                progress.Report(new DownloadProgress(fraction * (isMp4 ? 100 : 90),
                    $"Downloading: {done / 1048576.0:F1} MB" + (total > 0 ? $" / {total / 1048576.0:F1} MB" : "") +
                    Formatting.SpeedEta(done, clock.Elapsed, fraction ?? 0)));
            }
        }

        try
        {
            if (isMp4)
            {
                File.Move(tempTarget, finalPath);
            }
            else
            {
                await new FfmpegRunner(_env).RunAsync(
                    new[] { "-i", tempTarget, "-map", "0:v?", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart", finalPath },
                    new ScaledProgress(progress, 90, 100), ct, null, "Converting to MP4").ConfigureAwait(false);
            }
            return finalPath;
        }
        finally
        {
            try { if (File.Exists(tempTarget)) File.Delete(tempTarget); } catch (IOException) { }
        }
    }

    public async Task<string> DownloadDashAsync(DownloadOptions opt, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var uri = new Uri(opt.Url);
        Directory.CreateDirectory(opt.OutputFolder);
        var output = Formatting.UniquePath(Path.Combine(opt.OutputFolder,
            Formatting.SafeFileName(opt.FileName ?? Formatting.GuessName(uri, _env.Now())) + ".mp4"));
        var args = new List<string>();
        args.AddRange(FfmpegRunner.HttpInputArgs(opt));
        args.AddRange(new[] { "-i", opt.Url, "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", output });
        progress.Report(new DownloadProgress(null, "Downloading DASH stream with ffmpeg..."));
        await new FfmpegRunner(_env).RunAsync(args, progress, ct, null, "Downloading").ConfigureAwait(false);
        return output;
    }
}

/// <summary>Thrown when a "direct" URL actually serves an HLS playlist.</summary>
public sealed class RedirectToHlsException : Exception
{
    public RedirectToHlsException() : base("The URL serves an HLS playlist.") { }
}
