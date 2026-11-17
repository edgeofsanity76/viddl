using System.Net;
using Viddl.Core.Models;
using Viddl.Downloads.Processes;
using Viddl.Downloads.Queue;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class DownloadManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly List<string> _log = new();

    public void Dispose() => _dir.Dispose();

    private DownloadOptions Opt(string url) => new() { Url = url, OutputFolder = Path.Combine(_dir.Path, "out"), FileName = "v" };

    /// <summary>Acts as ffmpeg (copy input to output) or yt-dlp (print SAVED) depending on the program.</summary>
    private FakeProcesses Programs(bool ytDlpSucceeds = true) => new((req, ct) =>
    {
        if (req.FileName.EndsWith("yt-dlp.exe"))
        {
            if (!ytDlpSucceeds) { req.StandardError!("ERROR: Unsupported URL"); return Task.FromResult(1); }
            req.StandardOutput!("SAVED from-yt-dlp.mp4");
            return Task.FromResult(0);
        }
        return FakeProcesses.Ffmpeg().RunAsync(req, ct);
    });

    private Task<string> Run(string url, DownloadEngine engine, FakeProcesses? programs = null, FakeTools? tools = null) =>
        new DownloadManager(Env.Create(_http, programs ?? Programs(), tools, _dir.File("tmp")))
            .DownloadAsync(Opt(url), engine, new ProgressLog(), _log.Add, CancellationToken.None);

    [Fact]
    public async Task YtDlp_engine_always_uses_yt_dlp()
    {
        Assert.Equal("from-yt-dlp.mp4", await Run("https://cdn.test/v.m3u8", DownloadEngine.YtDlp));
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "out")));
    }

    [Fact]
    public async Task Routes_hls_dash_and_files()
    {
        _http.Text("https://cdn.test/v.m3u8", "#EXTM3U\n#EXTINF:1,\ns.ts\n#EXT-X-ENDLIST\n").Bytes("https://cdn.test/s.ts", new byte[] { 1 });
        Assert.EndsWith("v.mp4", await Run("https://cdn.test/v.m3u8", DownloadEngine.Auto));
        Assert.Contains("URL type: Hls", _log);

        Assert.EndsWith("v (1).mp4", await Run("https://cdn.test/m.mpd", DownloadEngine.BuiltIn));

        _http.Bytes("https://cdn.test/f.mp4", new byte[] { 1, 2 }, "video/mp4");
        Assert.EndsWith("v (2).mp4", await Run("https://cdn.test/f.mp4", DownloadEngine.Auto));
    }

    [Fact]
    public async Task File_url_that_serves_a_playlist_switches_to_hls()
    {
        _http.Text("https://cdn.test/f.mp4", "#EXTM3U\n#EXTINF:1,\ns.ts\n#EXT-X-ENDLIST\n", "application/x-mpegURL").Bytes("https://cdn.test/s.ts", new byte[] { 1 });
        Assert.EndsWith("v.mp4", await Run("https://cdn.test/f.mp4", DownloadEngine.Auto));
        Assert.Contains(_log, l => l.Contains("switching to the HLS engine"));
    }

    [Fact]
    public async Task Web_pages_use_yt_dlp_then_fall_back_to_scanning()
    {
        Assert.Equal("from-yt-dlp.mp4", await Run("https://site.test/watch", DownloadEngine.Auto));

        _http.Text("https://site.test/watch", "<title>Page</title><video src=\"https://cdn.test/f.mp4\">", "text/html")
             .Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var path = await Run("https://site.test/watch", DownloadEngine.Auto, Programs(ytDlpSucceeds: false));
        Assert.EndsWith("v.mp4", path);
        Assert.Contains(_log, l => l.StartsWith("yt-dlp failed"));
        Assert.Contains(_log, l => l.StartsWith("Using: https://cdn.test/f.mp4"));

        var referer = _http.Requests.Last(r => r.RequestUri!.ToString() == "https://cdn.test/f.mp4").Headers.Referrer;
        Assert.Equal("https://site.test/watch", referer!.ToString());
    }

    [Fact]
    public async Task BuiltIn_engine_scans_pages_without_yt_dlp()
    {
        _http.Text("https://site.test/p", "<video src=\"https://cdn.test/f.mp4\">", "text/html").Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var programs = Programs();
        await Run("https://site.test/p", DownloadEngine.BuiltIn, programs);
        Assert.DoesNotContain(programs.Requests, r => r.FileName.EndsWith("yt-dlp.exe"));

        await Run("https://site.test/p", DownloadEngine.Auto, programs, new FakeTools { YtDlp = null });
    }

    [Fact]
    public async Task Page_without_streams_explains_what_to_do()
    {
        _http.Text("https://site.test/empty", "<p>nothing</p>", "text/html");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run("https://site.test/empty", DownloadEngine.BuiltIn));
        Assert.Contains("built-in browser", ex.Message);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_by_the_fallback()
    {
        var cancelling = new FakeProcesses((_, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("https://site.test/watch", DownloadEngine.Auto, cancelling));
    }
}

public class DownloadQueueTests
{
    private static DownloadOptions Opt(string url) => new() { Url = url, OutputFolder = "C:\\out" };

    /// <summary>A downloader whose behaviour per URL is scripted by the test.</summary>
    private sealed class ScriptedDownloader : IDownloader
    {
        public Func<DownloadOptions, IProgress<DownloadProgress>, Action<string>, CancellationToken, Task<string>> Handler { get; set; }
            = (o, _, _, _) => Task.FromResult(o.Url + ".mp4");

        public Task<string> DownloadAsync(DownloadOptions opt, DownloadEngine engine, IProgress<DownloadProgress> progress, Action<string> log, CancellationToken ct) =>
            Handler(opt, progress, log, ct);
    }

    [Fact]
    public void Enqueue_rejects_duplicates_and_raises_events()
    {
        var q = new DownloadQueue(new ScriptedDownloader());
        int changes = 0;
        var log = new List<string>();
        q.StateChanged += () => changes++;
        q.Log += log.Add;

        var a = q.Enqueue(Opt("https://a"), DownloadEngine.Auto, "A")!;
        Assert.Null(q.Enqueue(Opt("https://a"), DownloadEngine.Auto, "A again"));
        Assert.Equal("https://a", a.Url);
        Assert.Equal(QueueItemState.Waiting, a.State);
        Assert.Equal("Waiting", a.Status);
        Assert.Equal(1, q.WaitingCount);
        Assert.Equal(1, changes);
        Assert.Contains("Queued: A - https://a", log);
    }

    [Fact]
    public async Task Runs_items_in_order_and_summarises()
    {
        var dl = new ScriptedDownloader();
        dl.Handler = (o, p, log, _) =>
        {
            if (o.Url.Contains("bad")) throw new InvalidOperationException("broken\nmore detail");
            if (o.Url.Contains("blocked")) throw new PageBlockedException(403, null);
            p.Report(new DownloadProgress(40, "working"));
            log("engine line");
            return Task.FromResult(o.Url + ".mp4");
        };
        var q = new DownloadQueue(dl);
        var good = q.Enqueue(Opt("https://good"), DownloadEngine.Auto, "Good")!;
        var bad = q.Enqueue(Opt("https://bad"), DownloadEngine.Auto, "Bad")!;
        var blocked = q.Enqueue(Opt("https://blocked"), DownloadEngine.Auto, "Blocked")!;
        var finished = new List<QueueItem>();
        QueueSummary? summary = null;
        var log = new List<string>();
        q.ItemFinished += finished.Add;
        q.Finished += s => summary = s;
        q.Log += log.Add;

        var result = await Task.Run(q.RunAsync);

        Assert.Equal(new[] { good, bad, blocked }, finished);
        Assert.Equal(QueueItemState.Done, good.State);
        Assert.Equal("https://good.mp4", good.OutputPath);
        Assert.Equal(100, good.Percent);
        Assert.Equal("Done", good.Status);
        Assert.Equal(QueueItemState.Failed, bad.State);
        Assert.Equal("Failed: broken", bad.Status);
        Assert.Equal("Website refused the request (HTTP 403)", blocked.Status);
        Assert.Equal(new QueueSummary(1, 2, false), summary);
        Assert.Same(summary, result);
        Assert.Equal("Finished: 1 saved, 2 failed", summary!.ToString());
        Assert.Contains("engine line", log);
        Assert.False(q.IsRunning);
        Assert.Null(q.Current);
    }

    [Fact]
    public async Task Done_status_shows_file_size_when_the_file_exists()
    {
        using var dir = new TempDir();
        var file = dir.Write("v.mp4", new string('x', 2048));
        var q = new DownloadQueue(new ScriptedDownloader { Handler = (_, _, _, _) => Task.FromResult(file) });
        var item = q.Enqueue(Opt("https://a"), DownloadEngine.Auto, "A")!;
        await Task.Run(q.RunAsync);
        Assert.Equal("Done  ·  0.0 MB", item.Status);
    }

    [Fact]
    public async Task Progress_only_moves_forward_and_unknown_keeps_position()
    {
        var reported = new List<double?>();
        var q = new DownloadQueue(new ScriptedDownloader
        {
            Handler = (o, p, _, _) =>
            {
                p.Report(new DownloadProgress(null, "starting"));
                p.Report(new DownloadProgress(60, "a"));
                p.Report(new DownloadProgress(30, "new phase"));
                p.Report(new DownloadProgress(null, "merging"));
                p.Report(new DownloadProgress(150, "over"));
                return Task.FromResult("x");
            },
        });
        q.Progress += (_, p) => reported.Add(p.Percent);
        var item = q.Enqueue(Opt("https://a"), DownloadEngine.Auto, "A")!;
        var statuses = new List<string>();
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(QueueItem.Status)) statuses.Add(item.Status); };

        await Task.Run(q.RunAsync);

        Assert.Equal(new double?[] { null, 60, 60, 60, 100 }, reported);
        Assert.Contains("60%  ·  new phase", statuses);
        Assert.Contains("starting", statuses);
    }

    [Fact]
    public async Task Stop_cancels_current_item_and_leaves_others_waiting()
    {
        DownloadQueue q = null!;
        var dl = new ScriptedDownloader
        {
            Handler = async (_, _, _, ct) =>
            {
                q.Stop();
                await Task.Delay(Timeout.Infinite, ct);
                return "never";
            },
        };
        q = new DownloadQueue(dl);
        var first = q.Enqueue(Opt("https://1"), DownloadEngine.Auto, "1")!;
        var second = q.Enqueue(Opt("https://2"), DownloadEngine.Auto, "2")!;

        var summary = await Task.Run(q.RunAsync);

        Assert.Null(summary); // nothing saved or failed
        Assert.Equal(QueueItemState.Cancelled, first.State);
        Assert.Equal("Cancelled", first.Status);
        Assert.Equal(QueueItemState.Waiting, second.State);
        Assert.Equal(1, q.WaitingCount);
    }

    [Fact]
    public async Task Summary_mentions_items_left_waiting()
    {
        DownloadQueue q = null!;
        q = new DownloadQueue(new ScriptedDownloader
        {
            Handler = (o, _, _, _) =>
            {
                q.Stop();
                return Task.FromResult("done");
            },
        });
        q.Enqueue(Opt("https://1"), DownloadEngine.Auto, "1");
        q.Enqueue(Opt("https://2"), DownloadEngine.Auto, "2");
        var summary = await Task.Run(q.RunAsync);
        Assert.Equal("Finished: 1 saved (stopped - others still waiting)", summary!.ToString());
    }

    [Fact]
    public async Task RunAsync_while_running_does_nothing()
    {
        var gate = new TaskCompletionSource<string>();
        var q = new DownloadQueue(new ScriptedDownloader { Handler = (_, _, _, _) => gate.Task });
        q.Enqueue(Opt("https://1"), DownloadEngine.Auto, "1");
        var running = Task.Run(q.RunAsync);
        while (!q.IsRunning) await Task.Delay(5);
        Assert.True(q.IsRunning);
        Assert.NotNull(q.Current);
        Assert.Null(await q.RunAsync());
        gate.SetResult("x");
        Assert.NotNull(await running);
        Assert.Null(await q.RunAsync()); // nothing waiting
    }

    [Fact]
    public async Task Remove_retry_and_clear_finished()
    {
        var dl = new ScriptedDownloader { Handler = (o, _, _, _) => o.Url.Contains("fail") ? throw new Exception("x") : Task.FromResult("ok") };
        var q = new DownloadQueue(dl);
        var ok = q.Enqueue(Opt("https://ok"), DownloadEngine.Auto, "ok")!;
        var fail = q.Enqueue(Opt("https://fail"), DownloadEngine.Auto, "fail")!;
        await Task.Run(q.RunAsync);

        q.Retry(new[] { ok, fail });
        Assert.Equal(QueueItemState.Done, ok.State); // only failed/cancelled items are retried
        Assert.Equal(QueueItemState.Waiting, fail.State);
        Assert.Equal(0, fail.Percent);

        q.ClearFinished();
        Assert.Equal(new[] { fail }, q.Items);
        q.Remove(new[] { fail });
        Assert.Empty(q.Items);
    }

    [Fact]
    public async Task Running_item_cannot_be_removed()
    {
        var gate = new TaskCompletionSource<string>();
        var q = new DownloadQueue(new ScriptedDownloader { Handler = (_, _, _, _) => gate.Task });
        var item = q.Enqueue(Opt("https://1"), DownloadEngine.Auto, "1")!;
        var run = Task.Run(q.RunAsync);
        while (!q.IsRunning) await Task.Delay(5);
        q.Remove(new[] { item });
        Assert.Single(q.Items);
        gate.SetResult("x");
        await run;
    }

    [Fact]
    public async Task Progress_is_delivered_on_the_callers_context_and_late_reports_are_ignored()
    {
        var context = new QueueingSyncContext();
        IProgress<DownloadProgress>? captured = null;
        Action<string>? capturedLog = null;
        var q = new DownloadQueue(new ScriptedDownloader
        {
            Handler = (_, p, log, _) =>
            {
                captured = p;
                capturedLog = log;
                p.Report(new DownloadProgress(10, "posted"));
                log("posted log");
                return Task.FromResult("x");
            },
        });
        var item = q.Enqueue(Opt("https://1"), DownloadEngine.Auto, "1")!;
        var logs = new List<string>();
        q.Log += logs.Add;

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task<QueueSummary?> run;
        try { run = q.RunAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await run;
        Assert.Equal(100, item.Percent);
        captured!.Report(new DownloadProgress(5, "late")); // after completion
        capturedLog!("late log");
        Assert.True(context.RunAll() >= 3);
        Assert.Equal(100, item.Percent);
        Assert.Equal("Done", item.Status);
        Assert.Contains("posted log", logs);
    }

    [Fact]
    public void QueueItem_raises_property_changes_once_per_change()
    {
        var item = new QueueItem { Options = Opt("u"), Engine = DownloadEngine.Auto, Name = "n" };
        var names = new List<string?>();
        item.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        item.Status = "a";
        item.Status = "a";
        item.Percent = 5;
        item.OutputPath = "p";
        item.State = QueueItemState.Done;
        Assert.Equal(new[] { "Status", "Percent", "OutputPath", "State" }, names);
    }
}

public class DownloadQueueEventTests
{
    private sealed class FailingDownloader : IDownloader
    {
        public Task<string> DownloadAsync(DownloadOptions opt, DownloadEngine engine, IProgress<DownloadProgress> progress, Action<string> log, CancellationToken ct) =>
            throw new InvalidOperationException("fails");
    }

    [Fact]
    public async Task Remove_and_retry_raise_state_changes()
    {
        var q = new DownloadQueue(new FailingDownloader());
        var item = q.Enqueue(new DownloadOptions { Url = "https://a", OutputFolder = @"C:\out" }, DownloadEngine.Auto, "A")!;
        await Task.Run(q.RunAsync);
        int changes = 0;
        q.StateChanged += () => changes++;

        q.Retry(new[] { item });
        Assert.Equal(1, changes);
        q.Remove(new[] { item });
        Assert.Equal(2, changes);
        Assert.Empty(q.Items);
    }
}

public class DownloadQueueClearTests
{
    [Fact]
    public async Task Clear_removes_everything_except_the_running_item()
    {
        var gate = new TaskCompletionSource<string>();
        var q = new DownloadQueue(new GatedDownloader(gate.Task));
        var running = q.Enqueue(new DownloadOptions { Url = "https://1", OutputFolder = "out" }, DownloadEngine.Auto, "1")!;
        q.Enqueue(new DownloadOptions { Url = "https://2", OutputFolder = "out" }, DownloadEngine.Auto, "2");
        q.Enqueue(new DownloadOptions { Url = "https://3", OutputFolder = "out" }, DownloadEngine.Auto, "3");
        var run = Task.Run(q.RunAsync);
        while (running.State != QueueItemState.Running) await Task.Delay(5);
        int changes = 0;
        q.StateChanged += () => changes++;

        Assert.Equal(2, q.Clear());
        Assert.Equal(new[] { running }, q.Items);
        Assert.Equal(1, changes);

        gate.SetResult("done.mp4");
        await run;
        Assert.Equal(1, q.Clear());
        Assert.Empty(q.Items);
        Assert.Equal(0, q.Clear());
    }

    private sealed class GatedDownloader : IDownloader
    {
        private readonly Task<string> _result;

        public GatedDownloader(Task<string> result) => _result = result;

        public Task<string> DownloadAsync(DownloadOptions opt, DownloadEngine engine, IProgress<DownloadProgress> progress, Action<string> log, CancellationToken ct) => _result;
    }
}
