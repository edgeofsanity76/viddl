using System.IO.Compression;
using System.Net;
using Viddl.Core.Models;
using Viddl.Downloads.Processes;
using Viddl.Downloads.Tools;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Captures_output_errors_environment_and_exit_code()
    {
        using var dir = new TempDir();
        var stdout = new List<string>();
        var stderr = new List<string>();
        var exit = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            FileName = Cmd,
            Arguments = new[] { "/c", "echo hello %VIDDL_TEST%& cd & echo oops 1>&2 & exit /b 3" },
            Environment = new Dictionary<string, string> { ["VIDDL_TEST"] = "world" },
            WorkingDirectory = dir.Path,
            StandardOutput = l => { lock (stdout) stdout.Add(l); },
            StandardError = l => { lock (stderr) stderr.Add(l); },
        }, CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Contains("hello world", stdout);
        Assert.Contains(dir.Path, stdout);
        Assert.Contains(stderr, l => l.Contains("oops"));
    }

    [Fact]
    public async Task Runs_without_output_handlers()
    {
        var exit = await new ProcessRunner().RunAsync(new ProcessRequest { FileName = Cmd, Arguments = new[] { "/c", "echo x & echo y 1>&2" } }, CancellationToken.None);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Cancellation_kills_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            new ProcessRequest { FileName = Cmd, Arguments = new[] { "/c", "ping -n 30 127.0.0.1 >nul" } }, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Graceful_stop_sends_q_and_returns_exit_code()
    {
        // "set /p" reads the line we send; the program then exits by itself with code 5.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exit = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            FileName = Cmd,
            Arguments = new[] { "/c", "set /p x= & exit /b 5" },
            GracefulStop = true,
            GracefulStopTimeout = TimeSpan.FromSeconds(1),
        }, cts.Token);
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Graceful_stop_kills_after_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new ProcessRunner().RunAsync(new ProcessRequest
        {
            FileName = Cmd,
            Arguments = new[] { "/c", "ping -n 30 127.0.0.1 >nul" },
            GracefulStop = true,
            GracefulStopTimeout = TimeSpan.FromMilliseconds(300),
        }, cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }
}

public class ToolTests
{
    [Fact]
    public void Locator_searches_folders_then_path()
    {
        using var first = new TempDir();
        using var onPath = new TempDir();
        first.Write("ffmpeg.exe", "");
        onPath.Write("yt-dlp.exe", "");
        var locator = new ToolLocator(new[] { first.Path }, () => $"C:\\nowhere;\"{onPath.Path}\"");

        Assert.Equal(first.File("ffmpeg.exe"), locator.FindFfmpeg());
        Assert.Equal(onPath.File("yt-dlp.exe"), locator.FindYtDlp());

        var none = new ToolLocator(Array.Empty<string>(), () => null);
        Assert.Null(none.FindFfmpeg());
        // The default PATH source is the process environment; whatever it finds must exist.
        var fromProcessPath = new ToolLocator(Array.Empty<string>()).FindYtDlp();
        Assert.True(fromProcessPath == null || File.Exists(fromProcessPath));
    }

    [Fact]
    public void Environment_requires_tools()
    {
        var env = Env.Create(new FakeHttp(), tools: new FakeTools { Ffmpeg = null, YtDlp = null });
        var ex = Assert.Throws<ToolMissingException>(() => env.RequireFfmpeg());
        Assert.Equal("ffmpeg", ex.Tool);
        Assert.Contains("ffmpeg.exe", ex.Message);
        Assert.Equal("yt-dlp", Assert.Throws<ToolMissingException>(() => env.RequireYtDlp()).Tool);

        var real = new DownloadEnvironment { Tools = new FakeTools() };
        Assert.IsType<ProcessRunner>(real.Processes);
        Assert.Null(real.HttpHandlers);
        Assert.EndsWith("viddl", real.TempRoot);
        Assert.Equal(TimeSpan.FromMilliseconds(300), real.ProgressInterval);
        Assert.True((DateTime.Now - real.Now()).Duration() < TimeSpan.FromMinutes(1));
        Assert.True(real.Delay(TimeSpan.Zero, CancellationToken.None).IsCompleted);
        Assert.Equal(@"C:\tools\ffmpeg.exe", real.RequireFfmpeg());
        Assert.Equal(@"C:\tools\yt-dlp.exe", real.RequireYtDlp());
    }

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var e in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(e).Open());
                w.Write("binary:" + e);
            }
        return ms.ToArray();
    }

    [Fact]
    public async Task Installer_downloads_yt_dlp_and_extracts_ffmpeg()
    {
        using var dir = new TempDir();
        var http = new FakeHttp()
            .Bytes("https://dl.test/yt-dlp.exe", new byte[300_000])
            .Bytes("https://dl.test/ffmpeg.zip", Zip("ffmpeg-x/bin/ffmpeg.exe", "ffmpeg-x/bin/ffprobe.exe", "ffmpeg-x/doc/readme.txt"));
        var installer = new ToolInstaller(Path.Combine(dir.Path, "tools"), http.Factory, "https://dl.test/yt-dlp.exe", "https://dl.test/ffmpeg.zip");
        var progress = new ProgressLog();

        await installer.InstallYtDlpAsync(progress, CancellationToken.None);
        await installer.InstallFfmpegAsync(progress, CancellationToken.None);

        Assert.Equal(Path.Combine(dir.Path, "tools"), installer.ToolsDir);
        Assert.Equal(300_000, new FileInfo(Path.Combine(installer.ToolsDir, "yt-dlp.exe")).Length);
        Assert.Equal("binary:ffmpeg-x/bin/ffmpeg.exe", File.ReadAllText(Path.Combine(installer.ToolsDir, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(installer.ToolsDir, "ffprobe.exe")));
        Assert.True(progress.Any("Downloading yt-dlp"));
        Assert.True(progress.Any("Extracting ffmpeg"));
        Assert.True(progress.Any("ffmpeg installed"));
        Assert.False(File.Exists(Path.Combine(installer.ToolsDir, "yt-dlp.exe.part")));
    }

    [Fact]
    public async Task Installer_rejects_archive_without_ffmpeg_and_http_errors()
    {
        using var dir = new TempDir();
        var http = new FakeHttp()
            .Bytes("https://dl.test/bad.zip", Zip("x/bin/other.exe"))
            .Status("https://dl.test/missing.exe", HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ToolInstaller(dir.Path, http.Factory, ffmpegZipUrl: "https://dl.test/bad.zip").InstallFfmpegAsync(new ProgressLog(), CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ToolInstaller(dir.Path, http.Factory, ytDlpUrl: "https://dl.test/missing.exe").InstallYtDlpAsync(new ProgressLog(), CancellationToken.None));
    }

    [Fact]
    public void ExtractFfmpeg_honours_cancellation()
    {
        using var dir = new TempDir();
        var zip = dir.File("f.zip");
        File.WriteAllBytes(zip, Zip("a/bin/ffmpeg.exe"));
        Assert.Throws<OperationCanceledException>(() => ToolInstaller.ExtractFfmpeg(zip, dir.Path, new CancellationToken(true)));
        Assert.Contains("github.com", ToolInstaller.DefaultYtDlpUrl);
        Assert.EndsWith(".zip", ToolInstaller.DefaultFfmpegZipUrl);
    }
}

public class ProcessRunnerEdgeTests
{
    [Fact]
    public void Quit_and_kill_tolerate_processes_that_never_started()
    {
        using var notStarted = new System.Diagnostics.Process();
        Assert.False(ProcessRunner.TrySendQuit(notStarted));
        ProcessRunner.Kill(notStarted); // must not throw
    }
}
