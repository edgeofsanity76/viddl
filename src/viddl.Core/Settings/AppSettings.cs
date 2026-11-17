namespace Viddl.Core.Settings;

/// <summary>A favorite or history entry.</summary>
public sealed class SiteEntry
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public DateTime LastVisited { get; set; } = DateTime.Now;
    public int Visits { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title!;
}

/// <summary>A site where strict mode was switched on automatically, and why.</summary>
public sealed class AutoStrictSite
{
    public string Site { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime Since { get; set; }
}

/// <summary>Window position and size.</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>User preferences, favorites and history.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 3;
    public const int MaxHistory = 300;

    /// <summary>Settings format version (used to apply new defaults to existing settings files).</summary>
    public int Version { get; set; }

    public string? Folder { get; set; }
    public int Quality { get; set; }
    public int Engine { get; set; }
    public int Parallel { get; set; } = 4;
    public int CookieBrowser { get; set; }
    public bool OpenFolderWhenDone { get; set; }
    public bool NotifyWhenDone { get; set; } = true;
    public bool AdBlock { get; set; } = true;
    public bool Protection { get; set; } = true;
    /// <summary>Strict mode for every site. Off by default; <see cref="AutoStrict"/> enables it per site when needed.</summary>
    public bool StrictScripts { get; set; }

    /// <summary>Switch strict mode on for sites that behave suspiciously.</summary>
    public bool AutoStrict { get; set; } = true;

    /// <summary>Sites where strict mode was switched on automatically.</summary>
    public List<AutoStrictSite> AutoStrictSites { get; set; } = new();

    /// <summary>Sites (registrable domains) where strict mode allows third-party scripts.</summary>
    public List<string> StrictAllowedSites { get; set; } = new();

    public WindowPlacement? MainBounds { get; set; }
    public bool MainMaximized { get; set; }
    public string? LastUrl { get; set; }
    public bool ClearStreamsOnNavigate { get; set; } = true;

    /// <summary>Detected streams below this height are hidden (0 = show all).</summary>
    public int MinStreamHeight { get; set; }

    /// <summary>Whether streams whose resolution isn't known pass the quality filter.</summary>
    public bool ShowUnknownQuality { get; set; } = true;

    /// <summary>Conversion preset applied to every download once it's saved ("none" = keep the MP4).</summary>
    public string ConversionPreset { get; set; } = "none";

    /// <summary>Keep the downloaded file after converting it.</summary>
    public bool KeepOriginalAfterConvert { get; set; } = true;
    public int StreamsPanelWidth { get; set; } = 420;
    public int DownloadsPanelHeight { get; set; } = 260;
    public List<SiteEntry> Favorites { get; set; } = new();
    public List<SiteEntry> History { get; set; } = new();

    /// <summary>URL without the #fragment or trailing slash, used to match pages.</summary>
    public static string Key(string url)
    {
        var i = url.IndexOf('#');
        return (i >= 0 ? url[..i] : url).TrimEnd('/');
    }

    private static bool SamePage(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

    public SiteEntry? FindFavorite(string url) => Favorites.FirstOrDefault(f => SamePage(f.Url, url));

    /// <summary>Adds the page to favorites, or removes it if it's already there. Returns true when added.</summary>
    public bool ToggleFavorite(string url, string? title)
    {
        var existing = FindFavorite(url);
        if (existing != null)
        {
            Favorites.Remove(existing);
            return false;
        }
        Favorites.Add(new SiteEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : title,
        });
        return true;
    }

    /// <summary>Moves (or adds) the page to the top of the history.</summary>
    public void RecordVisit(string url, string? title, DateTime now)
    {
        var e = History.FirstOrDefault(h => SamePage(h.Url, url));
        if (e == null) e = new SiteEntry { Url = url };
        else History.Remove(e);
        History.Insert(0, e);
        e.Visits++;
        e.LastVisited = now;
        if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) e.Title = title;
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
    }

    /// <summary>Updates the stored title of a page once it has loaded.</summary>
    public void UpdateTitle(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var e = History.FirstOrDefault(h => SamePage(h.Url, url));
        if (e != null) e.Title = title;
    }

    public AutoStrictSite? FindAutoStrict(string site) =>
        AutoStrictSites.FirstOrDefault(a => string.Equals(a.Site, site, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records that strict mode was switched on automatically for a site.</summary>
    public void AddAutoStrict(string site, string reason, DateTime now)
    {
        if (FindAutoStrict(site) != null) return;
        AutoStrictSites.Add(new AutoStrictSite { Site = site, Reason = reason, Since = now });
    }

    public void RemoveAutoStrict(string site) =>
        AutoStrictSites.RemoveAll(a => string.Equals(a.Site, site, StringComparison.OrdinalIgnoreCase));

    public bool IsStrictAllowed(string site) => StrictAllowedSites.Contains(site, StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns the strict-mode exception for a site on or off. Returns the new state.</summary>
    public bool ToggleStrictAllowed(string site)
    {
        if (IsStrictAllowed(site))
        {
            StrictAllowedSites.RemoveAll(s => string.Equals(s, site, StringComparison.OrdinalIgnoreCase));
            return false;
        }
        StrictAllowedSites.Add(site);
        return true;
    }

    /// <summary>Brings settings from an older version up to date.</summary>
    public void Upgrade()
    {
        // v3: strict mode is off by default and switched on per site when needed - switch it off once.
        if (Version < 3) StrictScripts = false;
        Version = CurrentVersion;
        Favorites ??= new();
        History ??= new();
        StrictAllowedSites ??= new();
        AutoStrictSites ??= new();
        Parallel = Math.Clamp(Parallel, 1, 16);
    }
}
