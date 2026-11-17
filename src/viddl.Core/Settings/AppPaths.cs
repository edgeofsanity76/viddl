namespace Viddl.Core.Settings;

/// <summary>Where viddl keeps its files.</summary>
public sealed class AppPaths
{
    public const string AppFolderName = "viddl";

    /// <summary>Folder name used by versions before 2.0.</summary>
    public const string LegacyFolderName = "StreamVideoDownloader";

    public AppPaths(string roamingRoot, string localRoot)
    {
        RoamingRoot = roamingRoot;
        LocalRoot = localRoot;
    }

    /// <summary>Paths under the current user's AppData folders.</summary>
    public static AppPaths ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public string RoamingRoot { get; }
    public string LocalRoot { get; }

    /// <summary>%AppData%\viddl - settings, favorites and history.</summary>
    public string SettingsDir => Path.Combine(RoamingRoot, AppFolderName);

    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    /// <summary>%LocalAppData%\viddl - block lists, tools and the browser profile.</summary>
    public string DataDir => Path.Combine(LocalRoot, AppFolderName);

    /// <summary>ffmpeg and yt-dlp.</summary>
    public string ToolsDir => Path.Combine(DataDir, "tools");

    /// <summary>The built-in browser's profile (cookies, cache, logins).</summary>
    public string BrowserProfileDir => Path.Combine(DataDir, "WebView2");

    /// <summary>
    /// Copies data from the pre-2.0 folders into the new ones, once. Existing new folders are left alone.
    /// Returns the folders that were migrated.
    /// </summary>
    public IReadOnlyList<string> MigrateLegacyData()
    {
        var migrated = new List<string>();
        foreach (var (root, target) in new[] { (RoamingRoot, SettingsDir), (LocalRoot, DataDir) })
        {
            var legacy = Path.Combine(root, LegacyFolderName);
            if (!Directory.Exists(legacy) || Directory.Exists(target)) continue;
            CopyDirectory(legacy, target);
            migrated.Add(target);
        }
        return migrated;
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false); }
            catch (IOException) { /* locked browser cache files etc. are not worth failing over */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
