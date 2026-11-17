using System.Globalization;
using System.Text.RegularExpressions;
using Viddl.Core.Models;
using Viddl.Core.Text;
using Viddl.Downloads.Processes;

namespace Viddl.Downloads.Engines;

/// <summary>
/// Interprets yt-dlp's output. Video and audio are often downloaded in separate passes and then merged, so all
/// passes are mapped onto one 0-100% scale: downloads share 0-95%, merging 95-100%.
/// </summary>
public sealed class YtDlpOutputParser
{
    /// <summary>Progress template using raw numbers (yt-dlp's formatted strings are padded and colored).</summary>
    public const string ProgressTemplate =
        "download:PROGRESS %(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|" +
        "%(progress.speed)s|%(progress.eta)s|%(progress.fragment_index)s|%(progress.fragment_count)s";

    private static readonly Regex FormatsRx = new(@"Downloading \d+ format\(s\):\s*(\S+)", RegexOptions.Compiled);

    private int _pass;
    private int _passes = 1;

    public string? SavedPath { get; private set; }

    /// <summary>Handles one stdout line. Returns progress to report (or null); <paramref name="log"/> = line worth logging.</summary>
    public DownloadProgress? Parse(string rawLine, out string? log)
    {
        log = null;
        var line = rawLine.Trim();
        if (line.Length == 0) return null;

        if (line.StartsWith("PROGRESS ")) return ParseProgress(line[9..]);
        if (line.StartsWith("SAVED "))
        {
            SavedPath = line[6..].Trim();
            return null;
        }

        log = line;
        if (FormatsRx.Match(line) is { Success: true } fm) _passes = fm.Groups[1].Value.Count(c => c == '+') + 1;
        if (line.StartsWith("[download] Destination:") || line.Contains("has already been downloaded")) _pass++;
        if (line.StartsWith("[Merger]") || line.StartsWith("[VideoRemuxer]") || line.StartsWith("[FixupM3u8]"))
            return new DownloadProgress(96, "Merging into MP4...");
        return null;
    }

    private DownloadProgress ParseProgress(string payload)
    {
        var f = payload.Split('|');
        double? N(int i) => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        double? done = N(0), total = N(1) ?? N(2), speed = N(3), eta = N(4), frag = N(5), frags = N(6);

        // Fragment count is steady for streams; byte totals are only estimates there.
        double? part = frags > 0 && frag != null ? frag / frags : total > 0 && done != null ? done / total : null;
        if (part == null) return new DownloadProgress(null, $"Downloading: {(done ?? 0) / 1048576.0:F1} MB");

        int p = Math.Clamp(_pass, 1, _passes);
        double overall = ((p - 1) + Math.Clamp(part.Value, 0, 1)) / _passes * 95;
        var text = (_passes > 1 ? $"Downloading part {p}/{_passes}" : "Downloading") +
                   (done != null ? $" · {done.Value / 1048576.0:F1} MB" : "") +
                   (speed > 0 ? $" · {speed.Value / 1048576.0:F1} MB/s" : "") +
                   (eta > 0 ? " · ETA " + Formatting.Duration(TimeSpan.FromSeconds(eta.Value)) : "");
        return new DownloadProgress(overall, text);
    }
}

/// <summary>
/// Uses yt-dlp to download videos from regular web pages (it supports over a thousand sites).
/// The result is always merged or remuxed into MP4 with ffmpeg.
/// </summary>
public sealed class YtDlpDownloader
{
    private readonly DownloadEnvironment _env;

    public YtDlpDownloader(DownloadEnvironment env) => _env = env;

    public async Task<string> DownloadAsync(DownloadOptions opt, IProgress<DownloadProgress> progress, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await RunAsync(opt, progress, log, ct, impersonate: false).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && LooksBlocked(ex.Message))
        {
            // Sites behind bot protection often reject Python's TLS fingerprint; yt-dlp can mimic Chrome's.
            log("Blocked by the website - retrying with browser impersonation...");
            return await RunAsync(opt, progress, log, ct, impersonate: true).ConfigureAwait(false);
        }
    }

    internal static bool LooksBlocked(string error) =>
        error.Contains("403") || error.Contains("429")
        || error.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase)
        || error.Contains("anti-bot", StringComparison.OrdinalIgnoreCase)
        || error.Contains("impersonat", StringComparison.OrdinalIgnoreCase);

    /// <summary>yt-dlp's format sort: resolution limit first, then MP4-friendly codecs so remuxing is lossless.</summary>
    internal static string SortFor(int? maxHeight) => maxHeight switch
    {
        null => "res,vcodec:h264,ext:mp4:m4a",
        0 => "+res,+size",
        int h => $"res:{h},vcodec:h264,ext:mp4:m4a",
    };

    internal static List<string> BuildArguments(DownloadOptions opt, string ffmpegDir, bool impersonate)
    {
        var template = string.IsNullOrWhiteSpace(opt.FileName) ? "%(title).150B.%(ext)s" : Formatting.SafeFileName(opt.FileName) + ".%(ext)s";
        var args = new List<string>
        {
            "--newline", "--no-playlist", "--no-mtime", "--windows-filenames", "--encoding", "utf-8",
            "-f", "bv*+ba/b",
            "-S", SortFor(opt.MaxHeight),
            "--merge-output-format", "mp4",
            "--remux-video", "mp4",
            "--ffmpeg-location", ffmpegDir,
            "-P", opt.OutputFolder,
            "-o", template,
            "--progress-template", YtDlpOutputParser.ProgressTemplate,
            "--print", "after_move:SAVED %(filepath)s",
            // --print silently enables quiet mode, which hides all progress output. Turn it back off.
            "--no-quiet", "--progress",
            // Pause between requests so long downloads don't trip rate limits.
            "--sleep-requests", "0.5",
            "--concurrent-fragments", Math.Max(1, opt.Concurrency).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(opt.Referer)) args.AddRange(new[] { "--referer", opt.Referer });
        if (!string.IsNullOrWhiteSpace(opt.CookiesFromBrowser)) args.AddRange(new[] { "--cookies-from-browser", opt.CookiesFromBrowser });
        if (impersonate) args.AddRange(new[] { "--impersonate", "chrome", "--extractor-args", "generic:impersonate" });
        else if (!string.IsNullOrWhiteSpace(opt.UserAgent)) args.AddRange(new[] { "--user-agent", opt.UserAgent });
        if (!string.IsNullOrWhiteSpace(opt.Cookies)) args.AddRange(new[] { "--add-header", "Cookie:" + opt.Cookies });
        args.Add(opt.Url);
        return args;
    }

    private async Task<string> RunAsync(DownloadOptions opt, IProgress<DownloadProgress> progress, Action<string> log, CancellationToken ct, bool impersonate)
    {
        var ytdlp = _env.RequireYtDlp();
        var ffmpeg = _env.RequireFfmpeg();
        Directory.CreateDirectory(opt.OutputFolder);
        var parser = new YtDlpOutputParser();
        var errors = new List<string>();

        var request = new ProcessRequest
        {
            FileName = ytdlp,
            Arguments = BuildArguments(opt, Path.GetDirectoryName(ffmpeg)!, impersonate),
            WorkingDirectory = opt.OutputFolder,
            Environment = new Dictionary<string, string>
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUNBUFFERED"] = "1", // otherwise progress arrives in bursts when output is redirected
            },
            StandardOutput = line =>
            {
                if (parser.Parse(line, out var logLine) is { } p) progress.Report(p);
                if (logLine != null) log(logLine);
            },
            StandardError = line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                log(line);
                if (line.StartsWith("ERROR")) lock (errors) errors.Add(line);
            },
        };

        progress.Report(new DownloadProgress(null, "Starting yt-dlp..."));
        var exit = await _env.Processes.RunAsync(request, ct).ConfigureAwait(false);
        if (exit != 0 || parser.SavedPath == null)
            throw new InvalidOperationException(errors.Count > 0 ? string.Join(Environment.NewLine, errors) : $"yt-dlp exited with code {exit}.");
        return parser.SavedPath;
    }
}
