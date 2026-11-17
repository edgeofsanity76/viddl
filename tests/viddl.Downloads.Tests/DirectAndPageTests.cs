using System.Net;
using Viddl.Core.Models;
using Viddl.Downloads.Engines;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class DirectDownloaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();

    public void Dispose() => _dir.Dispose();

    private DownloadOptions Opt(string url, string? name = "video", long? expected = null) =>
        new() { Url = url, OutputFolder = _dir.Path, FileName = name, ExpectedBytes = expected };

    [Fact]
    public async Task Mp4_is_saved_as_is_with_progress()
    {
        var body = new byte[3 * (1 << 17) + 5];
        _http.Route("https://cdn.test/v.mp4", _ =>
        {
            var content = new StreamContent(new SlowStream(body));
            content.Headers.ContentLength = body.Length;
            content.Headers.TryAddWithoutValidation("Content-Type", "video/mp4");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var progress = new ProgressLog();
        var ffmpeg = FakeProcesses.Ffmpeg();

        var path = await new DirectDownloader(Env.Create(_http, ffmpeg)).DownloadFileAsync(Opt("https://cdn.test/v.mp4"), progress, CancellationToken.None);

        Assert.Equal(_dir.File("video.mp4"), path);
        Assert.Equal(body.Length, new FileInfo(path).Length);
        Assert.Empty(ffmpeg.Requests);
        Assert.False(File.Exists(_dir.File("video.part")));
        Assert.True(progress.Any("Downloading:"));
        Assert.True(progress.Any(" / 0.4 MB"));
    }

    [Fact]
    public async Task Other_containers_are_remuxed_and_expected_size_is_used()
    {
        _http.Route("https://cdn.test/clip.webm", _ =>
        {
            var content = new StreamContent(new SlowStream(new byte[1 << 18]));
            content.Headers.TryAddWithoutValidation("Content-Type", "video/webm");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var ffmpeg = FakeProcesses.Ffmpeg();
        var progress = new ProgressLog();

        var path = await new DirectDownloader(Env.Create(_http, ffmpeg)).DownloadFileAsync(
            Opt("https://cdn.test/clip.webm", name: null, expected: 1 << 18), progress, CancellationToken.None);

        Assert.Equal(_dir.File("clip.mp4"), path);
        Assert.Single(ffmpeg.Requests);
        Assert.True(progress.Percents.All(p => p <= 100));
        Assert.Contains(progress.Percents, p => p is > 0 and <= 90);
        Assert.False(File.Exists(_dir.File("clip.part")));
    }

    [Fact]
    public async Task Mp4_url_with_unexpected_type_is_remuxed()
    {
        _http.Bytes("https://cdn.test/odd.mp4", new byte[10], "video/quicktime");
        var ffmpeg = FakeProcesses.Ffmpeg();
        await new DirectDownloader(Env.Create(_http, ffmpeg)).DownloadFileAsync(Opt("https://cdn.test/odd.mp4"), new ProgressLog(), CancellationToken.None);
        Assert.Single(ffmpeg.Requests);
    }

    [Fact]
    public async Task Detects_playlists_and_web_pages()
    {
        _http.Text("https://cdn.test/a.mp4", "#EXTM3U", "application/vnd.apple.mpegurl");
        _http.Text("https://cdn.test/b.mp4", "<html>", "text/html");
        var dl = new DirectDownloader(Env.Create(_http));
        Assert.Equal("The URL serves an HLS playlist.",
            (await Assert.ThrowsAsync<RedirectToHlsException>(() => dl.DownloadFileAsync(Opt("https://cdn.test/a.mp4"), new ProgressLog(), CancellationToken.None))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dl.DownloadFileAsync(Opt("https://cdn.test/b.mp4"), new ProgressLog(), CancellationToken.None));
    }

    [Fact]
    public async Task Failed_remux_removes_temp_file()
    {
        _http.Bytes("https://cdn.test/c.mkv", new byte[10], "video/x-matroska");
        var failing = new FakeProcesses((_, _) => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DirectDownloader(Env.Create(_http, failing)).DownloadFileAsync(Opt("https://cdn.test/c.mkv"), new ProgressLog(), CancellationToken.None));
        Assert.False(File.Exists(_dir.File("video.part")));
    }

    [Fact]
    public async Task Dash_is_handed_to_ffmpeg()
    {
        var ffmpeg = FakeProcesses.Ffmpeg();
        var progress = new ProgressLog();
        var path = await new DirectDownloader(Env.Create(_http, ffmpeg)).DownloadDashAsync(
            new DownloadOptions { Url = "https://cdn.test/movie/manifest.mpd", OutputFolder = _dir.Path, Referer = "https://p" }, progress, CancellationToken.None);

        Assert.Equal(_dir.File("movie.mp4"), path);
        var args = ffmpeg.Requests.Single().Arguments;
        Assert.Contains("https://cdn.test/movie/manifest.mpd", args);
        Assert.Contains("-referer", args);
        Assert.True(progress.Any("DASH"));
        Assert.Equal(new double[] { 50, 100 }, progress.Percents);
    }

    /// <summary>A stream that sleeps between reads so progress gets reported.</summary>
    private sealed class SlowStream : MemoryStream
    {
        public SlowStream(byte[] data) : base(data) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(220, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

public class PageScannerTests
{
    private static readonly Uri Page = new("https://site.test/watch/1");

    [Fact]
    public void Finds_streams_in_markup_scripts_and_metadata()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="A &amp; B">
            <meta property="og:video:url" content="https://cdn.test/og.mp4">
            </head><body>
            <video src="/local/clip.webm"></video>
            <source src='//cdn.test/proto.m3u8'>
            <script>var cfg = {"hls":"https:\/\/cdn.test\/v\/master.m3u8?t=1","dash":"https://cdn.test/m.mpd"};</script>
            <a href="https://cdn.test/page.html">x</a>
            <video src="blob:https://site.test/123"></video>
            <video src="data:video/mp4;base64,AAAA"></video>
            <source src="https://cdn.test/og.mp4">
            </body></html>
            """;
        var r = PageScanner.Parse(html, Page);

        Assert.Equal("A & B", r.Title);
        Assert.Equal(new[]
        {
            "https://cdn.test/v/master.m3u8?t=1",
            "https://cdn.test/proto.m3u8",
            "https://cdn.test/m.mpd",
            "https://cdn.test/og.mp4",
            "https://site.test/local/clip.webm",
        }, r.Streams.Select(s => s.Uri.ToString()));
    }

    [Fact]
    public void Uses_title_tag_and_handles_pages_without_streams()
    {
        var r = PageScanner.Parse("<title> Plain page </title><img src=\"x.png\"><video src=\"mailto:a@b.mp4\">", Page);
        Assert.Equal("Plain page", r.Title);
        Assert.Empty(r.Streams);
        Assert.Null(PageScanner.Parse("<p>nothing</p>", Page).Title);
    }

    [Fact]
    public async Task ScanAsync_fetches_page_with_browser_headers()
    {
        var http = new FakeHttp().Text(Page.ToString(), "<title>T</title><video src=\"v.mp4\">", "text/html");
        var r = await new PageScanner(http.Factory).ScanAsync(Page.ToString(), CancellationToken.None, "UA", "c=1");
        Assert.Equal("https://site.test/watch/v.mp4", r.Streams.Single().Uri.ToString());
        var req = http.Requests.Single();
        Assert.Equal("c=1", req.Headers.GetValues("Cookie").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ScanAsync_reports_blocked_pages(HttpStatusCode code)
    {
        var http = new FakeHttp().Route(Page.ToString(), _ =>
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent("") };
            r.Headers.Server.ParseAdd("cloudflare");
            return r;
        });
        var ex = await Assert.ThrowsAsync<PageBlockedException>(() => new PageScanner(http.Factory).ScanAsync(Page.ToString(), CancellationToken.None));
        Assert.Equal((int)code, ex.StatusCode);
        Assert.Contains("cloudflare", ex.Message);
    }

    [Fact]
    public async Task ScanAsync_throws_for_other_errors()
    {
        var http = new FakeHttp().Status(Page.ToString(), HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => new PageScanner(http.Factory).ScanAsync(Page.ToString(), CancellationToken.None));
    }
}
