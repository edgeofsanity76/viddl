using Viddl.Core.Settings;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class SettingsTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0);

    [Theory]
    [InlineData("https://a.com/x#frag", "https://a.com/x")]
    [InlineData("https://a.com/x/", "https://a.com/x")]
    [InlineData("https://a.com/", "https://a.com")]
    public void Key_ignores_fragment_and_trailing_slash(string url, string key) =>
        Assert.Equal(key, AppSettings.Key(url));

    [Fact]
    public void ToggleFavorite_adds_and_removes()
    {
        var s = new AppSettings();
        Assert.True(s.ToggleFavorite("https://a.com/page", "Page"));
        Assert.Equal("Page", s.FindFavorite("https://a.com/page/#top")!.Title);
        Assert.False(s.ToggleFavorite("https://A.com/page", null));
        Assert.Empty(s.Favorites);

        s.ToggleFavorite("https://b.com/x", "  ");
        Assert.Equal("b.com", s.Favorites[0].Title);
        s.ToggleFavorite("not a url", null);
        Assert.Null(s.Favorites[1].Title);
        Assert.Equal("not a url", s.Favorites[1].DisplayTitle);
    }

    [Fact]
    public void RecordVisit_moves_page_to_top_and_counts_visits()
    {
        var s = new AppSettings();
        s.RecordVisit("https://a.com/", "A", T0);
        s.RecordVisit("https://b.com/", "B", T0.AddMinutes(1));
        s.RecordVisit("https://a.com", "https://a.com", T0.AddMinutes(2)); // URL-like title doesn't replace a real one

        Assert.Equal(new[] { "https://a.com/", "https://b.com/" }, s.History.Select(h => h.Url));
        Assert.Equal(2, s.History[0].Visits);
        Assert.Equal("A", s.History[0].Title);
        Assert.Equal(T0.AddMinutes(2), s.History[0].LastVisited);

        s.UpdateTitle("https://b.com", "Bee");
        s.UpdateTitle("https://b.com", " ");
        s.UpdateTitle("https://unknown.com", "x");
        Assert.Equal("Bee", s.History[1].Title);
    }

    [Fact]
    public void History_is_capped()
    {
        var s = new AppSettings();
        for (int i = 0; i < AppSettings.MaxHistory + 20; i++) s.RecordVisit($"https://site{i}.com/", null, T0);
        Assert.Equal(AppSettings.MaxHistory, s.History.Count);
        Assert.Equal($"https://site{AppSettings.MaxHistory + 19}.com/", s.History[0].Url);
    }

    [Fact]
    public void Strict_exceptions_toggle()
    {
        var s = new AppSettings();
        Assert.True(s.ToggleStrictAllowed("example.com"));
        Assert.True(s.IsStrictAllowed("EXAMPLE.com"));
        Assert.False(s.ToggleStrictAllowed("Example.com"));
        Assert.False(s.IsStrictAllowed("example.com"));
    }

    [Fact]
    public void Upgrade_turns_strict_off_for_old_settings_and_repairs_values()
    {
        var old = new AppSettings
        {
            Version = 2, StrictScripts = true, Parallel = 99,
            Favorites = null!, History = null!, StrictAllowedSites = null!, AutoStrictSites = null!,
        };
        old.Upgrade();
        Assert.False(old.StrictScripts);
        Assert.Equal(AppSettings.CurrentVersion, old.Version);
        Assert.Equal(16, old.Parallel);
        Assert.NotNull(old.Favorites);
        Assert.NotNull(old.History);
        Assert.NotNull(old.StrictAllowedSites);
        Assert.NotNull(old.AutoStrictSites);

        var current = new AppSettings { Version = AppSettings.CurrentVersion, StrictScripts = true };
        current.Upgrade();
        Assert.True(current.StrictScripts); // a deliberate choice is kept
    }

    [Fact]
    public void Auto_strict_sites_are_recorded_once_and_removable()
    {
        var s = new AppSettings();
        s.AddAutoStrict("bad.com", "It opened pop-ups", T0);
        s.AddAutoStrict("BAD.com", "again", T0.AddDays(1));
        var entry = Assert.Single(s.AutoStrictSites);
        Assert.Equal("It opened pop-ups", entry.Reason);
        Assert.Equal(T0, entry.Since);
        Assert.Same(entry, s.FindAutoStrict("bad.COM"));
        s.RemoveAutoStrict("Bad.com");
        Assert.Null(s.FindAutoStrict("bad.com"));
    }

    [Fact]
    public void Store_loads_defaults_when_missing_or_corrupt()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        Assert.Equal(dir.File("settings.json"), store.FilePath);

        var defaults = store.Load();
        Assert.False(defaults.StrictScripts);
        Assert.True(defaults.AutoStrict);
        Assert.Equal(4, defaults.Parallel);

        dir.Write("settings.json", "{ not json");
        Assert.Equal(AppSettings.CurrentVersion, store.Load().Version);
    }

    [Fact]
    public void Store_round_trips_settings()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "nested", "settings.json"));
        var s = new AppSettings { Folder = "D:\\Videos", Quality = 3, MainBounds = new WindowPlacement { X = 1, Y = 2, Width = 3, Height = 4 } };
        s.ToggleFavorite("https://a.com", "A");
        s.RecordVisit("https://b.com", "B", T0);

        Assert.True(store.Save(s));
        var back = store.Load();

        Assert.Equal("D:\\Videos", back.Folder);
        Assert.Equal(3, back.Quality);
        Assert.Equal(4, back.MainBounds!.Height);
        Assert.Equal("A", back.Favorites.Single().Title);
        Assert.Equal("B", back.History.Single().Title);
    }

    [Fact]
    public void Store_reads_settings_from_older_versions()
    {
        using var dir = new TempDir();
        dir.Write("settings.json", """
            { "StrictScripts": false, "Folder": "C:\\Temp", "AutoPasteClipboard": true,
              "MainBounds": { "X": 10, "Y": 20, "Width": 900, "Height": 700, "Location": "10, 20", "IsEmpty": false } }
            """);
        var s = new SettingsStore(dir.File("settings.json")).Load();
        Assert.False(s.StrictScripts);
        Assert.True(s.AutoStrict);
        Assert.Equal("C:\\Temp", s.Folder);
        Assert.Equal(900, s.MainBounds!.Width);
    }

    [Fact]
    public void Store_save_reports_failure()
    {
        using var dir = new TempDir();
        var blocker = dir.Write("file.txt", "x"); // a file where a folder is needed
        Assert.False(new SettingsStore(Path.Combine(blocker, "settings.json")).Save(new AppSettings()));
    }

    [Fact]
    public void Store_save_reports_access_denied()
    {
        using var dir = new TempDir();
        var path = dir.Write("settings.json", "{}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(new SettingsStore(path).Save(new AppSettings()));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Store_tolerates_locked_file()
    {
        using var dir = new TempDir();
        var path = dir.Write("settings.json", "{}");
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(4, new SettingsStore(path).Load().Parallel);
    }

    [Fact]
    public void AppPaths_keep_everything_in_the_local_data_folder()
    {
        var p = new AppPaths("L");
        Assert.Equal("L", p.LocalRoot);
        Assert.Equal(Path.Combine("L", "viddl"), p.DataDir);
        Assert.Equal(Path.Combine("L", "viddl", "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine("L", "viddl", "tools"), p.ToolsDir);
        Assert.Equal(Path.Combine("L", "viddl", "WebView2"), p.BrowserProfileDir);

        var user = AppPaths.ForCurrentUser();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), user.LocalRoot);
    }

    [Fact]
    public void New_installation_starts_with_default_settings()
    {
        using var local = new TempDir();
        var paths = new AppPaths(local.Path);
        var s = new SettingsStore(paths.SettingsFile).Load();

        Assert.Null(s.Folder);
        Assert.Null(s.LastUrl);
        Assert.Empty(s.Favorites);
        Assert.Empty(s.History);
        Assert.False(s.StrictScripts);
        Assert.True(s.AutoStrict);
        Assert.True(s.Protection);
        Assert.False(File.Exists(paths.SettingsFile)); // nothing is written until the app saves
    }
}
