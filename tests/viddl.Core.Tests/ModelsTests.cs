using Viddl.Core.Models;

namespace Viddl.Core.Tests;

public class ModelsTests
{
    [Fact]
    public void ScaledProgress_maps_percent_into_range()
    {
        DownloadProgress? got = null;
        var scaled = new ScaledProgress(new InlineProgress<DownloadProgress>(p => got = p), 90, 100);

        scaled.Report(new DownloadProgress(50, "half"));
        Assert.Equal(95, got!.Percent);
        Assert.Equal("half", got.Status);

        scaled.Report(new DownloadProgress(150, "over"));
        Assert.Equal(100, got.Percent);

        scaled.Report(new DownloadProgress(-5, "under"));
        Assert.Equal(90, got.Percent);

        scaled.Report(new DownloadProgress(null, "unknown"));
        Assert.Null(got.Percent);
    }

    [Fact]
    public void InlineProgress_invokes_callback_immediately()
    {
        int value = 0;
        new InlineProgress<int>(v => value = v).Report(7);
        Assert.Equal(7, value);
    }

    [Fact]
    public void DownloadOptions_WithUrl_copies_everything_but_url_name_and_referer()
    {
        var o = new DownloadOptions
        {
            Url = "https://a/page", OutputFolder = "C:\\out", FileName = "x", Referer = "r", UserAgent = "ua",
            Cookies = "c=1", MaxHeight = 720, Concurrency = 3, CookiesFromBrowser = "firefox", ExpectedBytes = 42,
        };

        var copy = o.WithUrl("https://a/v.m3u8", "title", "https://a/page");

        Assert.Equal("https://a/v.m3u8", copy.Url);
        Assert.Equal("title", copy.FileName);
        Assert.Equal("https://a/page", copy.Referer);
        Assert.Equal("C:\\out", copy.OutputFolder);
        Assert.Equal("ua", copy.UserAgent);
        Assert.Equal("c=1", copy.Cookies);
        Assert.Equal(720, copy.MaxHeight);
        Assert.Equal(3, copy.Concurrency);
        Assert.Equal("firefox", copy.CookiesFromBrowser);
        Assert.Equal(42, copy.ExpectedBytes);
        Assert.Equal(4, new DownloadOptions { Url = "u", OutputFolder = "o" }.Concurrency);
    }

    [Theory]
    [InlineData("https://cdn.example.com/hls/abc/720p/index.m3u8?t=1", StreamKind.Hls, "[HLS]  cdn.example.com  …/720p/index.m3u8")]
    [InlineData("https://example.com/video.mp4", StreamKind.Direct, "[DIRECT]  example.com  /video.mp4")]
    [InlineData("https://example.com/a/b.mpd", StreamKind.Dash, "[DASH]  example.com  /a/b.mpd")]
    public void DetectedStream_has_short_label(string url, StreamKind kind, string expected) =>
        Assert.Equal(expected, new DetectedStream(new Uri(url), kind).ToString());

    [Fact]
    public void ScanResult_holds_title_and_streams()
    {
        var r = new ScanResult("t", new[] { new DetectedStream(new Uri("https://a/b.mp4"), StreamKind.Direct) });
        Assert.Equal("t", r.Title);
        Assert.Single(r.Streams);
    }

    [Fact]
    public void PageBlockedException_includes_status_and_server()
    {
        var withServer = new PageBlockedException(403, "cloudflare");
        Assert.Equal(403, withServer.StatusCode);
        Assert.Contains("HTTP 403, cloudflare", withServer.Message);

        var without = new PageBlockedException(429, " ");
        Assert.Contains("HTTP 429)", without.Message);
    }
}
