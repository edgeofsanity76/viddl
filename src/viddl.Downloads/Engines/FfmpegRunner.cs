using System.Globalization;
using System.Text.RegularExpressions;
using Viddl.Core.Models;
using Viddl.Core.Net;
using Viddl.Downloads.Processes;

namespace Viddl.Downloads.Engines;

/// <summary>Turns ffmpeg's stderr output into progress (it prints "Duration: ..." then "time=...").</summary>
public sealed class FfmpegOutputParser
{
    private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly string _label;
    private double? _duration;

    public FfmpegOutputParser(string label, double? knownDuration)
    {
        _label = label;
        _duration = knownDuration > 0 ? knownDuration : null;
    }

    /// <summary>Returns a progress report for a line, or null if the line carries no progress.</summary>
    public DownloadProgress? Parse(string line)
    {
        if (_duration == null && DurationRx.Match(line) is { Success: true } dm) _duration = ToSeconds(dm);
        if (TimeRx.Match(line) is not { Success: true } tm) return null;
        var t = ToSeconds(tm);
        double? pct = _duration > 0 ? Math.Min(100, t * 100 / _duration.Value) : null;
        var text = $"{_label}: {TimeSpan.FromSeconds(t):hh\\:mm\\:ss}" +
                   (_duration > 0 ? $" / {TimeSpan.FromSeconds(_duration.Value):hh\\:mm\\:ss}" : "");
        return new DownloadProgress(pct, text);
    }

    private static double ToSeconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
        + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
}

/// <summary>Runs ffmpeg with progress reporting.</summary>
public sealed class FfmpegRunner
{
    private readonly DownloadEnvironment _env;

    public FfmpegRunner(DownloadEnvironment env) => _env = env;

    /// <summary>Arguments that make ffmpeg send the right headers for network inputs.</summary>
    public static IEnumerable<string> HttpInputArgs(DownloadOptions opt)
    {
        yield return "-user_agent";
        yield return string.IsNullOrWhiteSpace(opt.UserAgent) ? HttpClients.DefaultUserAgent : opt.UserAgent!;
        if (!string.IsNullOrWhiteSpace(opt.Referer))
        {
            yield return "-referer";
            yield return opt.Referer!;
        }
        if (!string.IsNullOrWhiteSpace(opt.Cookies))
        {
            yield return "-headers";
            yield return $"Cookie: {opt.Cookies}\r\n";
        }
    }

    /// <param name="gracefulStopOnCancel">
    /// True: cancelling lets ffmpeg finish the file (live recording) and returns normally.
    /// False: cancelling kills ffmpeg and throws <see cref="OperationCanceledException"/>.
    /// </param>
    public async Task RunAsync(IEnumerable<string> args, IProgress<DownloadProgress> progress, CancellationToken ct,
        double? knownDuration = null, string label = "Processing", bool gracefulStopOnCancel = false)
    {
        var ffmpeg = _env.RequireFfmpeg();
        var parser = new FfmpegOutputParser(label, knownDuration);
        var tail = new Queue<string>();
        var request = new ProcessRequest
        {
            FileName = ffmpeg,
            Arguments = new[] { "-hide_banner", "-y" }.Concat(args).ToList(),
            GracefulStop = gracefulStopOnCancel,
            StandardError = line =>
            {
                lock (tail)
                {
                    tail.Enqueue(line);
                    if (tail.Count > 25) tail.Dequeue();
                }
                if (parser.Parse(line) is { } p) progress.Report(p);
            },
        };

        var exit = await _env.Processes.RunAsync(request, ct).ConfigureAwait(false);
        if (exit != 0 && !(gracefulStopOnCancel && ct.IsCancellationRequested))
        {
            string log;
            lock (tail) log = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"ffmpeg failed (exit code {exit}):{Environment.NewLine}{log}");
        }
    }
}
