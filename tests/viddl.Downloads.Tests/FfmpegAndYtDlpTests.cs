using Viddl.Core.Models;
using Viddl.Core.Net;
using Viddl.Downloads.Engines;
using Viddl.Downloads.Processes;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class FfmpegTests
{
    [Fact]
    public void Parser_reads_duration_then_time()
    {
        var p = new FfmpegOutputParser("Converting", null);
        Assert.Null(p.Parse("Input #0"));
        Assert.Null(p.Parse("  Duration: 00:01:40.00, start: 0"));
        var half = p.Parse("frame=1 time=00:00:50.00 bitrate=N/A")!;
        Assert.Equal(50, half.Percent);
        Assert.Equal("Converting: 00:00:50 / 00:01:40", half.Status);
        Assert.Equal(100, p.Parse("time=00:02:00.00")!.Percent); // capped
    }

    [Fact]
    public void Parser_without_duration_reports_time_only()
    {
        var p = new FfmpegOutputParser("Recording", 0);
        var r = p.Parse("time=01:00:00.50")!;
        Assert.Null(r.Percent);
        Assert.Equal("Recording: 01:00:00", r.Status);

        var known = new FfmpegOutputParser("X", 20);
        Assert.Null(known.Parse("Duration: 00:00:99.00"));
        Assert.Equal(50, known.Parse("time=00:00:10.00")!.Percent); // known duration wins
    }

    [Fact]
    public void HttpInputArgs_include_agent_referer_and_cookies()
    {
        var plain = FfmpegRunner.HttpInputArgs(new DownloadOptions { Url = "u", OutputFolder = "o" }).ToList();
        Assert.Equal(new[] { "-user_agent", HttpClients.DefaultUserAgent }, plain);

        var full = FfmpegRunner.HttpInputArgs(new DownloadOptions { Url = "u", OutputFolder = "o", UserAgent = "UA", Referer = "https://r/", Cookies = "a=1" }).ToList();
        Assert.Equal(new[] { "-user_agent", "UA", "-referer", "https://r/", "-headers", "Cookie: a=1\r\n" }, full);
    }

    [Fact]
    public async Task Runner_reports_progress_and_fails_with_log_tail()
    {
        var progress = new ProgressLog();
        var ok = FakeProcesses.Ffmpeg();
        await new FfmpegRunner(Env.Create(new FakeHttp(), ok)).RunAsync(new[] { "-i", "in", "out" }, progress, CancellationToken.None, label: "Work");
        Assert.Equal(new[] { "-hide_banner", "-y", "-i", "in", "out" }, ok.Requests.Single().Arguments);
        Assert.Equal(new double[] { 50, 100 }, progress.Percents);
        File.Delete("out");

        var failing = new FakeProcesses((r, _) => { r.StandardError!("bad input"); return Task.FromResult(1); });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FfmpegRunner(Env.Create(new FakeHttp(), failing)).RunAsync(new[] { "x" }, progress, CancellationToken.None));
        Assert.Contains("exit code 1", ex.Message);
        Assert.Contains("bad input", ex.Message);
    }

    [Fact]
    public async Task Runner_keeps_only_recent_output_in_error()
    {
        var noisy = new FakeProcesses((r, _) =>
        {
            for (int i = 0; i < 40; i++) r.StandardError!($"line {i}");
            return Task.FromResult(2);
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FfmpegRunner(Env.Create(new FakeHttp(), noisy)).RunAsync(new[] { "x" }, new ProgressLog(), CancellationToken.None));
        Assert.DoesNotContain("line 5" + Environment.NewLine, ex.Message);
        Assert.Contains("line 39", ex.Message);
    }

    [Fact]
    public async Task Graceful_stop_ignores_exit_code_after_cancel()
    {
        using var cts = new CancellationTokenSource();
        var stopped = new FakeProcesses((r, _) => { Assert.True(r.GracefulStop); cts.Cancel(); return Task.FromResult(255); });
        await new FfmpegRunner(Env.Create(new FakeHttp(), stopped)).RunAsync(new[] { "x" }, new ProgressLog(), cts.Token, gracefulStopOnCancel: true);
    }

    [Fact]
    public async Task Runner_requires_ffmpeg()
    {
        var env = Env.Create(new FakeHttp(), tools: new FakeTools { Ffmpeg = null });
        await Assert.ThrowsAsync<ToolMissingException>(() => new FfmpegRunner(env).RunAsync(new[] { "x" }, new ProgressLog(), CancellationToken.None));
    }
}

public class YtDlpTests
{
    [Fact]
    public void Parser_maps_two_passes_and_merge_onto_one_scale()
    {
        var p = new YtDlpOutputParser();
        Assert.Null(p.Parse("[info] abc: Downloading 1 format(s): 137+140", out var log));
        Assert.StartsWith("[info]", log);
        p.Parse("[download] Destination: video.f137.mp4", out _);

        var first = p.Parse("PROGRESS 5242880|10485760|NA|1048576|65|NA|NA", out log)!;
        Assert.Null(log);
        Assert.Equal(23.75, first.Percent);
        Assert.Equal("Downloading part 1/2 · 5.0 MB · 1.0 MB/s · ETA 01:05", first.Status);

        p.Parse("[download] Destination: video.f140.m4a", out _);
        var second = p.Parse("PROGRESS 10|NA|20|NA|NA|NA|NA", out _)!;
        Assert.Equal(71.25, second.Percent);
        Assert.StartsWith("Downloading part 2/2", second.Status);

        var merge = p.Parse("[Merger] Merging formats into \"video.mp4\"", out _)!;
        Assert.Equal(96, merge.Percent);

        Assert.Null(p.Parse("SAVED C:\\out\\video.mp4", out log));
        Assert.Equal("C:\\out\\video.mp4", p.SavedPath);
        Assert.Null(log);
    }

    [Fact]
    public void Parser_prefers_fragments_and_handles_unknown_totals()
    {
        var p = new YtDlpOutputParser();
        p.Parse("[download] video.mp4 has already been downloaded", out _);
        var frag = p.Parse("PROGRESS 999|NA|123456|NA|7200|16|64", out _)!;
        Assert.Equal(23.75, frag.Percent);
        Assert.Contains("ETA 2:00:00", frag.Status);
        Assert.Equal("Downloading", frag.Status.Split(" · ")[0]);

        var unknown = p.Parse("PROGRESS 2097152|NA|NA|NA|NA|NA|NA", out _)!;
        Assert.Null(unknown.Percent);
        Assert.Equal("Downloading: 2.0 MB", unknown.Status);
        Assert.Equal("Downloading: 0.0 MB", p.Parse("PROGRESS NA", out _)!.Status);

        Assert.Null(p.Parse("   ", out var log));
        Assert.Null(log);
        Assert.Equal(96, p.Parse("[VideoRemuxer] Remuxing", out _)!.Percent);
        Assert.Equal(96, p.Parse("[FixupM3u8] Fixing", out _)!.Percent);
    }

    [Theory]
    [InlineData(null, "res,vcodec:h264,ext:mp4:m4a")]
    [InlineData(0, "+res,+size")]
    [InlineData(720, "res:720,vcodec:h264,ext:mp4:m4a")]
    public void Sort_order(int? height, string expected) => Assert.Equal(expected, YtDlpDownloader.SortFor(height));

    [Theory]
    [InlineData("HTTP Error 403: Forbidden", true)]
    [InlineData("HTTP Error 429", true)]
    [InlineData("Got blocked by Cloudflare", true)]
    [InlineData("anti-bot challenge", true)]
    [InlineData("try --impersonate", true)]
    [InlineData("Unsupported URL", false)]
    public void Detects_blocking_errors(string error, bool blocked) => Assert.Equal(blocked, YtDlpDownloader.LooksBlocked(error));

    [Fact]
    public void Arguments_reflect_options()
    {
        var minimal = YtDlpDownloader.BuildArguments(new DownloadOptions { Url = "https://v/1", OutputFolder = "C:\\o" }, "C:\\ff", false);
        Assert.Contains("%(title).150B.%(ext)s", minimal);
        Assert.Contains("--no-quiet", minimal);
        Assert.Equal("https://v/1", minimal[^1]);
        Assert.DoesNotContain("--referer", minimal);

        var full = YtDlpDownloader.BuildArguments(new DownloadOptions
        {
            Url = "https://v/2", OutputFolder = "C:\\o", FileName = "My: clip", Referer = "https://r", UserAgent = "UA",
            Cookies = "a=1", CookiesFromBrowser = "firefox", Concurrency = 0, MaxHeight = 480,
        }, "C:\\ff", false);
        Assert.Contains("My_ clip.%(ext)s", full);
        Assert.Contains("--referer", full);
        Assert.Contains("--cookies-from-browser", full);
        Assert.Contains("--user-agent", full);
        Assert.Contains("Cookie:a=1", full);
        Assert.Equal("1", full[full.IndexOf("--concurrent-fragments") + 1]);

        var imp = YtDlpDownloader.BuildArguments(new DownloadOptions { Url = "u", OutputFolder = "o", UserAgent = "UA" }, "f", true);
        Assert.Contains("--impersonate", imp);
        Assert.DoesNotContain("--user-agent", imp);
    }

    private static FakeProcesses Script(Func<ProcessRequest, int> run) => new((r, _) => Task.FromResult(run(r)));

    [Fact]
    public async Task Download_returns_saved_path_and_reports_progress()
    {
        using var dir = new TempDir();
        var procs = Script(r =>
        {
            Assert.Equal(@"C:\tools\yt-dlp.exe", r.FileName);
            Assert.Equal("1", r.Environment["PYTHONUNBUFFERED"]);
            r.StandardOutput!("[download] Destination: x.mp4");
            r.StandardOutput!("PROGRESS 50|100|NA|NA|NA|NA|NA");
            r.StandardError!("WARNING: something");
            r.StandardError!("");
            r.StandardOutput!("SAVED " + Path.Combine(dir.Path, "x.mp4"));
            return 0;
        });
        var progress = new ProgressLog();
        var log = new List<string>();
        var path = await new YtDlpDownloader(Env.Create(new FakeHttp(), procs)).DownloadAsync(
            new DownloadOptions { Url = "https://v/1", OutputFolder = dir.Path }, progress, log.Add, CancellationToken.None);

        Assert.Equal(Path.Combine(dir.Path, "x.mp4"), path);
        Assert.Contains(47.5, progress.Percents);
        Assert.Contains("WARNING: something", log);
    }

    [Fact]
    public async Task Download_retries_with_impersonation_when_blocked()
    {
        using var dir = new TempDir();
        int calls = 0;
        var procs = Script(r =>
        {
            calls++;
            if (!r.Arguments.Contains("--impersonate"))
            {
                r.StandardError!("ERROR: HTTP Error 403: Forbidden");
                return 1;
            }
            r.StandardOutput!("SAVED done.mp4");
            return 0;
        });
        var log = new List<string>();
        var path = await new YtDlpDownloader(Env.Create(new FakeHttp(), procs)).DownloadAsync(
            new DownloadOptions { Url = "u", OutputFolder = dir.Path }, new ProgressLog(), log.Add, CancellationToken.None);
        Assert.Equal("done.mp4", path);
        Assert.Equal(2, calls);
        Assert.Contains(log, l => l.Contains("impersonation"));
    }

    [Fact]
    public async Task Download_reports_errors()
    {
        using var dir = new TempDir();
        var opt = new DownloadOptions { Url = "u", OutputFolder = dir.Path };

        var withError = Script(r => { r.StandardError!("ERROR: Unsupported URL"); return 1; });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new YtDlpDownloader(Env.Create(new FakeHttp(), withError)).DownloadAsync(opt, new ProgressLog(), _ => { }, CancellationToken.None));
        Assert.Equal("ERROR: Unsupported URL", ex.Message);

        var silent = Script(_ => 0); // exit 0 but no SAVED line
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new YtDlpDownloader(Env.Create(new FakeHttp(), silent)).DownloadAsync(opt, new ProgressLog(), _ => { }, CancellationToken.None));
        Assert.Contains("exited with code 0", ex.Message);

        await Assert.ThrowsAsync<ToolMissingException>(() =>
            new YtDlpDownloader(Env.Create(new FakeHttp(), silent, new FakeTools { YtDlp = null })).DownloadAsync(opt, new ProgressLog(), _ => { }, CancellationToken.None));
    }
}
