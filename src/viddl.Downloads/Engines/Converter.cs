using Viddl.Core.Models;
using Viddl.Core.Text;

namespace Viddl.Downloads.Engines;

/// <summary>A way of converting a saved video with ffmpeg.</summary>
/// <param name="Id">Stable identifier (stored in settings).</param>
/// <param name="Name">Shown in the app.</param>
/// <param name="Extension">Output file extension, with the dot.</param>
/// <param name="Suffix">Added to the file name when the extension doesn't change (e.g. " (720p)").</param>
/// <param name="Arguments">ffmpeg arguments between the input and the output file.</param>
public sealed record ConversionPreset(string Id, string Name, string Extension, string Suffix, IReadOnlyList<string> Arguments)
{
    /// <summary>"No conversion" - keep the downloaded MP4 as it is.</summary>
    public const string NoneId = "none";

    private static readonly string[] H264 = { "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p" };
    private static readonly string[] Aac = { "-c:a", "aac", "-b:a", "192k" };
    private static readonly string[] FastStart = { "-movflags", "+faststart" };

    /// <summary>All presets; the first is "no conversion".</summary>
    public static readonly IReadOnlyList<ConversionPreset> All = new[]
    {
        new ConversionPreset(NoneId, "MP4 (no conversion)", ".mp4", "", Array.Empty<string>()),
        new ConversionPreset("mp4-h264", "MP4 - H.264/AAC (plays everywhere)", ".mp4", " (H.264)",
            H264.Concat(new[] { "-crf", "20" }).Concat(Aac).Concat(FastStart).ToArray()),
        new ConversionPreset("mp4-720p", "MP4 - 720p (smaller)", ".mp4", " (720p)",
            new[] { "-vf", "scale=-2:'min(720,ih)'" }.Concat(H264).Concat(new[] { "-crf", "23" }).Concat(Aac).Concat(FastStart).ToArray()),
        new ConversionPreset("mp4-480p", "MP4 - 480p (small)", ".mp4", " (480p)",
            new[] { "-vf", "scale=-2:'min(480,ih)'" }.Concat(H264).Concat(new[] { "-crf", "26" })
                .Concat(new[] { "-c:a", "aac", "-b:a", "128k" }).Concat(FastStart).ToArray()),
        new ConversionPreset("mkv", "MKV (no quality loss)", ".mkv", "", new[] { "-map", "0", "-c", "copy" }),
        new ConversionPreset("webm", "WebM - VP9/Opus", ".webm", "",
            new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k" }),
        new ConversionPreset("mp3", "MP3 - audio only", ".mp3", "", new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" }),
        new ConversionPreset("m4a", "M4A - audio only (AAC)", ".m4a", "", new[] { "-vn", "-c:a", "aac", "-b:a", "192k" }),
    };

    public bool IsNone => Id == NoneId;

    /// <summary>The preset with this id, or "no conversion" for an unknown or empty id.</summary>
    public static ConversionPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    /// <summary>The output path for converting <paramref name="input"/>, never overwriting an existing file.</summary>
    public string OutputPathFor(string input)
    {
        var dir = Path.GetDirectoryName(input) ?? "";
        var name = Path.GetFileNameWithoutExtension(input);
        var sameExtension = string.Equals(Path.GetExtension(input), Extension, StringComparison.OrdinalIgnoreCase);
        return Formatting.UniquePath(Path.Combine(dir, name + (sameExtension ? Suffix : "") + Extension));
    }
}

/// <summary>Converts saved videos with ffmpeg using a <see cref="ConversionPreset"/>.</summary>
public sealed class Converter
{
    private readonly DownloadEnvironment _env;

    public Converter(DownloadEnvironment env) => _env = env;

    /// <summary>
    /// Converts <paramref name="input"/> and returns the new file's path ("no conversion" returns the input).
    /// A failed or cancelled conversion leaves no partial output and never deletes the original.
    /// </summary>
    /// <param name="keepOriginal">False: delete the input after a successful conversion.</param>
    public async Task<string> ConvertAsync(string input, ConversionPreset preset, bool keepOriginal,
        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (preset.IsNone) return input;
        if (!File.Exists(input)) throw new FileNotFoundException("The file to convert no longer exists.", input);

        var output = preset.OutputPathFor(input);
        var args = new List<string> { "-i", input };
        args.AddRange(preset.Arguments);
        args.Add(output);
        progress.Report(new DownloadProgress(null, $"Converting to {preset.Name}..."));
        try
        {
            await new FfmpegRunner(_env).RunAsync(args, progress, ct, null, $"Converting to {preset.Name}").ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            throw;
        }
        if (!keepOriginal)
        {
            try { File.Delete(input); } catch (IOException) { /* in use - keep it rather than fail */ }
        }
        return output;
    }
}
