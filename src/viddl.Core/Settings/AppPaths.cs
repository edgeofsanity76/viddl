namespace Viddl.Core.Settings;

/// <summary>
/// Where viddl keeps its files. Everything lives in the user's local (non-roaming) application data folder, so
/// settings stay on this PC and a new installation starts with default settings.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "viddl";

    /// <param name="localRoot">The local application data folder (normally %LocalAppData%).</param>
    public AppPaths(string localRoot) => LocalRoot = localRoot;

    /// <summary>Paths under the current user's %LocalAppData% folder.</summary>
    public static AppPaths ForCurrentUser() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public string LocalRoot { get; }

    /// <summary>%LocalAppData%\viddl - settings, block lists, tools and the browser profile.</summary>
    public string DataDir => Path.Combine(LocalRoot, AppFolderName);

    /// <summary>Settings, favorites and history.</summary>
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>ffmpeg and yt-dlp.</summary>
    public string ToolsDir => Path.Combine(DataDir, "tools");

    /// <summary>The built-in browser's profile (cookies, cache, logins).</summary>
    public string BrowserProfileDir => Path.Combine(DataDir, "WebView2");
}
