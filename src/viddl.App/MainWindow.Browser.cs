using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Viddl.Core.Blocking;
using Viddl.Core.Browsing;
using Viddl.Core.Net;
using Viddl.Core.Streams;
using Viddl.Core.Text;

namespace Viddl.App;

/// <summary>The built-in browser: navigation, protection, favorites and the address bar.</summary>
public sealed partial class MainWindow
{
    private string? _guardScriptId;
    private int _threatCount;
    private int _adCount;
    private readonly HashSet<string> _loggedThreats = new(StringComparer.OrdinalIgnoreCase);
    private string _lastPath = "";
    private StrictModeAdvisor? _advisor;
    private string? _noticeSite;

    private StrictModeAdvisor Advisor => _advisor ??= new StrictModeAdvisor(S);

    private async Task InitBrowserAsync()
    {
        // The browser keeps its own profile (logins, cookies) in viddl's data folder.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Services.Paths.BrowserProfileDir);
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-blink-features=AutomationControlled");
        try
        {
            await Web.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Collapsed;
            WebViewMissing.Visibility = Visibility.Visible;
            Log("The built-in browser could not start: " + ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = true;
        try { core.Settings.IsReputationCheckingRequired = true; } catch (Exception) { /* older runtime */ }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserScripts.HideAutomation);
        await ApplyProtectionAsync();

        core.WebResourceResponseReceived += OnResponse;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // never open separate windows
            if (Services.Protection.BlockPopups)
            {
                _adCount++;
                UpdateShield();
                Signal(core.Source, ThreatSignal.PopUp);
                return;
            }
            core.Navigate(e.Uri);
        };
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!Services.Protection.BlockFrame(e.Uri)) return;
            e.Cancel = true;
            CountThreat("frame from a known malicious site", e.Uri);
            Signal(core.Source, ThreatSignal.MaliciousRequest);
        };
        core.SourceChanged += (_, e) =>
        {
            if (Address.FocusState == FocusState.Unfocused) Address.Text = core.Source.StartsWith("data:") ? "" : core.Source;
            // Single-page sites switch videos without a full page load: a new path counts as a new page.
            var path = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : core.Source;
            if (!e.IsNewDocument && !string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase))
            {
                OnNewPage();
                RecordVisit();
            }
            _lastPath = path;
            UpdateStar();
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) RecordVisit();
            UpdateStar();
            UpdateShield();
        };
        core.HistoryChanged += (_, _) =>
        {
            BackButton.IsEnabled = core.CanGoBack;
            ForwardButton.IsEnabled = core.CanGoForward;
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!Services.Queue.IsRunning) Title = WindowTitle();
            if (AddressInput.IsHttpUrl(core.Source)) S.UpdateTitle(core.Source, core.DocumentTitle);
            _catalog.UpdateTitles(core.Source, core.DocumentTitle);
        };
        HookProtection(core);

        if (AddressInput.IsHttpUrl(S.LastUrl)) core.Navigate(S.LastUrl);
        else core.NavigateToString(BrowserScripts.StartPage);
    }

    private void Navigate(string text)
    {
        if (AddressInput.ToUrl(text) is not { } url || Web.CoreWebView2 == null) return;
        try { Web.CoreWebView2.Navigate(url); } catch (ArgumentException) { return; }
        Web.Focus(FocusState.Programmatic);
    }

    private void OnNewPage()
    {
        if (ClearOnNavigate.IsChecked == true) _catalog.Clear();
        _loggedThreats.Clear();
    }

    private void RecordVisit()
    {
        var core = Web.CoreWebView2;
        if (core != null && AddressInput.IsHttpUrl(core.Source)) S.RecordVisit(core.Source, core.DocumentTitle, DateTime.Now);
    }

    private async Task<(string UserAgent, string? Cookies)> IdentityAsync(Uri uri)
    {
        var core = Web.CoreWebView2;
        if (core == null) return (HttpClients.DefaultUserAgent, null);
        string? cookies = null;
        try
        {
            var list = await core.CookieManager.GetCookiesAsync(uri.ToString());
            if (list.Count > 0) cookies = string.Join("; ", list.Select(c => $"{c.Name}={c.Value}"));
        }
        catch (Exception) { /* cookies are optional */ }
        return (core.Settings.UserAgent, cookies);
    }

    // ---- Stream detection ---------------------------------------------------------------
    private void OnResponse(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        string Header(string name)
        {
            try { return e.Response.Headers.Contains(name) ? e.Response.Headers.GetHeader(name) : ""; }
            catch (Exception) { return ""; }
        }

        var stream = _catalog.OnResponse(e.Request.Uri, Header("Content-Type"), e.Response.StatusCode, sender.Source,
            sender.DocumentTitle, Header("Content-Range"), Header("Content-Length"));
        if (stream?.Kind == Viddl.Core.Models.StreamKind.Hls) _ = ProbeAsync(stream);
    }

    private async Task ProbeAsync(SniffedStream stream)
    {
        var (ua, cookies) = await IdentityAsync(stream.Uri);
        var description = await HlsDescription.ProbeAsync(stream.Uri, stream.PageUrl, ua, cookies);
        _catalog.ApplyDescription(stream, description);
    }

    // ---- Protection -------------------------------------------------------------------------
    private void OnRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var reason = Services.Protection.CheckRequest(e.Request.Uri, e.ResourceContext == CoreWebView2WebResourceContext.Script, sender.Source);
        if (reason == null) return;
        e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        if (reason == BlockReason.Ad)
        {
            _adCount++;
            UpdateShield();
            Signal(sender.Source, ThreatSignal.Ad);
            return;
        }
        CountThreat(BrowserProtection.Describe(reason.Value), e.Request.Uri);
        if (reason == BlockReason.MaliciousDomain) Signal(sender.Source, ThreatSignal.MaliciousRequest);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // A page sending you to another site by itself counts towards switching strict mode on; if this attempt
        // tips the balance, the check below already blocks it.
        if (BrowserProtection.IsAutomaticCrossSiteNavigation(e.Uri, sender.Source, e.IsUserInitiated, e.IsRedirected))
            Signal(sender.Source, ThreatSignal.AutomaticRedirect);

        var reason = Services.Protection.CheckNavigation(e.Uri, sender.Source, e.IsUserInitiated, e.IsRedirected);
        if (reason != null)
        {
            e.Cancel = true;
            if (reason == BlockReason.Ad) { _adCount++; UpdateShield(); return; }
            CountThreat(BrowserProtection.Describe(reason.Value), e.Uri);
            if (reason == BlockReason.MaliciousDomain) Signal(sender.Source, ThreatSignal.MaliciousRequest);
            SetStatus(0, reason == BlockReason.MaliciousDomain
                ? $"Blocked a known malicious site: {SiteKey.DisplayHost(e.Uri)}"
                : $"Blocked an automatic redirect to {SiteKey.DisplayHost(e.Uri)}");
            return;
        }
        if (!e.IsRedirected) OnNewPage();
    }

    private async Task ApplyProtectionAsync()
    {
        var core = Web.CoreWebView2;
        if (core == null) { UpdateShield(); return; }
        if (_guardScriptId != null)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(_guardScriptId);
            _guardScriptId = null;
        }
        if (S.Protection) _guardScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserScripts.Guard);
        // With protection on, page dialogs are handled (and suppressed) by viddl.
        core.Settings.AreDefaultScriptDialogsEnabled = !S.Protection;
        UpdateShield();
    }

    private void HookProtection(CoreWebView2 core)
    {
        core.ScriptDialogOpening += (_, e) =>
        {
            if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload) { e.Accept(); return; } // let the user leave
            var msg = Formatting.Shorten(e.Message.Replace('\n', ' '), 120);
            CountThreat($"page {e.Kind.ToString().ToLowerInvariant()} dialog: \"{msg}\"", e.Uri);
            Signal(core.Source, ThreatSignal.PageDialog);
        };
        core.DownloadStarting += (_, e) =>
        {
            if (!S.Protection) return;
            e.Cancel = true;
            var name = Path.GetFileName(e.ResultFilePath);
            CountThreat($"download of \"{name}\" started by the page", e.DownloadOperation.Uri);
            Signal(core.Source, ThreatSignal.PageDownload);
            SetStatus(0, $"Blocked a download started by the page: {name}. Use Download URL if you really want it.");
        };
        core.PermissionRequested += (_, e) =>
        {
            if (!S.Protection) return;
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            CountThreat($"permission request: {e.PermissionKind}", e.Uri);
            Signal(core.Source, ThreatSignal.PermissionRequest);
        };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            if (!S.Protection) return;
            e.Cancel = true;
            CountThreat("attempt to open an external program", e.Uri);
            Signal(core.Source, ThreatSignal.ExternalProgram);
        };
    }

    private void CountThreat(string what, string url)
    {
        _threatCount++;
        UpdateShield();
        // Log each kind of block once per page so the log stays readable.
        if (_loggedThreats.Add(what + "|" + SiteKey.DisplayHost(url)))
            Log($"Protection blocked {what} ({SiteKey.DisplayHost(url)})");
    }

    /// <summary>Feeds a suspicious action to the advisor; tells the user if strict mode got switched on.</summary>
    private void Signal(string? pageUrl, ThreatSignal signal)
    {
        if (Advisor.Record(pageUrl, signal) is not { } decision) return;
        Services.Save();
        Log($"Strict mode switched on for {decision.Site}. {decision.Reason}.");
        _noticeSite = decision.Site;
        StrictNotice.Title = $"Strict mode switched on for {decision.Site}";
        StrictNotice.Message = $"{decision.Reason}. Third-party scripts and automatic redirects are now blocked on this site. " +
                               "Reload to apply it to scripts that already loaded.";
        StrictNotice.IsOpen = true;
        UpdateShield();
    }

    private void UpdateShield()
    {
        var parts = new List<string>();
        if (S.Protection) parts.Add($"{_threatCount} threat{(_threatCount == 1 ? "" : "s")}");
        if (S.AdBlock) parts.Add($"{_adCount} ads");
        ShieldText.Text = parts.Count == 0 ? "Protection off" : string.Join(" · ", parts);
        ShieldIcon.Glyph = S.Protection ? "\uE83D" : "\uE7BA";
        UpdateStrictBadge();
    }

    /// <summary>
    /// Updates the strict-mode icon beside the address bar: an orange "Strict" badge when strict mode applies to the
    /// current page, a grey open lock when it doesn't.
    /// </summary>
    private void UpdateStrictBadge()
    {
        var page = Web.CoreWebView2?.Source;
        var state = Services.Protection.StrictStateFor(page);
        var site = SiteKey.SiteOfUrl(page);
        bool on = state is StrictState.Everywhere or StrictState.Detected;

        StrictBadgeIcon.Glyph = on ? "" : ""; // closed lock / open lock
        StrictBadgeText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StrictBadgeText.Text = state == StrictState.Detected ? "Strict · detected" : "Strict";
        if (on)
        {
            StrictBadge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"];
            StrictBadge.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            StrictBadgeIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            StrictBadge.ClearValue(Control.BackgroundProperty);
            StrictBadge.ClearValue(Control.BorderBrushProperty);
            StrictBadgeIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        var tip = state switch
        {
            StrictState.Detected when site != null && S.FindAutoStrict(site) is { } a =>
                $"Strict mode is on for {site} (switched on {a.Since:d}). {a.Reason}.\n" +
                "Third-party scripts and automatic redirects are blocked. Click for options.",
            StrictState.Everywhere =>
                "Strict mode is on for every site: third-party scripts and automatic redirects are blocked. Click for options.",
            StrictState.AllowedHere =>
                $"Strict mode is off on {site} because you allowed its scripts. Click for options.",
            _ => "Strict mode is off for this page. viddl switches it on automatically if a site misbehaves. Click for options.",
        };
        ToolTipService.SetToolTip(StrictBadge, tip);
    }

    private void StrictBadge_Click(object sender, RoutedEventArgs e) => ShieldFlyout.ShowAt(StrictBadge);

    private void StrictNoticeReload_Click(object sender, RoutedEventArgs e)
    {
        StrictNotice.IsOpen = false;
        Web.CoreWebView2?.Reload();
    }

    private void StrictNoticeUndo_Click(object sender, RoutedEventArgs e)
    {
        StrictNotice.IsOpen = false;
        if (_noticeSite != null) TurnOffStrictForSite(_noticeSite);
    }

    private void StrictNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => _noticeSite = null;

    /// <summary>Stops strict mode on a site and keeps it off (it won't be detected again).</summary>
    private void TurnOffStrictForSite(string site)
    {
        S.RemoveAutoStrict(site);
        if (!S.IsStrictAllowed(site)) S.ToggleStrictAllowed(site);
        Advisor.Forget(site);
        Services.Save();
        Log($"Strict mode turned off for {site}.");
        UpdateShield();
        Web.CoreWebView2?.Reload();
    }

    private IEnumerable<ToggleMenuFlyoutItem> ProtectionToggles()
    {
        yield return Toggle("Block malicious sites & scripts", () => S.Protection, async v => { S.Protection = v; await ApplyProtectionAsync(); Web.CoreWebView2?.Reload(); });
        yield return Toggle("Detect when a site needs strict mode", () => S.AutoStrict, v => { S.AutoStrict = v; UpdateShield(); });
        yield return Toggle("Strict mode on every site", () => S.StrictScripts, v => { S.StrictScripts = v; UpdateShield(); Web.CoreWebView2?.Reload(); });
        yield return Toggle("Block ads & pop-ups", () => S.AdBlock, v => { S.AdBlock = v; UpdateShield(); Web.CoreWebView2?.Reload(); });
    }

    private void ShieldFlyout_Opening(object sender, object e)
    {
        ShieldFlyout.Items.Clear();
        ShieldFlyout.Items.Add(new MenuFlyoutItem { Text = $"{_threatCount} threats and {_adCount} ads blocked this session", IsEnabled = false });
        ShieldFlyout.Items.Add(new MenuFlyoutSeparator());

        var site = SiteKey.SiteOfUrl(Web.CoreWebView2?.Source);
        if (site != null)
        {
            switch (Services.Protection.StrictStateFor(Web.CoreWebView2?.Source))
            {
                case StrictState.Detected:
                    var auto = S.FindAutoStrict(site)!;
                    ShieldFlyout.Items.Add(new MenuFlyoutItem { Text = $"Strict mode detected for {site} on {auto.Since:d}", IsEnabled = false });
                    ShieldFlyout.Items.Add(new MenuFlyoutItem { Text = auto.Reason, IsEnabled = false });
                    ShieldFlyout.Items.Add(Item($"Turn strict mode off for {site}", () => TurnOffStrictForSite(site)));
                    ShieldFlyout.Items.Add(new MenuFlyoutSeparator());
                    break;
                case StrictState.Everywhere:
                case StrictState.AllowedHere:
                    var allow = new ToggleMenuFlyoutItem { Text = $"Allow third-party scripts on {site}", IsChecked = S.IsStrictAllowed(site) };
                    ToolTipService.SetToolTip(allow, "Use this if a video player on this site doesn't work in strict mode.");
                    allow.Click += (_, _) =>
                    {
                        S.ToggleStrictAllowed(site);
                        Services.Save();
                        UpdateShield();
                        Web.CoreWebView2?.Reload();
                    };
                    ShieldFlyout.Items.Add(allow);
                    ShieldFlyout.Items.Add(new MenuFlyoutSeparator());
                    break;
            }
        }
        foreach (var t in ProtectionToggles()) ShieldFlyout.Items.Add(t);
        ShieldFlyout.Items.Add(new MenuFlyoutSeparator());
        ShieldFlyout.Items.Add(Item("Show log", () => Tabs.SelectedItem = LogTab));
    }

    private async Task UpdateBlockListsAsync()
    {
        try
        {
            SetStatus(null, "Updating block lists...");
            using var http = HttpClients.Create();
            var ads = await Services.Ads.UpdateAsync(http, Blocklists.LooksLikeHostsFile);
            var bad = await Services.Malware.UpdateAsync(http);
            var text = $"Block lists updated: {ads:N0} ad domains, {bad:N0} malware/miner domains.";
            SetStatus(0, text);
            Log(text);
            Web.CoreWebView2?.Reload();
        }
        catch (Exception ex)
        {
            SetStatus(0, "Couldn't update block lists: " + ex.Message);
        }
    }

    private async Task ClearBrowserDataAsync()
    {
        if (Web.CoreWebView2 == null) return;
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Clear browser data", "Delete all cookies, cache and site data from the built-in browser? You'll be signed out of sites.", "Clear", "Cancel")) return;
        await Web.CoreWebView2.Profile.ClearBrowsingDataAsync();
        Web.CoreWebView2.Reload();
        SetStatus(0, "Browser data cleared.");
    }

    // ---- Navigation buttons -----------------------------------------------------------------
    private void BackButton_Click(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }

    private void ForwardButton_Click(object sender, RoutedEventArgs e) { if (Web.CanGoForward) Web.GoForward(); }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    private void GoButton_Click(object sender, RoutedEventArgs e) => Navigate(Address.Text);

    // ---- Address bar with favorites and recent sites ---------------------------------------
    private void Address_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = Suggestions(sender.Text);
    }

    private List<SuggestionRow> Suggestions(string text) =>
        SuggestionEngine.Build(text, S.Favorites, S.History).Select(s => new SuggestionRow(s)).ToList();

    private TextBox? _addressText;
    private bool _selectAllOnRelease;

    private void Address_GotFocus(object sender, RoutedEventArgs e)
    {
        HookAddressText();
        // Select the whole address so typing replaces it. A mouse click places the caret after focus arrives,
        // so for clicks the selection is applied when the button is released.
        _selectAllOnRelease = true;
        DispatcherQueue.TryEnqueue(() => _addressText?.SelectAll());

        // Show recent sites straight away when the bar is clicked.
        Address.ItemsSource = Suggestions("");
        Address.IsSuggestionListOpen = true;
    }

    /// <summary>Finds the text box inside the AutoSuggestBox (once) to control its selection.</summary>
    private void HookAddressText()
    {
        if (_addressText != null) return;
        _addressText = FindChild<TextBox>(Address);
        if (_addressText == null) return;
        _addressText.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
        {
            if (!_selectAllOnRelease) return;
            _selectAllOnRelease = false;
            _addressText.SelectAll();
        }), handledEventsToo: true);
        _addressText.LostFocus += (_, _) => _selectAllOnRelease = false;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void Address_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = args.ChosenSuggestion is SuggestionRow s ? s.Url : args.QueryText;
        sender.IsSuggestionListOpen = false;
        Navigate(target);
    }

    // ---- Favorites -------------------------------------------------------------------------
    private string? CurrentPageUrl => AddressInput.IsHttpUrl(Web.CoreWebView2?.Source) ? Web.CoreWebView2!.Source : null;

    private void UpdateStar()
    {
        var url = CurrentPageUrl;
        bool fav = url != null && S.FindFavorite(url) != null;
        StarIcon.Glyph = fav ? "" : "";
        StarButton.IsEnabled = url != null;
    }

    private void StarButton_Click(object sender, RoutedEventArgs e) => ToggleFavorite();

    private void ToggleFavorite()
    {
        var url = CurrentPageUrl;
        if (url == null) return;
        var added = S.ToggleFavorite(url, Web.CoreWebView2!.DocumentTitle);
        Services.Save();
        UpdateStar();
        SetStatus(0, added ? $"Added to favorites: {S.FindFavorite(url)!.DisplayTitle}" : "Removed from favorites.");
    }

    private void FavoritesFlyout_Opening(object sender, object e)
    {
        FavoritesFlyout.Items.Clear();
        var url = CurrentPageUrl;
        if (url != null)
        {
            bool fav = S.FindFavorite(url) != null;
            FavoritesFlyout.Items.Add(Item(fav ? "Remove this page from favorites" : "Add this page to favorites (Ctrl+D)", ToggleFavorite));
            FavoritesFlyout.Items.Add(new MenuFlyoutSeparator());
        }
        if (S.Favorites.Count == 0) FavoritesFlyout.Items.Add(new MenuFlyoutItem { Text = "No favorites yet", IsEnabled = false });
        foreach (var f in S.Favorites.Take(40))
        {
            var target = f.Url;
            var item = Item(Formatting.Shorten(f.DisplayTitle, 60), () => Navigate(target));
            ToolTipService.SetToolTip(item, f.Url);
            FavoritesFlyout.Items.Add(item);
        }
        FavoritesFlyout.Items.Add(new MenuFlyoutSeparator());

        var recent = new MenuFlyoutSubItem { Text = "Recently visited" };
        foreach (var h in S.History.Take(20))
        {
            var target = h.Url;
            var item = Item(Formatting.Shorten(h.DisplayTitle, 60), () => Navigate(target));
            ToolTipService.SetToolTip(item, $"{h.Url}\nLast visited {h.LastVisited:g}");
            recent.Items.Add(item);
        }
        if (recent.Items.Count == 0) recent.Items.Add(new MenuFlyoutItem { Text = "Nothing yet", IsEnabled = false });
        else
        {
            recent.Items.Add(new MenuFlyoutSeparator());
            recent.Items.Add(Item("Clear history…", async () => await ClearHistoryAsync()));
        }
        FavoritesFlyout.Items.Add(recent);
        FavoritesFlyout.Items.Add(Item("Organize favorites…", async () => await OrganizeFavoritesAsync()));
    }

    private async Task OrganizeFavoritesAsync()
    {
        var dialog = new FavoritesDialog(Root.XamlRoot, S.Favorites);
        await dialog.ShowAsync();
        Services.Save();
        UpdateStar();
        if (dialog.UrlToOpen != null) Navigate(dialog.UrlToOpen);
    }

    private async Task ClearHistoryAsync()
    {
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Clear history", "Clear the list of recently visited sites?", "Clear", "Cancel")) return;
        S.History.Clear();
        Services.Save();
        SetStatus(0, "History cleared.");
    }
}
