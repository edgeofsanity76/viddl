using Viddl.Core.Text;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData("My: Video? <1>", "My_ Video_ _1_")]
    [InlineData("  name ends with dots...  ", "name ends with dots")]
    [InlineData("", "video")]
    [InlineData("   ", "video")]
    [InlineData("a/b\\c|d*e\"f", "a_b_c_d_e_f")]
    [InlineData("tab\there", "tab_here")]
    public void SafeFileName_replaces_invalid_characters(string input, string expected) =>
        Assert.Equal(expected, Formatting.SafeFileName(input));

    [Fact]
    public void SafeFileName_limits_length()
    {
        var name = Formatting.SafeFileName(new string('x', 300));
        Assert.Equal(150, name.Length);
    }

    [Fact]
    public void UniquePath_appends_counter_when_file_exists()
    {
        using var dir = new TempDir();
        var target = dir.File("v.mp4");
        Assert.Equal(target, Formatting.UniquePath(target));

        File.WriteAllText(target, "");
        Assert.Equal(dir.File("v (1).mp4"), Formatting.UniquePath(target));

        File.WriteAllText(dir.File("v (1).mp4"), "");
        Assert.Equal(dir.File("v (2).mp4"), Formatting.UniquePath(target));
    }

    [Fact]
    public void SpeedEta_formats_speed_and_time_left()
    {
        Assert.Equal("", Formatting.SpeedEta(1000, TimeSpan.FromMilliseconds(500), 0.5));
        Assert.Equal("", Formatting.SpeedEta(0, TimeSpan.FromSeconds(5), 0.5));
        Assert.Equal(" · 2.5 MB/s · ETA 01:00", Formatting.SpeedEta(50L * 1048576, TimeSpan.FromSeconds(20), 0.25));
        Assert.Equal(" · 0.5 MB/s · ETA 2:30:00", Formatting.SpeedEta(500L * 1048576, TimeSpan.FromSeconds(1000), 0.1));
        Assert.Equal(" · 1.0 MB/s", Formatting.SpeedEta(10L * 1048576, TimeSpan.FromSeconds(10), 1));
        Assert.Equal(" · 1.0 MB/s", Formatting.SpeedEta(10L * 1048576, TimeSpan.FromSeconds(10), 0.001));
    }

    [Fact]
    public void Duration_and_FileSize_format()
    {
        Assert.Equal("01:05", Formatting.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal("1:00:05", Formatting.Duration(TimeSpan.FromSeconds(3605)));
        Assert.Equal("1.5 MB", Formatting.FileSize(1572864));
        Assert.Equal("2.00 GB", Formatting.FileSize(2L << 30));
    }

    [Fact]
    public void Shorten_truncates_with_ellipsis()
    {
        Assert.Equal("short", Formatting.Shorten("short", 10));
        Assert.Equal("abcd…", Formatting.Shorten("abcdefgh", 5));
    }

    [Theory]
    [InlineData("https://cdn.example.com/shows/my-episode/720p/index.m3u8", "my-episode")]
    [InlineData("https://cdn.example.com/movie_trailer.mp4", "movie_trailer")]
    [InlineData("https://cdn.example.com/a%20b%20c/master.m3u8", "a b c")]
    public void GuessName_skips_generic_segments(string url, string expected) =>
        Assert.Equal(expected, Formatting.GuessName(new Uri(url), DateTime.Now));

    [Fact]
    public void GuessName_falls_back_to_timestamp()
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal("video_20260102_030405", Formatting.GuessName(new Uri("https://x.com/1080/index.m3u8"), now));
        Assert.Equal("video_20260102_030405", Formatting.GuessName(new Uri("https://x.com/"), now));
    }

    [Fact]
    public void ShortName_uses_host_and_last_segment()
    {
        Assert.Equal("example.com / file name.mp4", Formatting.ShortName("https://example.com/v/file%20name.mp4"));
        Assert.Equal("example.com", Formatting.ShortName("https://example.com/"));
    }
}
