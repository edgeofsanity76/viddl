using Viddl.Core.Models;
using Viddl.Downloads.Engines;
using Viddl.Downloads.Processes;
using Viddl.Tests;

namespace Viddl.Downloads.Tests;

public class TagTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string Description = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
          Metadata:
            major_brand     : isom
            title           : My clip
            comment         : Source: https://site.test/watch
            date            : 2026
          Duration: 00:00:03.00, start: 0.000000, bitrate: 300 kb/s
          Stream #0:0[0x1](und): Video: h264
            Metadata:
              handler_name    : VideoHandler
        """;

    [Fact]
    public void Fields_are_video_tags_only()
    {
        Assert.Equal(new[] { "title", "description", "comment", "date", "genre" }, VideoTags.Fields.Select(f => f.Key));
        Assert.True(VideoTags.IsVideoTag("TITLE"));
        Assert.False(VideoTags.IsVideoTag("artist"));
        Assert.False(VideoTags.IsVideoTag("album"));
    }

    [Fact]
    public void Parse_reads_file_level_tags_only()
    {
        var tags = VideoTags.Parse(Description.Split('\n'));
        Assert.Equal("My clip", tags["title"]);
        Assert.Equal("Source: https://site.test/watch", tags["Comment"]);
        Assert.Equal("2026", tags["date"]);
        Assert.Equal("isom", tags["major_brand"]);
        Assert.Equal("", tags["handler_name"]); // stream-level block ignored
        Assert.Equal("", tags["genre"]);
        Assert.False(tags.IsEmpty);
    }

    [Fact]
    public void Parse_handles_files_without_tags()
    {
        Assert.True(VideoTags.Parse(new[] { "Input #0, mp3, from 'a.mp3':", "  Duration: 00:00:01.00", "  Stream #0:0: Audio: mp3" }).IsEmpty);
        Assert.True(VideoTags.Parse(new[] { "garbage" }).IsEmpty);
        var noDuration = VideoTags.Parse(new[] { "Input #0, x:", "  Metadata:", "    title : T", "    broken line", "  Stream #0:0: Video" });
        Assert.Equal("T", noDuration["title"]);
        var endsInBlock = VideoTags.Parse(new[] { "Input #0, x:", "  Metadata:", "    title : Last" });
        Assert.Equal("Last", endsInBlock["title"]);
        var otherLineFirst = VideoTags.Parse(new[] { "Input #0, x:", "  Chapters:", "  Metadata:", "    title : After chapters" });
        Assert.Equal("After chapters", otherLineFirst["title"]);
    }

    [Fact]
    public void Values_are_trimmed_and_empty_values_count_as_empty()
    {
        var tags = new VideoTags(new Dictionary<string, string> { ["title"] = "  Hi  ", ["comment"] = "" });
        Assert.Equal("Hi", tags["title"]);
        tags["title"] = null!;
        Assert.True(tags.IsEmpty);
        Assert.Equal(2, tags.Values.Count);
    }

    [Fact]
    public async Task Read_runs_ffmpeg_and_parses_its_description()
    {
        var file = _dir.Write("clip.mp4", "x");
        var procs = new FakeProcesses((r, _) =>
        {
            Assert.Equal(new[] { "-hide_banner", "-i", file }, r.Arguments);
            foreach (var line in Description.Split('\n')) r.StandardError!(line);
            return Task.FromResult(1); // ffmpeg exits with an error when no output is given
        });
        var tags = await new TagEditor(Env.Create(new FakeHttp(), procs)).ReadAsync(file);
        Assert.Equal("My clip", tags["title"]);
    }

    [Fact]
    public async Task Write_replaces_the_file_only_after_ffmpeg_succeeds()
    {
        var file = _dir.Write("clip.mp4", "original");
        var procs = new FakeProcesses((r, _) =>
        {
            File.WriteAllText(r.Arguments[^1], "tagged");
            return Task.FromResult(0);
        });
        var tags = new VideoTags { ["title"] = "New title", ["comment"] = "" };

        await new TagEditor(Env.Create(new FakeHttp(), procs)).WriteAsync(file, tags);

        Assert.Equal("tagged", File.ReadAllText(file));
        var args = procs.Requests.Single().Arguments;
        Assert.Contains("-map_metadata", args);
        Assert.Contains("title=New title", args);
        Assert.Contains("comment=", args); // empty value removes the tag
        Assert.Contains("+faststart", args);
        Assert.Contains("copy", args);
        Assert.Single(Directory.GetFiles(_dir.Path)); // temp file gone
    }

    [Fact]
    public async Task Failed_write_leaves_the_original_untouched()
    {
        var file = _dir.Write("clip.mkv", "original");
        var procs = new FakeProcesses((r, _) =>
        {
            Assert.DoesNotContain("+faststart", r.Arguments);
            File.WriteAllText(r.Arguments[^1], "partial");
            return Task.FromResult(1);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TagEditor(Env.Create(new FakeHttp(), procs)).WriteAsync(file, new VideoTags { ["title"] = "x" }));
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Missing_files_are_reported()
    {
        var editor = new TagEditor(Env.Create(new FakeHttp()));
        await Assert.ThrowsAsync<FileNotFoundException>(() => editor.ReadAsync(_dir.File("gone.mp4")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => editor.WriteAsync(_dir.File("gone.mp4"), new VideoTags()));
    }

    private DownloadManager Manager(FakeHttp http, FakeProcesses procs) => new(Env.Create(http, procs, tempRoot: _dir.File("tmp")));

    [Fact]
    public async Task Downloads_get_their_video_tags_written()
    {
        var http = new FakeHttp().Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var procs = FakeProcesses.Ffmpeg();
        var log = new List<string>();
        var opt = new DownloadOptions
        {
            Url = "https://cdn.test/f.mp4", OutputFolder = _dir.Path, FileName = "v",
            Tags = new Dictionary<string, string> { ["title"] = "Nice video", ["comment"] = "Source: https://site.test", ["artist"] = "ignored", ["date"] = " " },
        };

        var path = await Manager(http, procs).DownloadAsync(opt, DownloadEngine.Auto, new ProgressLog(), log.Add, CancellationToken.None);

        Assert.Equal(_dir.File("v.mp4"), path);
        var args = procs.Requests.Single().Arguments;
        Assert.Contains("title=Nice video", args);
        Assert.Contains("comment=Source: https://site.test", args);
        Assert.DoesNotContain(args, a => a.StartsWith("artist="));  // not a video tag
        Assert.DoesNotContain(args, a => a.StartsWith("date="));    // blank
        Assert.Contains("Tagged: title, comment", log);
    }

    [Fact]
    public async Task Tagging_failure_is_logged_but_the_download_succeeds()
    {
        var http = new FakeHttp().Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var failing = new FakeProcesses((_, _) => Task.FromResult(1));
        var log = new List<string>();
        var opt = new DownloadOptions { Url = "https://cdn.test/f.mp4", OutputFolder = _dir.Path, FileName = "v", Tags = new Dictionary<string, string> { ["title"] = "T" } };

        var path = await Manager(http, failing).DownloadAsync(opt, DownloadEngine.Auto, new ProgressLog(), log.Add, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Contains(log, l => l.StartsWith("Couldn't add tags: ffmpeg failed"));
    }

    [Fact]
    public async Task No_tags_means_no_extra_ffmpeg_run()
    {
        var http = new FakeHttp().Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var procs = FakeProcesses.Ffmpeg();
        var opt = new DownloadOptions { Url = "https://cdn.test/f.mp4", OutputFolder = _dir.Path, FileName = "v", Tags = new Dictionary<string, string>() };
        await Manager(http, procs).DownloadAsync(opt, DownloadEngine.Auto, new ProgressLog(), _ => { }, CancellationToken.None);
        Assert.Empty(procs.Requests);
        Assert.Equal(opt.Tags, opt.WithUrl("x", null, null).Tags);
    }

    [Fact]
    public async Task Cancellation_while_tagging_is_not_swallowed()
    {
        var http = new FakeHttp().Bytes("https://cdn.test/f.mp4", new byte[] { 1 }, "video/mp4");
        var cancelling = new FakeProcesses((_, _) => throw new OperationCanceledException());
        var opt = new DownloadOptions { Url = "https://cdn.test/f.mp4", OutputFolder = _dir.Path, FileName = "v", Tags = new Dictionary<string, string> { ["title"] = "T" } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Manager(http, cancelling).DownloadAsync(opt, DownloadEngine.Auto, new ProgressLog(), _ => { }, CancellationToken.None));
    }
}
