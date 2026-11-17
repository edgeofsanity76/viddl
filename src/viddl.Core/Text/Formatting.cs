namespace Viddl.Core.Text;

/// <summary>File-name, size and progress text helpers.</summary>
public static class Formatting
{
    private static readonly string[] GenericNames =
        { "index", "master", "playlist", "manifest", "chunklist", "prog_index", "main", "video" };

    /// <summary>Makes a string safe to use as a Windows file name (max 150 characters).</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 150) cleaned = cleaned[..150].TrimEnd();
        return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned;
    }

    /// <summary>Returns a path that doesn't exist yet by appending " (1)", " (2)"... before the extension.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>" · 2.1 MB/s · ETA 01:23" from bytes so far, elapsed time and fraction done (0-1).</summary>
    public static string SpeedEta(long bytes, TimeSpan elapsed, double fractionDone)
    {
        if (elapsed.TotalSeconds < 1 || bytes <= 0) return "";
        var s = $" · {bytes / 1048576.0 / elapsed.TotalSeconds:F1} MB/s";
        if (fractionDone > 0.01 && fractionDone < 1)
        {
            var eta = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fractionDone) / fractionDone);
            s += " · ETA " + Duration(eta);
        }
        return s;
    }

    /// <summary>"mm:ss" below an hour, "h:mm:ss" above.</summary>
    public static string Duration(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

    /// <summary>"12.3 MB" or "1.25 GB".</summary>
    public static string FileSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / 1048576.0:F1} MB";

    /// <summary>Truncates with an ellipsis.</summary>
    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Guesses a readable name from a stream URL, skipping generic names like "index" or "master".</summary>
    public static string GuessName(Uri uri, DateTime now)
    {
        var segments = uri.Segments.Select(s => Uri.UnescapeDataString(s.Trim('/'))).Where(s => s.Length > 0).ToList();
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var name = Path.GetFileNameWithoutExtension(segments[i]);
            if (name.Length > 2
                && !GenericNames.Any(g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase))
                && !name.All(c => char.IsDigit(c) || c == 'p'))
                return name;
        }
        return $"video_{now:yyyyMMdd_HHmmss}";
    }

    /// <summary>"cdn.example.com / file.mp4" style label for a URL.</summary>
    public static string ShortName(string url)
    {
        var u = new Uri(url);
        var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
        return string.IsNullOrEmpty(last) ? u.Host : $"{u.Host} / {Uri.UnescapeDataString(last)}";
    }
}
