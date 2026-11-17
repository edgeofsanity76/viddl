using Viddl.Core.Models;
using Viddl.Downloads.Engines;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class ConverterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Presets_are_unique_and_none_comes_first()
    {
        Assert.True(ConversionPreset.All[0].IsNone);
        Assert.Equal(ConversionPreset.All.Count, ConversionPreset.All.Select(p => p.Id).Distinct().Count());
        Assert.All(ConversionPreset.All, p => Assert.StartsWith(".", p.Extension));
        Assert.Equal("mp3", ConversionPreset.Find("mp3").Id);
        Assert.True(ConversionPreset.Find(null).IsNone);
        Assert.True(ConversionPreset.Find("unknown").IsNone);
        Assert.Contains("-vn", ConversionPreset.Find("m4a").Arguments);
        Assert.Contains("libvpx-vp9", ConversionPreset.Find("webm").Arguments);
        Assert.Contains("scale=-2:'min(720,ih)'", ConversionPreset.Find("mp4-720p").Arguments);
    }

    [Fact]
    public void Output_names_change_extension_or_add_a_suffix_and_never_overwrite()
    {
        var input = _dir.Write("clip.mp4", "x");
        Assert.Equal(_dir.File("clip.mp3"), ConversionPreset.Find("mp3").OutputPathFor(input));
        Assert.Equal(_dir.File("clip (720p).mp4"), ConversionPreset.Find("mp4-720p").OutputPathFor(input));
        _dir.Write("clip.mkv", "existing");
        Assert.Equal(_dir.File("clip (1).mkv"), ConversionPreset.Find("mkv").OutputPathFor(input));
    }

    [Fact]
    public async Task Converts_with_ffmpeg_and_keeps_the_original_by_default()
    {
        var input = _dir.Write("clip.mp4", "video");
        var ffmpeg = FakeProcesses.Ffmpeg();
        var progress = new ProgressLog();

        var output = await new Converter(Env.Create(new FakeHttp(), ffmpeg)).ConvertAsync(input, ConversionPreset.Find("mp3"), true, progress, CancellationToken.None);

        Assert.Equal(_dir.File("clip.mp3"), output);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(input));
        var args = ffmpeg.Requests.Single().Arguments;
        Assert.Equal(input, args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains("libmp3lame", args);
        Assert.Equal(output, args[^1]);
        Assert.True(progress.Any("Converting to MP3"));
        Assert.Contains(100.0, progress.Percents);
    }

    [Fact]
    public async Task Can_remove_the_original_after_converting()
    {
        var input = _dir.Write("clip.mp4", "video");
        var output = await new Converter(Env.Create(new FakeHttp())).ConvertAsync(input, ConversionPreset.Find("mkv"), false, new ProgressLog(), CancellationToken.None);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(input));
    }

    [Fact]
    public async Task Original_in_use_is_kept_rather_than_failing()
    {
        var input = _dir.Write("clip.mp4", "video");
        using var locked = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        var output = await new Converter(Env.Create(new FakeHttp())).ConvertAsync(input, ConversionPreset.Find("mkv"), false, new ProgressLog(), CancellationToken.None);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task No_conversion_returns_the_input_untouched()
    {
        var input = _dir.Write("clip.mp4", "video");
        var ffmpeg = FakeProcesses.Ffmpeg();
        Assert.Equal(input, await new Converter(Env.Create(new FakeHttp(), ffmpeg)).ConvertAsync(input, ConversionPreset.Find(null), false, new ProgressLog(), CancellationToken.None));
        Assert.Empty(ffmpeg.Requests);
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task Failed_conversion_removes_partial_output_and_keeps_the_original()
    {
        var input = _dir.Write("clip.mp4", "video");
        var failing = new FakeProcesses((r, _) => { File.WriteAllText(r.Arguments[^1], "partial"); return Task.FromResult(1); });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Converter(Env.Create(new FakeHttp(), failing)).ConvertAsync(input, ConversionPreset.Find("webm"), false, new ProgressLog(), CancellationToken.None));
        Assert.False(File.Exists(_dir.File("clip.webm")));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task Missing_input_is_reported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new Converter(Env.Create(new FakeHttp())).ConvertAsync(_dir.File("gone.mp4"), ConversionPreset.Find("mp3"), true, new ProgressLog(), CancellationToken.None));
    }

    [Fact]
    public async Task Download_manager_converts_after_saving()
    {
        var http = new FakeHttp().Bytes("https://cdn.test/f.mp4", new byte[] { 1, 2, 3 }, "video/mp4");
        var ffmpeg = FakeProcesses.Ffmpeg();
        var progress = new ProgressLog();
        var log = new List<string>();
        var opt = new DownloadOptions
        {
            Url = "https://cdn.test/f.mp4", OutputFolder = _dir.Path, FileName = "v",
            ConversionPresetId = "mp4-720p", KeepOriginal = false,
        };

        var path = await new DownloadManager(Env.Create(http, ffmpeg, tempRoot: _dir.File("tmp"))).DownloadAsync(opt, DownloadEngine.Auto, progress, log.Add, CancellationToken.None);

        Assert.Equal(_dir.File("v (720p).mp4"), path);
        Assert.False(File.Exists(_dir.File("v.mp4")));
        Assert.Contains(log, l => l.StartsWith("Converting v.mp4 to MP4 - 720p"));
        Assert.Contains(log, l => l.StartsWith("Converted: "));
        Assert.Contains(progress.Percents, p => p is >= 80 and <= 100);
        Assert.DoesNotContain(progress.Percents, p => p > 100);
    }

    [Fact]
    public void Options_carry_conversion_settings_when_resolving_a_page()
    {
        var opt = new DownloadOptions { Url = "u", OutputFolder = "o", ConversionPresetId = "mp3", KeepOriginal = false };
        var copy = opt.WithUrl("u2", null, null);
        Assert.Equal("mp3", copy.ConversionPresetId);
        Assert.False(copy.KeepOriginal);
        Assert.True(new DownloadOptions { Url = "u", OutputFolder = "o" }.KeepOriginal);
    }
}
