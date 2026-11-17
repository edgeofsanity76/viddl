using Viddl.Core.Hls;

namespace Viddl.Core.Tests;

public class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://cdn.example.com/show/master.m3u8");

    private const string Master = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/en.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="French",LANGUAGE="fr",URI="audio/fr.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",URI="subs/en.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="aud"
        v360/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="aud"
        v1080/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
        v720/index.m3u8
        """;

    [Fact]
    public void Parses_master_playlist()
    {
        var pl = HlsPlaylist.Parse(Master, Base);

        Assert.True(pl.IsMaster);
        Assert.Equal(3, pl.Variants.Count);
        Assert.Equal(2, pl.AudioRenditions.Count);
        var v = pl.Variants[1];
        Assert.Equal(new Uri("https://cdn.example.com/show/v1080/index.m3u8"), v.Uri);
        Assert.Equal(1920, v.Width);
        Assert.Equal(1080, v.Height);
        Assert.Equal(5000000, v.Bandwidth);
        Assert.Equal("aud", v.AudioGroup);
        Assert.Equal("1080p (5000 kbps)", v.ToString());
        var en = pl.AudioRenditions[0];
        Assert.True(en.IsDefault);
        Assert.Equal("English", en.Name);
        Assert.Equal("en", en.Language);
        Assert.Equal(new Uri("https://cdn.example.com/show/audio/en.m3u8"), en.Uri);
    }

    [Fact]
    public void Variant_without_resolution_shows_bandwidth()
    {
        var pl = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=abc,RESOLUTION=bad\nlow.m3u8\n", Base);
        Assert.Equal(0, pl.Variants[0].Height);
        Assert.Equal(0, pl.Variants[0].Bandwidth);
        Assert.Equal("0 kbps", pl.Variants[0].ToString());
    }

    [Fact]
    public void Parses_media_playlist_with_keys_maps_and_ranges()
    {
        const string media = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:10
            #EXT-X-PLAYLIST-TYPE:vod
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x0000000000000000000000000000ABCD
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:6.0,
            #EXT-X-BYTERANGE:1000@720
            seg.m4s
            #EXTINF:4.5,first
            #EXT-X-BYTERANGE:500
            seg.m4s
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:2,
            plain.m4s
            #EXT-X-ENDLIST
            """;
        var pl = HlsPlaylist.Parse(media, new Uri("https://cdn.example.com/show/v720/index.m3u8"));

        Assert.False(pl.IsMaster);
        Assert.Equal(3, pl.Segments.Count);
        Assert.Equal(12.5, pl.TotalDuration);
        Assert.Equal(6, pl.TargetDuration);
        Assert.Equal("VOD", pl.PlaylistType);
        Assert.True(pl.IsEndList);
        Assert.True(pl.IsVod);

        var s0 = pl.Segments[0];
        Assert.Equal(10, s0.Sequence);
        Assert.Equal(1000, s0.ByteRange!.Length);
        Assert.Equal(720, s0.ByteRange.Offset);
        Assert.Equal("AES-128", s0.Key!.Method);
        Assert.True(s0.Key.IsStandardAes);
        Assert.Equal(new Uri("https://cdn.example.com/show/v720/key.bin"), s0.Key.Uri);
        Assert.Equal(0xAB, s0.Key.Iv![14]);
        Assert.Equal(0xCD, s0.Key.Iv[15]);
        Assert.Equal(new Uri("https://cdn.example.com/show/v720/init.mp4"), s0.InitSection!.Uri);
        Assert.Equal(720, s0.InitSection.ByteRange!.Length);

        // A byte range without an offset continues where the previous one ended.
        Assert.Equal(1720, pl.Segments[1].ByteRange!.Offset);
        Assert.Equal(11, pl.Segments[1].Sequence);
        Assert.Null(pl.Segments[2].Key);
        Assert.Null(pl.FindDrmKey());
    }

    [Fact]
    public void Live_playlist_is_not_vod()
    {
        var pl = HlsPlaylist.Parse("#EXTM3U\n#EXTINF:2,\na.ts\n", Base);
        Assert.False(pl.IsEndList);
        Assert.False(pl.IsVod);
        Assert.Null(pl.PlaylistType);
    }

    [Fact]
    public void Short_hex_iv_is_left_padded()
    {
        var pl = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",IV=1F\n#EXTINF:1,\na.ts\n", Base);
        Assert.Equal(16, pl.Segments[0].Key!.Iv!.Length);
        Assert.Equal(0x1F, pl.Segments[0].Key!.Iv![15]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not a playlist</html>")]
    public void Rejects_non_playlists(string text) =>
        Assert.Throws<InvalidDataException>(() => HlsPlaylist.Parse(text, Base));

    [Fact]
    public void Detects_drm_keys()
    {
        var sample = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"\n#EXTINF:1,\na.ts\n", Base);
        Assert.Equal("SAMPLE-AES", sample.FindDrmKey()!.Method);

        var widevine = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",KEYFORMAT=\"com.widevine\"\n#EXTINF:1,\na.ts\n", Base);
        Assert.False(widevine.Segments[0].Key!.IsStandardAes);
        Assert.NotNull(widevine.FindDrmKey());

        var identity = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",KEYFORMAT=\"identity\"\n#EXTINF:1,\na.ts\n", Base);
        Assert.Null(identity.FindDrmKey());
    }

    [Fact]
    public void ChooseVariant_picks_by_height()
    {
        var variants = HlsPlaylist.Parse(Master, Base).Variants;

        Assert.Equal(1080, HlsPlaylist.ChooseVariant(variants, null).Height);
        Assert.Equal(720, HlsPlaylist.ChooseVariant(variants, 720).Height);
        Assert.Equal(720, HlsPlaylist.ChooseVariant(variants, 1000).Height);
        Assert.Equal(360, HlsPlaylist.ChooseVariant(variants, 0).Height);
        Assert.Equal(360, HlsPlaylist.ChooseVariant(variants, 240).Height); // nothing fits -> lowest
        Assert.Throws<ArgumentException>(() => HlsPlaylist.ChooseVariant(Array.Empty<HlsVariant>(), null));
    }

    [Fact]
    public void AudioFor_prefers_default_rendition()
    {
        var pl = HlsPlaylist.Parse(Master, Base);
        Assert.EndsWith("audio/en.m3u8", pl.AudioFor(pl.Variants[0])!.ToString());
        Assert.Null(pl.AudioFor(pl.Variants[2])); // no audio group

        var noDefault = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\",URI=\"x.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"a\"\nv.m3u8\n", Base);
        Assert.EndsWith("x.m3u8", noDefault.AudioFor(noDefault.Variants[0])!.ToString());

        var noUri = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"a\"\n#EXT-X-STREAM-INF:BANDWIDTH=1,AUDIO=\"a\"\nv.m3u8\n", Base);
        Assert.Null(noUri.AudioFor(noUri.Variants[0]));
    }

    [Fact]
    public void ParseAttributes_handles_quotes_and_commas()
    {
        var a = HlsPlaylist.ParseAttributes("BANDWIDTH=1,CODECS=\"avc1,mp4a\",NAME=\"x\"");
        Assert.Equal("1", a["BANDWIDTH"]);
        Assert.Equal("avc1,mp4a", a["CODECS"]);
        Assert.Equal("x", a["name"]);
    }
}
