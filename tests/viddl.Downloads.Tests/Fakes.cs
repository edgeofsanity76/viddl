using Viddl.Downloads;
using Viddl.Downloads.Processes;
using Viddl.Downloads.Tools;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public sealed class FakeTools : IToolLocator
{
    public string? Ffmpeg { get; set; } = @"C:\tools\ffmpeg.exe";
    public string? YtDlp { get; set; } = @"C:\tools\yt-dlp.exe";

    public string? FindFfmpeg() => Ffmpeg;

    public string? FindYtDlp() => YtDlp;
}

/// <summary>Records process requests and answers them with a scripted handler.</summary>
public sealed class FakeProcesses : IProcessRunner
{
    private readonly Func<ProcessRequest, CancellationToken, Task<int>> _handler;

    public FakeProcesses(Func<ProcessRequest, CancellationToken, Task<int>> handler) => _handler = handler;

    public List<ProcessRequest> Requests { get; } = new();

    public Task<int> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return _handler(request, ct);
    }

    /// <summary>
    /// Behaves like ffmpeg converting: copies the first "-i" input file to the output (last argument) and prints
    /// "Duration"/"time=" progress. Network inputs produce a small placeholder file.
    /// </summary>
    public static FakeProcesses Ffmpeg(int exitCode = 0, Action<ProcessRequest>? onRun = null) => new((req, ct) =>
    {
        onRun?.Invoke(req);
        var args = req.Arguments;
        var output = args[^1];
        var input = args[args.ToList().IndexOf("-i") + 1];
        req.StandardError?.Invoke("Input #0, mpegts");
        req.StandardError?.Invoke("  Duration: 00:00:10.00, start: 0.000000, bitrate: 1000 kb/s");
        req.StandardError?.Invoke("frame=  100 fps=0.0 q=-1.0 size=  1024kB time=00:00:05.00 bitrate=N/A");
        req.StandardError?.Invoke("frame=  200 fps=0.0 q=-1.0 size=  2048kB time=00:00:10.00 bitrate=N/A");
        if (exitCode == 0)
        {
            if (File.Exists(input)) File.Copy(input, output, overwrite: true);
            else File.WriteAllText(output, "converted:" + input);
        }
        return Task.FromResult(exitCode);
    });
}

public static class Env
{
    public static readonly List<TimeSpan> NoDelays = new();

    /// <summary>A test environment: fake HTTP, fake programs, no real waiting.</summary>
    public static DownloadEnvironment Create(FakeHttp http, IProcessRunner? processes = null, FakeTools? tools = null,
        string? tempRoot = null, List<TimeSpan>? delays = null) => new()
    {
        HttpHandlers = http.Factory,
        Processes = processes ?? FakeProcesses.Ffmpeg(),
        Tools = tools ?? new FakeTools(),
        TempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "viddl-tests", "tmp"),
        ProgressInterval = TimeSpan.FromMilliseconds(5),
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
        Delay = (t, ct) =>
        {
            if (delays != null) lock (delays) delays.Add(t);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        },
    };
}

/// <summary>Collects progress reports thread-safely.</summary>
public sealed class ProgressLog : IProgress<Viddl.Core.Models.DownloadProgress>
{
    public List<Viddl.Core.Models.DownloadProgress> Items { get; } = new();

    public void Report(Viddl.Core.Models.DownloadProgress value)
    {
        lock (Items) Items.Add(value);
    }

    public IReadOnlyList<double> Percents
    {
        get { lock (Items) return Items.Where(i => i.Percent != null).Select(i => i.Percent!.Value).ToList(); }
    }

    public bool Any(string text)
    {
        lock (Items) return Items.Any(i => i.Status.Contains(text));
    }
}
