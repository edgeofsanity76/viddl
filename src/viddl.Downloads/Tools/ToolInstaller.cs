using System.IO.Compression;
using Viddl.Core.Models;
using Viddl.Core.Net;

namespace Viddl.Downloads.Tools;

/// <summary>Downloads ffmpeg and yt-dlp into the tools folder.</summary>
public sealed class ToolInstaller
{
    public const string DefaultYtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string DefaultFfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private readonly string _toolsDir;
    private readonly HttpHandlerFactory? _handlers;
    private readonly string _ytDlpUrl;
    private readonly string _ffmpegZipUrl;

    public ToolInstaller(string toolsDir, HttpHandlerFactory? handlers = null,
        string ytDlpUrl = DefaultYtDlpUrl, string ffmpegZipUrl = DefaultFfmpegZipUrl)
    {
        _toolsDir = toolsDir;
        _handlers = handlers;
        _ytDlpUrl = ytDlpUrl;
        _ffmpegZipUrl = ffmpegZipUrl;
    }

    public string ToolsDir => _toolsDir;

    public async Task InstallYtDlpAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_toolsDir);
        var target = Path.Combine(_toolsDir, "yt-dlp.exe");
        await DownloadFileAsync(_ytDlpUrl, target + ".part", "yt-dlp", progress, ct).ConfigureAwait(false);
        File.Move(target + ".part", target, overwrite: true);
        progress.Report(new DownloadProgress(100, "yt-dlp installed."));
    }

    public async Task InstallFfmpegAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_toolsDir);
        var zipPath = Path.Combine(Path.GetTempPath(), $"viddl-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadFileAsync(_ffmpegZipUrl, zipPath, "ffmpeg", progress, ct).ConfigureAwait(false);
            progress.Report(new DownloadProgress(null, "Extracting ffmpeg..."));
            await Task.Run(() => ExtractFfmpeg(zipPath, _toolsDir, ct), ct).ConfigureAwait(false);
            progress.Report(new DownloadProgress(100, "ffmpeg installed."));
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }

    /// <summary>Copies ffmpeg.exe (required) and ffprobe.exe (optional) out of a release archive's bin folder.</summary>
    internal static void ExtractFfmpeg(string zipPath, string toolsDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        bool found = false;
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            entry.ExtractToFile(Path.Combine(toolsDir, name), overwrite: true);
            found |= name == "ffmpeg.exe";
        }
        if (!found) throw new InvalidDataException("ffmpeg.exe was not found inside the downloaded archive.");
    }

    private async Task DownloadFileAsync(string url, string target, string label, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        using var client = HttpClients.Create(handlerFactory: _handlers);
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(target);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        var lastReport = DateTime.MinValue;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 200) continue; // don't flood the UI
            lastReport = DateTime.UtcNow;
            double? pct = total > 0 ? done * 100.0 / total : null;
            progress.Report(new DownloadProgress(pct, $"Downloading {label}: {done / 1048576.0:F1} MB"));
        }
    }
}
