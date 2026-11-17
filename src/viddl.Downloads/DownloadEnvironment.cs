using Viddl.Core.Net;
using Viddl.Downloads.Processes;
using Viddl.Downloads.Tools;

namespace Viddl.Downloads;

/// <summary>
/// Everything the download engines need from the outside world: HTTP, external programs, tool locations,
/// a temp folder and timing. Production code uses the defaults; tests substitute fakes.
/// </summary>
public sealed class DownloadEnvironment
{
    public HttpHandlerFactory? HttpHandlers { get; init; }
    public IProcessRunner Processes { get; init; } = new ProcessRunner();
    public required IToolLocator Tools { get; init; }

    /// <summary>Parent folder for per-download temporary folders.</summary>
    public string TempRoot { get; init; } = Path.Combine(Path.GetTempPath(), "viddl");

    /// <summary>Used for retry back-off and the live-stream check.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>How often HLS progress is reported while segments download.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    public string RequireFfmpeg() => Tools.FindFfmpeg() ?? throw new ToolMissingException("ffmpeg");

    public string RequireYtDlp() => Tools.FindYtDlp() ?? throw new ToolMissingException("yt-dlp");
}

/// <summary>A required helper program isn't installed.</summary>
public sealed class ToolMissingException : Exception
{
    public ToolMissingException(string tool)
        : base($"{tool} was not found. Install it from Settings → Install / update ffmpeg & yt-dlp, or put {tool}.exe on your PATH.")
    {
        Tool = tool;
    }

    public string Tool { get; }
}
