using System.Net;
using Viddl.Core.Hls;
using Viddl.Core.Models;
using Viddl.Core.Streams;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class StreamsTests
{
    [Theory]
    [InlineData("https://a.com/v/index.m3u8", StreamKind.Hls)]
    [InlineData("https://a.com/v/index.M3U8?token=1", StreamKind.Hls)]
    [InlineData("https://a.com/play?format=m3u8", StreamKind.Hls)]
    [InlineData("https://a.com/manifest.mpd", StreamKind.Dash)]
    [InlineData("https://a.com/file.webm", StreamKind.Direct)]
    [InlineData("https://a.com/file.ts", StreamKind.Direct)]
    [InlineData("https://a.com/watch?v=1", StreamKind.WebPage)]
    [InlineData("relative/path.mp4", StreamKind.Direct)]
    public void ClassifyUrl(string url, StreamKind kind) => Assert.Equal(kind, StreamClassifier.ClassifyUrl(url));

    [Theory]
    [InlineData("https://a.com/x", "application/vnd.apple.mpegurl", StreamKind.Hls)]
    [InlineData("https://a.com/x", "application/dash+xml", StreamKind.Dash)]
    [InlineData("https://a.com/x.m3u8", null, StreamKind.Hls)]
    [InlineData("https://a.com/x.mpd", "", StreamKind.Dash)]
    [InlineData("https://a.com/stream", "video/mp4", StreamKind.Direct)]
    [InlineData("https://a.com/clip.webm", "application/octet-stream", StreamKind.Direct)]
    [InlineData("https://a.com/seg1.ts", "video/mp2t", null)]
    [InlineData("https://a.com/seg1.m4s", "video/mp4", null)]
    [InlineData("https://a.com/audio.m4a", "", null)]
    [InlineData("https://a.com/page.mp4", "text/html", null)]
    [InlineData("https://a.com/image.png", "image/png", null)]
    [InlineData("not a url", "", null)]
    public void ClassifyResponse(string url, string? contentType, StreamKind? kind) =>
        Assert.Equal(kind, StreamClassifier.ClassifyResponse(url, contentType));

    [Theory]
    [InlineData("bytes 0-1023/52428800", null, 206, 52428800L)]
    [InlineData(null, "1000", 200, 1000L)]
    [InlineData(null, "1000", 206, null)]
    [InlineData("bytes 0-1/*", "x", 200, null)]
    [InlineData(null, null, 200, null)]
    public void TotalSize(string? range, string? length, int status, long? expected) =>
        Assert.Equal(expected, StreamClassifier.TotalSize(range, length, status));

    [Fact]
    public void SniffedStream_names_and_labels()
    {
        var s = new SniffedStream { Uri = new Uri("https://cdn.a.com/v/clip.mp4?x=1"), Kind = StreamKind.Direct, PageUrl = "p" };
        Assert.Equal("https://cdn.a.com/v/clip.mp4", s.Key);
        Assert.Equal("clip.mp4", s.DisplayName);
        Assert.Equal("File", s.KindLabel);
        s.Title = "Page title";
        Assert.Equal("Page title", s.DisplayName);
        s.CustomName = "Mine";
        Assert.Equal("Mine", s.DisplayName);

        var root = new SniffedStream { Uri = new Uri("https://cdn.a.com/"), Kind = StreamKind.Hls, PageUrl = "p" };
        Assert.Equal("cdn.a.com", root.DisplayName);
        Assert.Equal("HLS", root.KindLabel);
        Assert.Equal("DASH", new SniffedStream { Uri = root.Uri, Kind = StreamKind.Dash, PageUrl = "p" }.KindLabel);
        Assert.Equal("checking…", root.Info);
    }

    [Fact]
    public void Catalog_adds_streams_and_ignores_duplicates()
    {
        var now = new DateTime(2026, 1, 1);
        var catalog = new StreamCatalog(() => now);
        var added = new List<SniffedStream>();
        catalog.Added += added.Add;

        var hls = catalog.OnResponse("https://cdn/a/master.m3u8?t=1", "application/x-mpegurl", 200, "https://page/1", "Title");
        var file = catalog.OnResponse("https://cdn/b/clip.mp4", "video/mp4", 206, "https://page/1", null, "bytes 0-1/2097152", "2");
        var dash = catalog.OnResponse("https://cdn/c/m.mpd", "", 200, "https://page/1", null);

        Assert.Null(catalog.OnResponse("https://cdn/a/master.m3u8?t=2", "application/x-mpegurl", 200, "https://page/1", null)); // same path
        Assert.Null(catalog.OnResponse("https://cdn/x.png", "image/png", 200, "p", null));
        Assert.Null(catalog.OnResponse("https://cdn/y.m3u8", "", 404, "p", null));
        Assert.Null(catalog.OnResponse("blob:https://cdn/1", "", 200, "p", null));

        // No resolution known yet: adaptive streams (newest first) before plain files.
        Assert.Equal(new[] { dash!, hls!, file! }, catalog.Items);
        Assert.Equal(3, added.Count);
        Assert.Equal(now, hls!.FoundAt);
        Assert.Equal("2.0 MB", file!.Info);
        Assert.Equal(2097152, file.SizeBytes);
        Assert.Equal("DASH", dash!.Info);

        var noSize = catalog.OnResponse("https://cdn/d.webm", "video/webm", 206, "p", null);
        Assert.Equal("video file", noSize!.Info);
    }

    [Fact]
    public void Catalog_folds_child_playlists_into_master()
    {
        var catalog = new StreamCatalog();
        var removed = new List<SniffedStream>();
        var changed = new List<SniffedStream>();
        catalog.Removed += removed.Add;
        catalog.Changed += changed.Add;

        var child = catalog.OnResponse("https://cdn/v720/index.m3u8", "", 200, "p", null)!;
        var master = catalog.OnResponse("https://cdn/master.m3u8", "", 200, "p", null)!;
        catalog.ApplyDescription(master, new HlsDescription("1080p · 720p", false,
            new[] { "https://cdn/v720/index.m3u8", "https://cdn/v1080/index.m3u8", master.Key }));

        Assert.Equal(new[] { master }, catalog.Items);
        Assert.Equal(new[] { child }, removed);
        Assert.Equal("1080p · 720p", master.Info);
        Assert.Contains(master, changed);
        Assert.Null(catalog.OnResponse("https://cdn/v1080/index.m3u8?x", "", 200, "p", null)); // child seen later

        var gone = catalog.OnResponse("https://cdn/other.m3u8", "", 200, "p", null)!;
        catalog.Remove(gone);
        changed.Clear();
        catalog.ApplyDescription(gone, new HlsDescription("LIVE", false, Array.Empty<string>())); // no longer listed
        Assert.Empty(changed);
    }

    [Fact]
    public void Catalog_updates_titles_renames_removes_and_clears()
    {
        var catalog = new StreamCatalog();
        int cleared = 0;
        catalog.Cleared += () => cleared++;
        var a = catalog.OnResponse("https://cdn/a.m3u8", "", 200, "https://page", null)!;
        var b = catalog.OnResponse("https://cdn/b.m3u8", "", 200, "https://other", null)!;
        var c = catalog.OnResponse("https://cdn/c.m3u8", "", 200, "https://page", null)!;
        catalog.Rename(c, "Custom");

        catalog.UpdateTitles("https://page", "Real title");
        catalog.UpdateTitles("https://page", " ");
        Assert.Equal("Real title", a.Title);
        Assert.Null(b.Title);
        Assert.Null(c.Title);
        Assert.Equal("Custom", c.DisplayName);

        catalog.Remove(a);
        catalog.Remove(a); // already gone
        Assert.Equal(2, catalog.Items.Count);
        Assert.NotNull(catalog.OnResponse("https://cdn/a.m3u8", "", 200, "https://page", null)); // detectable again

        catalog.Clear();
        Assert.Empty(catalog.Items);
        Assert.Equal(1, cleared);
        Assert.NotNull(catalog.OnResponse("https://cdn/b.m3u8", "", 200, "https://page", null));
    }

    private static HlsPlaylist P(string text) => HlsPlaylist.Parse(text, new Uri("https://cdn/x/master.m3u8"));

    [Fact]
    public void Describe_master_lists_qualities_and_children()
    {
        var d = HlsDescription.Describe(P("""
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",URI="aud.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=640x360
            low.m3u8?t=1
            #EXT-X-STREAM-INF:BANDWIDTH=2,RESOLUTION=1920x1080
            hi.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3,RESOLUTION=1920x1080
            hi2.m3u8
            """));
        Assert.Equal("1080p · 360p", d.Info);
        Assert.Equal(1080, d.MaxHeight);
        Assert.False(d.Unsupported);
        Assert.Contains("https://cdn/x/low.m3u8", d.ChildKeys);
        Assert.Contains("https://cdn/x/aud.m3u8", d.ChildKeys);

        Assert.Equal("2 qualities", HlsDescription.Describe(P("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\na.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2\nb.m3u8\n")).Info);
    }

    [Fact]
    public void Describe_media_playlists()
    {
        Assert.Equal("1:05", HlsDescription.Describe(P("#EXTM3U\n#EXTINF:65,\na.ts\n#EXT-X-ENDLIST\n")).Info);
        Assert.Equal("1:00:05", HlsDescription.Describe(P("#EXTM3U\n#EXTINF:3605,\na.ts\n#EXT-X-ENDLIST\n")).Info);
        Assert.Equal("LIVE", HlsDescription.Describe(P("#EXTM3U\n#EXTINF:5,\na.ts\n")).Info);
        var drm = HlsDescription.Describe(P("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n#EXTINF:5,\na.ts\n#EXT-X-ENDLIST\n"));
        Assert.True(drm.Unsupported);
        Assert.Contains("DRM", drm.Info);
    }

    [Fact]
    public async Task Probe_fetches_and_describes()
    {
        var http = new FakeHttp().Text("https://cdn/v.m3u8", "#EXTM3U\n#EXTINF:30,\na.ts\n#EXT-X-ENDLIST\n");
        var d = await HlsDescription.ProbeAsync(new Uri("https://cdn/v.m3u8"), "https://page", "ua", "c=1", http.Factory);
        Assert.Equal("0:30", d.Info);
        var req = http.Requests.Single();
        Assert.Equal("https://page/", req.Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task Probe_falls_back_on_errors()
    {
        var http = new FakeHttp()
            .Status("https://cdn/missing.m3u8", HttpStatusCode.NotFound)
            .Text("https://cdn/bad.m3u8", "<html>");
        Assert.Equal("HLS", (await HlsDescription.ProbeAsync(new Uri("https://cdn/missing.m3u8"), null, null, null, http.Factory)).Info);
        Assert.Equal("HLS", (await HlsDescription.ProbeAsync(new Uri("https://cdn/bad.m3u8"), null, null, null, http.Factory)).Info);
    }
}

public class StreamCatalogEventTests
{
    [Fact]
    public void Rename_notifies_listeners()
    {
        var catalog = new StreamCatalog();
        var s = catalog.OnResponse("https://cdn/a.m3u8", "", 200, "https://page", null)!;
        var changed = new List<SniffedStream>();
        catalog.Changed += changed.Add;
        catalog.Rename(s, "New name");
        Assert.Equal(new[] { s }, changed);
        Assert.Equal("New name", s.DisplayName);
    }
}

public class StreamQualityTests
{
    [Theory]
    [InlineData("https://cdn.test/hls/720p/index.m3u8", 720)]
    [InlineData("https://cdn.test/video_1080P.mp4", 1080)]
    [InlineData("https://cdn.test/v/1920x1080/clip.mp4", 1080)]
    [InlineData("https://cdn.test/v/640x360_480p/x.m3u8", 480)]
    [InlineData("https://cdn.test/movie-4k.mp4", 2160)]
    [InlineData("https://cdn.test/uhd/master.m3u8", 2160)]
    [InlineData("https://cdn.test/v/1080/index.m3u8", null)]       // a bare number isn't a resolution
    [InlineData("https://cdn.test/v/9999p/x.mp4", null)]            // implausible height
    [InlineData("https://cdn.test/abc720pxyz/x.mp4", null)]         // part of a longer word
    [InlineData("https://cdn.test/x.mp4?q=1080p", null)]            // only the path counts
    [InlineData("not a url 720p", 720)]
    public void HeightFromUrl(string url, int? expected) => Assert.Equal(expected, StreamClassifier.HeightFromUrl(url));

    [Fact]
    public void Streams_are_sorted_best_quality_first()
    {
        var catalog = new StreamCatalog();
        int reorders = 0;
        catalog.Reordered += () => reorders++;

        var small = catalog.OnResponse("https://cdn/ad.mp4", "video/mp4", 200, "p", null, null, "1000")!;
        var big = catalog.OnResponse("https://cdn/movie.mp4", "video/mp4", 200, "p", null, null, "900000")!;
        var hd = catalog.OnResponse("https://cdn/v/720p/index.m3u8", "", 200, "p", null)!;
        var master = catalog.OnResponse("https://cdn/master.m3u8", "", 200, "p", null)!;
        var drm = catalog.OnResponse("https://cdn/protected.m3u8", "", 200, "p", null)!;

        Assert.Equal(720, hd.Height);
        Assert.Equal("720p", hd.QualityLabel);
        Assert.Equal("", master.QualityLabel);
        Assert.Equal(new[] { hd, drm, master, big, small }, catalog.Items);

        catalog.ApplyDescription(master, new HlsDescription("2160p · 1080p", false, Array.Empty<string>(), 2160));
        catalog.ApplyDescription(drm, new HlsDescription("DRM protected - can't download", true, Array.Empty<string>()));

        Assert.Equal(2160, master.Height);
        Assert.Equal(new[] { master, hd, big, small, drm }, catalog.Items);
        Assert.Equal(2, reorders);

        // A lower probed height doesn't overwrite a better hint, and an unchanged order raises nothing.
        catalog.ApplyDescription(hd, new HlsDescription("1:00", false, Array.Empty<string>(), 360));
        Assert.Equal(720, hd.Height);
        Assert.Equal(2, reorders);
    }

    [Fact]
    public void Ties_are_broken_by_size_then_newest()
    {
        static SniffedStream S(StreamKind kind, int? height = null, long? size = null, bool unsupported = false, long seq = 0) =>
            new() { Uri = new Uri("https://x/" + seq), Kind = kind, PageUrl = "p", Height = height, SizeBytes = size, Unsupported = unsupported, Sequence = seq };

        Assert.True(StreamCatalog.CompareQuality(S(StreamKind.Direct, 1080), S(StreamKind.Hls, 720)) < 0);
        Assert.True(StreamCatalog.CompareQuality(S(StreamKind.Hls, 720, unsupported: true), S(StreamKind.Direct)) > 0);
        Assert.True(StreamCatalog.CompareQuality(S(StreamKind.Dash), S(StreamKind.Direct, size: 5)) < 0);
        Assert.True(StreamCatalog.CompareQuality(S(StreamKind.Direct, size: 9), S(StreamKind.Direct, size: 5)) < 0);
        Assert.True(StreamCatalog.CompareQuality(S(StreamKind.Hls, seq: 2), S(StreamKind.Hls, seq: 1)) < 0);
        Assert.Equal(0, StreamCatalog.CompareQuality(S(StreamKind.Hls, seq: 1), S(StreamKind.Hls, seq: 1)));
    }
}

public class StreamFilterTests
{
    private static SniffedStream S(int? height) => new() { Uri = new Uri("https://x/" + height), Kind = StreamKind.Hls, PageUrl = "p", Height = height };

    [Theory]
    [InlineData(1080, 720, true, true)]
    [InlineData(720, 720, true, true)]
    [InlineData(480, 720, true, false)]
    [InlineData(null, 720, true, true)]
    [InlineData(null, 720, false, false)]
    [InlineData(null, 0, false, true)]   // "All" shows everything, even unknown
    [InlineData(240, 0, false, true)]
    public void PassesFilter(int? height, int minHeight, bool includeUnknown, bool expected) =>
        Assert.Equal(expected, StreamCatalog.PassesFilter(S(height), minHeight, includeUnknown));

    [Fact]
    public void Filtered_keeps_quality_order()
    {
        var catalog = new StreamCatalog();
        var low = catalog.OnResponse("https://cdn/360p/a.m3u8", "", 200, "p", null)!;
        var hd = catalog.OnResponse("https://cdn/1080p/b.m3u8", "", 200, "p", null)!;
        var unknown = catalog.OnResponse("https://cdn/c.m3u8", "", 200, "p", null)!;

        Assert.Equal(new[] { hd, unknown }, catalog.Filtered(720, true));
        Assert.Equal(new[] { hd }, catalog.Filtered(720, false));
        Assert.Equal(new[] { hd, low, unknown }, catalog.Filtered(0, false));
    }

    [Fact]
    public void Filter_settings_default_to_showing_everything()
    {
        var s = new Viddl.Core.Settings.AppSettings();
        Assert.Equal(0, s.MinStreamHeight);
        Assert.True(s.ShowUnknownQuality);
        s.MinStreamHeight = 720;
        s.ShowUnknownQuality = false;
        Assert.Equal(720, s.MinStreamHeight);
        Assert.False(s.ShowUnknownQuality);
    }
}
