using Viddl.Core.Blocking;
using Viddl.Core.Browsing;
using Viddl.Core.Settings;
using Viddl.Downloads;
using Viddl.Downloads.Queue;
using Viddl.Downloads.Tools;

namespace Viddl.App;

/// <summary>Creates and holds the app's long-lived objects.</summary>
public sealed class Services
{
    public Services()
    {
        // Everything lives in %LocalAppData%\viddl; a new installation starts with default settings.
        Paths = AppPaths.ForCurrentUser();
        Store = new SettingsStore(Paths.SettingsFile);
        Settings = Store.Load();

        Ads = Blocklists.CreateAdBlocker(Paths.DataDir);
        Malware = Blocklists.CreateMalwareBlocker(Paths.DataDir);
        Protection = new BrowserProtection(Ads, Malware, Settings);

        // Tools: the app's data folder first, then next to the exe, then the PATH.
        Tools = new ToolLocator(new[] { Paths.ToolsDir, Path.Combine(AppContext.BaseDirectory, "tools"), AppContext.BaseDirectory });
        Installer = new ToolInstaller(Paths.ToolsDir);
        Environment = new DownloadEnvironment { Tools = Tools };
        Queue = new DownloadQueue(new DownloadManager(Environment));
    }

    public AppPaths Paths { get; }
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public DomainBlocklist Ads { get; }
    public DomainBlocklist Malware { get; }
    public BrowserProtection Protection { get; }
    public ToolLocator Tools { get; }
    public ToolInstaller Installer { get; }
    public DownloadEnvironment Environment { get; }
    public DownloadQueue Queue { get; }

    public void Save() => Store.Save(Settings);
}
