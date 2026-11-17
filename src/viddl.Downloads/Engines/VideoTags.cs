using Viddl.Downloads.Processes;

namespace Viddl.Downloads.Engines;

/// <summary>Metadata tags stored in a video or audio file (title, artist, comment...).</summary>
public sealed class VideoTags
{
    /// <summary>The video tags viddl lets you edit, with their labels, in display order.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Fields = new[]
    {
        ("title", "Title"),
        ("description", "Description"),
        ("comment", "Comment"),
        ("date", "Date / year"),
        ("genre", "Genre"),
    };

    /// <summary>True for the video tags in <see cref="Fields"/>.</summary>
    public static bool IsVideoTag(string key) => Fields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public VideoTags() { }

    public VideoTags(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var (k, v) in values) this[k] = v;
    }

    /// <summary>A tag's value ("" when missing). Setting "" removes the tag when written.</summary>
    public string this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : "";
        set => _values[key] = (value ?? "").Trim();
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool IsEmpty => _values.Values.All(string.IsNullOrEmpty);

    /// <summary>
    /// Reads the file-level tags from ffmpeg's description of an input: the "Metadata:" block directly under
    /// "Input #0" (stream-level blocks further down are ignored).
    /// </summary>
    public static VideoTags Parse(IEnumerable<string> ffmpegOutput)
    {
        var tags = new VideoTags();
        bool inInput = false, inBlock = false;
        foreach (var raw in ffmpegOutput)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Input #0")) { inInput = true; continue; }
            if (!inInput) continue;
            var trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;
            if (indent == 2 && trimmed == "Metadata:") { inBlock = true; continue; }
            if (inBlock && indent >= 4)
            {
                int colon = trimmed.IndexOf(':');
                if (colon > 0) tags[trimmed[..colon].Trim()] = trimmed[(colon + 1)..].Trim();
                continue;
            }
            if (inBlock || trimmed.StartsWith("Duration:") || trimmed.StartsWith("Stream #")) break;
        }
        return tags;
    }
}

/// <summary>Reads and writes file tags with ffmpeg, without re-encoding.</summary>
public sealed class TagEditor
{
    private readonly DownloadEnvironment _env;

    public TagEditor(DownloadEnvironment env) => _env = env;

    public async Task<VideoTags> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The file no longer exists.", path);
        var lines = new List<string>();
        // "ffmpeg -i file" describes the file and exits with an error because no output is given; that's expected.
        await _env.Processes.RunAsync(new ProcessRequest
        {
            FileName = _env.RequireFfmpeg(),
            Arguments = new[] { "-hide_banner", "-i", path },
            StandardError = l => { lock (lines) lines.Add(l); },
        }, ct).ConfigureAwait(false);
        lock (lines) return VideoTags.Parse(lines);
    }

    /// <summary>
    /// Writes the given tags (empty values remove a tag; other existing tags are kept). The file is rewritten to a
    /// temporary copy and only replaced once ffmpeg succeeds, so a failure leaves the original untouched.
    /// </summary>
    public async Task WriteAsync(string path, VideoTags tags, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The file no longer exists.", path);
        var temp = Path.Combine(Path.GetDirectoryName(path) ?? "", $".viddl-tags-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        var args = new List<string> { "-i", path, "-map", "0", "-c", "copy", "-map_metadata", "0" };
        foreach (var (key, value) in tags.Values) args.AddRange(new[] { "-metadata", $"{key}={value}" });
        if (string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".m4a", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(temp);
        try
        {
            await new FfmpegRunner(_env).RunAsync(args, new Viddl.Core.Models.InlineProgress<Viddl.Core.Models.DownloadProgress>(_ => { }), ct,
                null, "Writing tags").ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
