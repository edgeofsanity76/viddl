namespace Viddl.Downloads.Tools;

/// <summary>Finds the external helper programs. Replaceable for testing.</summary>
public interface IToolLocator
{
    /// <summary>Full path of ffmpeg.exe, or null if it isn't installed.</summary>
    string? FindFfmpeg();

    /// <summary>Full path of yt-dlp.exe, or null if it isn't installed.</summary>
    string? FindYtDlp();
}

/// <summary>Looks for the tools in the given folders, then on the PATH.</summary>
public sealed class ToolLocator : IToolLocator
{
    private readonly IReadOnlyList<string> _folders;
    private readonly Func<string?> _pathVariable;

    /// <param name="folders">Folders to search first (e.g. the app's tools folder).</param>
    /// <param name="pathVariable">Supplies the PATH variable; defaults to the process environment.</param>
    public ToolLocator(IEnumerable<string> folders, Func<string?>? pathVariable = null)
    {
        _folders = folders.ToList();
        _pathVariable = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));
    }

    public string? FindFfmpeg() => Find("ffmpeg.exe");

    public string? FindYtDlp() => Find("yt-dlp.exe");

    private string? Find(string exe)
    {
        var candidates = _folders.Select(f => Path.Combine(f, exe)).ToList();
        var path = _pathVariable() ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim('"'), exe)));
        return candidates.FirstOrDefault(File.Exists);
    }
}
