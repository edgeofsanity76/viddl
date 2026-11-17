using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Viddl.Core.Models;
using Viddl.Downloads.Engines;
using Viddl.Downloads.Processes;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class HlsDownloaderTests : IDisposable
{
    private const string Cdn = "https://cdn.test/show/";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();

    public void Dispose() => _dir.Dispose();

    private DownloadOptions Opt(string url = Cdn + "media.m3u8", string? name = "clip", int? maxHeight = null, int concurrency = 2) =>
        new() { Url = url, OutputFolder = _dir.Path, FileName = name, MaxHeight = maxHeight, Concurrency = concurrency, Referer = "https://page.test/", Cookies = "s=1" };

    private static byte[] Seg(int i) => Encoding.ASCII.GetBytes($"[segment {i}]");

    private static string Media(int count, string extra = "", bool end = true, string prefix = "seg") =>
        "#EXTM3U\n#EXT-X-TARGETDURATION:4\n" + extra +
        string.Concat(Enumerable.Range(0, count).Select(i => $"#EXTINF:4.0,\n{prefix}{i}.ts\n")) + (end ? "#EXT-X-ENDLIST\n" : "");

    private void ServeSegments(int count, string prefix = "seg")
    {
        for (int i = 0; i < count; i++) _http.Bytes($"{Cdn}{prefix}{i}.ts", Seg(i), "video/mp2t");
    }

    private async Task<(string Path, ProgressLog Progress, FakeProcesses Ffmpeg)> Run(DownloadOptions opt, FakeProcesses? ffmpeg = null, List<TimeSpan>? delays = null)
    {
        ffmpeg ??= FakeProcesses.Ffmpeg();
        var progress = new ProgressLog();
        var env = Env.Create(_http, ffmpeg, tempRoot: _dir.File("tmp"), delays: delays);
        var path = await new HlsDownloader(opt, progress, env).DownloadAsync(CancellationToken.None);
        return (path, progress, ffmpeg);
    }

    [Fact]
    public async Task Downloads_joins_and_converts_a_media_playlist()
    {
        _http.Text(Cdn + "media.m3u8", Media(5));
        ServeSegments(5);

        var (path, progress, ffmpeg) = await Run(Opt());

        Assert.Equal(_dir.File("clip.mp4"), path);
        Assert.Equal(string.Concat(Enumerable.Range(0, 5).Select(i => $"[segment {i}]")), File.ReadAllText(path));
        var args = ffmpeg.Requests.Single().Arguments;
        Assert.Contains("+faststart", args);
        Assert.Contains("0:a?", args);
        Assert.True(progress.Any("Downloading segments 5/5"));
        Assert.True(progress.Any("Converting to MP4"));
        Assert.True(progress.Percents.Max() >= HlsDownloader.DownloadShare); // converting phase reached
        Assert.False(Directory.EnumerateFileSystemEntries(_dir.File("tmp")).Any()); // temp folder cleaned up

        var seg = _http.Requests.First(r => r.RequestUri!.ToString().EndsWith("seg0.ts"));
        Assert.Equal("https://page.test/", seg.Headers.Referrer!.ToString());
        Assert.Equal("s=1", seg.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task Progress_moves_while_a_large_segment_downloads()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        var big = new byte[64 * 1024 * 8];
        _http.Route(Cdn + "seg0.ts", _ =>
        {
            var content = new StreamContent(new TricklingStream(big));
            content.Headers.ContentLength = big.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var (_, progress, _) = await Run(Opt());

        Assert.Contains(progress.Percents, p => p is > 0 and < HlsDownloader.DownloadShare);
    }

    /// <summary>Delivers data in 64 KB pieces with a pause between them, like a slow connection.</summary>
    private sealed class TricklingStream : MemoryStream
    {
        public TricklingStream(byte[] data) : base(data) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(30, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], cancellationToken);
        }
    }

    [Fact]
    public async Task Master_playlist_chooses_quality_and_merges_separate_audio()
    {
        _http.Text(Cdn + "master.m3u8", """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",DEFAULT=YES,URI="audio.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="aud"
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="aud"
            high.m3u8
            """);
        _http.Text(Cdn + "low.m3u8", Media(2));
        _http.Text(Cdn + "audio.m3u8", Media(2, prefix: "a"));
        ServeSegments(2);
        ServeSegments(2, "a");

        var (_, progress, ffmpeg) = await Run(Opt(Cdn + "master.m3u8", maxHeight: 480));

        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.ToString().Contains("high.m3u8"));
        Assert.True(progress.Any("Selected quality: 360p"));
        Assert.True(progress.Any("Downloading segments 4/4"));
        var args = ffmpeg.Requests.Single().Arguments;
        Assert.Equal(2, args.Count(a => a == "-i"));
        Assert.Contains("1:a:0", args);
    }

    [Fact]
    public async Task Decrypts_aes_128_with_explicit_and_sequence_ivs()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var explicitIv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        _http.Bytes(Cdn + "key.bin", key);
        _http.Text(Cdn + "media.m3u8",
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n" +
            $"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x{Convert.ToHexString(explicitIv)}\n#EXTINF:4,\nseg0.ts\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n");
        _http.Bytes(Cdn + "seg0.ts", aes.EncryptCbc(Seg(0), explicitIv, PaddingMode.PKCS7));
        _http.Bytes(Cdn + "seg1.ts", aes.EncryptCbc(Seg(1), HlsDownloader.SequenceIv(8), PaddingMode.PKCS7));

        var (path, _, _) = await Run(Opt(concurrency: 1));

        Assert.Equal("[segment 0][segment 1]", File.ReadAllText(path));
        Assert.Single(_http.Requests, r => r.RequestUri!.ToString().EndsWith("key.bin")); // key fetched once
    }

    [Fact]
    public void SequenceIv_is_big_endian()
    {
        var iv = HlsDownloader.SequenceIv(0x0102);
        Assert.Equal(16, iv.Length);
        Assert.Equal(0x01, iv[14]);
        Assert.Equal(0x02, iv[15]);
        Assert.All(iv[..14], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Rejects_wrong_key_length()
    {
        _http.Bytes(Cdn + "key.bin", new byte[8]);
        _http.Text(Cdn + "media.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\nseg0.ts\n#EXT-X-ENDLIST\n");
        ServeSegments(1);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Run(Opt()));
        Assert.Contains("key length", ex.Message);
    }

    [Fact]
    public async Task Fmp4_streams_get_their_init_section_and_byte_ranges()
    {
        var file = Encoding.ASCII.GetBytes("INIT" + "AAAAABBBBB");
        _http.Route(Cdn + "file.mp4", req =>
        {
            var r = req.Headers.Range!.Ranges.Single();
            return FakeHttp.Response(HttpStatusCode.PartialContent, file[(int)r.From!.Value..((int)r.To!.Value + 1)]);
        });
        _http.Text(Cdn + "media.m3u8", """
            #EXTM3U
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXT-X-MAP:URI="file.mp4",BYTERANGE="4@0"
            #EXTINF:4,
            #EXT-X-BYTERANGE:5@4
            file.mp4
            #EXTINF:4,
            #EXT-X-BYTERANGE:5
            file.mp4
            """);

        var (path, _, ffmpeg) = await Run(Opt());

        Assert.Equal("INITAAAAABBBBB", File.ReadAllText(path));
        Assert.EndsWith("video.mp4", ffmpeg.Requests.Single().Arguments[3]); // joined fMP4 input
    }

    [Fact]
    public async Task Playlist_without_end_marker_that_does_not_change_is_downloaded_normally()
    {
        _http.Text(Cdn + "media.m3u8", Media(2, end: false));
        ServeSegments(2);
        var delays = new List<TimeSpan>();

        var (path, progress, _) = await Run(Opt(), delays: delays);

        Assert.True(progress.Any("checking whether it's live"));
        Assert.Contains(TimeSpan.FromSeconds(4), delays);
        Assert.Equal("[segment 0][segment 1]", File.ReadAllText(path));
    }

    [Fact]
    public async Task Playlist_that_ends_on_recheck_is_downloaded_normally()
    {
        _http.Sequence(Cdn + "media.m3u8",
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(1, end: false))),
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(2))));
        ServeSegments(2);
        var (path, _, _) = await Run(Opt());
        Assert.Equal("[segment 0][segment 1]", File.ReadAllText(path));
    }

    [Fact]
    public async Task Growing_playlist_is_recorded_live_with_ffmpeg()
    {
        _http.Text(Cdn + "master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"aud.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"a\"\nlive.m3u8\n");
        _http.Sequence(Cdn + "live.m3u8",
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(2, end: false))),
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(3, end: false))));

        var (path, progress, ffmpeg) = await Run(Opt(Cdn + "master.m3u8"));

        var req = ffmpeg.Requests.Single();
        Assert.True(req.GracefulStop);
        Assert.Contains(Cdn + "live.m3u8", req.Arguments);
        Assert.Contains(Cdn + "aud.m3u8", req.Arguments);
        Assert.Contains("+frag_keyframe+empty_moov", req.Arguments);
        Assert.Equal(2, req.Arguments.Count(a => a == "-referer"));
        Assert.True(progress.Any("Live stream detected"));
        Assert.Equal(_dir.File("clip.mp4"), path);
    }

    [Fact]
    public async Task Live_stream_without_audio_maps_input_streams()
    {
        _http.Sequence(Cdn + "media.m3u8",
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(1, end: false))),
            _ => FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(1, end: false, prefix: "next"))));
        var (_, _, ffmpeg) = await Run(Opt());
        Assert.Contains("0:a?", ffmpeg.Requests.Single().Arguments);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n", "SAMPLE-AES")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"k\",KEYFORMAT=\"com.microsoft.playready\"\n", "playready")]
    public async Task Refuses_drm_streams(string keyLine, string expected)
    {
        _http.Text(Cdn + "media.m3u8", Media(1, keyLine));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => Run(Opt()));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Refuses_drm_in_audio_track()
    {
        _http.Text(Cdn + "master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"aud.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"a\"\nv.m3u8\n");
        _http.Text(Cdn + "v.m3u8", Media(1));
        _http.Text(Cdn + "aud.m3u8", Media(1, "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(Opt(Cdn + "master.m3u8")));
    }

    [Fact]
    public async Task Retries_failed_segments_with_backoff()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        _http.Sequence(Cdn + "seg0.ts",
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new ByteArrayContent(Array.Empty<byte>()) },
            _ => FakeHttp.Response(HttpStatusCode.OK, Seg(0)));
        var delays = new List<TimeSpan>();

        var (path, _, _) = await Run(Opt(), delays: delays);

        Assert.Equal("[segment 0]", File.ReadAllText(path));
        Assert.Contains(delays, d => d >= TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task Pauses_all_workers_when_the_server_rate_limits()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        _http.Sequence(Cdn + "seg0.ts",
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return r;
            },
            _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                r.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(5));
                return r;
            },
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent(Array.Empty<byte>()) },
            _ => FakeHttp.Response(HttpStatusCode.OK, Seg(0)));
        var delays = new List<TimeSpan>();

        var (_, progress, _) = await Run(Opt(), delays: delays);

        Assert.True(progress.Any("Server is limiting requests (HTTP 429) - pausing 30s"));
        Assert.True(progress.Any("HTTP 503"));
        Assert.True(progress.Any("HTTP 403"));
        Assert.Contains(delays, d => d > TimeSpan.FromSeconds(20)); // waited for the Retry-After pause
    }

    [Fact]
    public async Task Gives_up_after_repeated_failures_and_cleans_up()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        _http.Status(Cdn + "seg0.ts", HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(Opt()));
        Assert.False(File.Exists(_dir.File("clip.mp4")));
        Assert.False(Directory.EnumerateFileSystemEntries(_dir.File("tmp")).Any());
    }

    [Fact]
    public async Task Failed_conversion_removes_partial_output()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        ServeSegments(1);
        var ffmpeg = new FakeProcesses((r, _) => { File.WriteAllText(r.Arguments[^1], "partial"); return Task.FromResult(1); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Opt(), ffmpeg));
        Assert.False(File.Exists(_dir.File("clip.mp4")));
    }

    [Fact]
    public async Task Requires_ffmpeg_before_downloading()
    {
        var env = Env.Create(_http, tools: new FakeTools { Ffmpeg = null });
        await Assert.ThrowsAsync<ToolMissingException>(() => new HlsDownloader(Opt(), new ProgressLog(), env).DownloadAsync(CancellationToken.None));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Names_file_from_url_and_avoids_overwriting()
    {
        _http.Text("https://cdn.test/shows/great-episode/index.m3u8", "#EXTM3U\n#EXTINF:1,\ns.ts\n#EXT-X-ENDLIST\n");
        _http.Bytes("https://cdn.test/shows/great-episode/s.ts", Seg(0));
        File.WriteAllText(_dir.File("great-episode.mp4"), "existing");

        var (path, _, _) = await Run(Opt("https://cdn.test/shows/great-episode/index.m3u8", name: null));

        Assert.Equal(_dir.File("great-episode (1).mp4"), path);
    }

    [Fact]
    public async Task Resolves_relative_paths_after_redirects()
    {
        _http.Route(Cdn + "redirect.m3u8", req =>
        {
            var r = FakeHttp.Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Media(1)));
            r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.test/real/media.m3u8");
            return r;
        });
        _http.Bytes("https://other.test/real/seg0.ts", Seg(0));
        var (path, _, _) = await Run(Opt(Cdn + "redirect.m3u8"));
        Assert.Equal("[segment 0]", File.ReadAllText(path));
    }

    [Fact]
    public async Task Cancellation_stops_the_download()
    {
        _http.Text(Cdn + "media.m3u8", Media(3));
        ServeSegments(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var env = Env.Create(_http, tempRoot: _dir.File("tmp"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HlsDownloader(Opt(), new ProgressLog(), env).DownloadAsync(cts.Token));
    }

    [Fact]
    public async Task Segment_that_fails_mid_stream_is_downloaded_again()
    {
        _http.Text(Cdn + "media.m3u8", Media(1));
        int attempts = 0;
        _http.Route(Cdn + "seg0.ts", _ =>
        {
            attempts++;
            var content = attempts == 1 ? (HttpContent)new StreamContent(new FailingStream(Seg(0))) : new ByteArrayContent(Seg(0));
            content.Headers.ContentLength = Seg(0).Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var (path, _, _) = await Run(Opt());
        Assert.Equal(2, attempts);
        Assert.Equal("[segment 0]", File.ReadAllText(path));
    }

    /// <summary>Returns a few bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream : MemoryStream
    {
        private bool _sent;

        public FailingStream(byte[] data) : base(data) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent) throw new IOException("connection reset");
            _sent = true;
            return base.ReadAsync(buffer[..3], cancellationToken);
        }
    }
}
