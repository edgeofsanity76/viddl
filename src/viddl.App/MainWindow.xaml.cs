using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Viddl.Core.Browsing;
using Viddl.Core.Models;
using Viddl.Core.Settings;
using Viddl.Core.Streams;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace Viddl.App;

/// <summary>
/// Main window: the built-in browser, the streams it detects while videos play, and the download queue.
/// The logic lives in viddl.Core and viddl.Downloads; this class wires it to the controls.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly (string Label, int? MaxHeight)[] Qualities =
    {
        ("Best available", null), ("2160p (4K)", 2160), ("1440p", 1440), ("1080p", 1080),
        ("720p", 720), ("480p", 480), ("360p", 360), ("Lowest", 0),
    };

    private static readonly string[] CookieBrowsers = { "None", "firefox", "edge", "chrome", "brave", "opera", "vivaldi" };
    private static readonly string[] EngineNames = { "Auto (recommended)", "Built-in only", "yt-dlp only" };

    private readonly IntPtr _hwnd;
    private readonly StreamCatalog _catalog = new();
    private bool _closeConfirmed;
    private int _logLines;

    public MainWindow()
    {
        InitializeComponent();
        Services = new Services();
        Title = "viddl";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "viddl.ico"));
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        RestorePlacement();
        LoadSettingsIntoControls();
        BuildSettingsMenu();
        WireStreams();
        WireQueue();
        AddKeyboardShortcuts();
        RefreshToolStatus();

        StreamsSplitter.TargetColumn = StreamsColumn;
        DownloadsSplitter.TargetRow = DownloadsRow;
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => OnClosed();
        Root.Loaded += async (_, _) => await InitBrowserAsync();
    }

    public Services Services { get; }

    public ObservableCollection<StreamRow> StreamRows { get; } = new();

    public ObservableCollection<QueueRow> QueueRows { get; } = new();

    private AppSettings S => Services.Settings;

    // =====================================================================================
    // Window placement and settings
    // =====================================================================================
    private void RestorePlacement()
    {
        if (S.MainBounds is { Width: > 600, Height: > 400 } b)
        {
            var area = DisplayArea.GetFromRect(new RectInt32(b.X, b.Y, b.Width, b.Height), DisplayAreaFallback.None);
            if (area != null) AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(1500, 960));
        }
        if (S.MainMaximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
        StreamsColumn.Width = new GridLength(Math.Clamp(S.StreamsPanelWidth, 280, 900));
        DownloadsRow.Height = new GridLength(Math.Clamp(S.DownloadsPanelHeight, 150, 800));
    }

    private void LoadSettingsIntoControls()
    {
        FolderBox.Text = string.IsNullOrWhiteSpace(S.Folder) ? DefaultFolder() : S.Folder;
        foreach (var q in Qualities) QualityBox.Items.Add(q.Label);
        QualityBox.SelectedIndex = Math.Clamp(S.Quality, 0, Qualities.Length - 1);
        ParallelBox.Value = S.Parallel;
        foreach (var preset in Viddl.Downloads.Engines.ConversionPreset.All) ConvertBox.Items.Add(preset.Name);
        ConvertBox.SelectedIndex = Math.Max(0, Viddl.Downloads.Engines.ConversionPreset.All.ToList()
            .IndexOf(Viddl.Downloads.Engines.ConversionPreset.Find(S.ConversionPreset)));
        ClearOnNavigate.IsChecked = S.ClearStreamsOnNavigate;
    }

    private static string DefaultFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Path.Combine(string.IsNullOrEmpty(videos) ? Environment.CurrentDirectory : videos, "viddl");
    }

    private void SaveSettings()
    {
        S.Folder = FolderBox.Text.Trim();
        S.Quality = QualityBox.SelectedIndex;
        S.Parallel = double.IsNaN(ParallelBox.Value) ? 4 : (int)ParallelBox.Value;
        S.ConversionPreset = SelectedConversion.Id;
        S.ClearStreamsOnNavigate = ClearOnNavigate.IsChecked == true;
        S.StreamsPanelWidth = (int)StreamsColumn.ActualWidth;
        S.DownloadsPanelHeight = (int)DownloadsRow.ActualHeight;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            S.MainMaximized = p.State == OverlappedPresenterState.Maximized;
            if (p.State == OverlappedPresenterState.Restored)
                S.MainBounds = new WindowPlacement { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height };
        }
        var src = Web.CoreWebView2?.Source;
        if (AddressInput.IsHttpUrl(src)) S.LastUrl = src;
        Services.Save();
    }

    private void BuildSettingsMenu()
    {
        var engine = new MenuFlyoutSubItem { Text = "Download engine" };
        for (int i = 0; i < EngineNames.Length; i++)
        {
            int index = i;
            var item = new RadioMenuFlyoutItem { Text = EngineNames[i], GroupName = "engine", IsChecked = S.Engine == i };
            item.Click += (_, _) => S.Engine = index;
            engine.Items.Add(item);
        }

        var cookies = new MenuFlyoutSubItem { Text = "yt-dlp: cookies from installed browser" };
        for (int i = 0; i < CookieBrowsers.Length; i++)
        {
            int index = i;
            var item = new RadioMenuFlyoutItem { Text = CookieBrowsers[i], GroupName = "cookies", IsChecked = S.CookieBrowser == i };
            item.Click += (_, _) => S.CookieBrowser = index;
            cookies.Items.Add(item);
        }

        SettingsFlyout.Items.Add(engine);
        SettingsFlyout.Items.Add(cookies);
        SettingsFlyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var item in ProtectionToggles()) SettingsFlyout.Items.Add(item);
        SettingsFlyout.Items.Add(Item("Update block lists (ads, malware, miners)", async () => await UpdateBlockListsAsync()));
        SettingsFlyout.Items.Add(Item("Clear browser data (cookies, cache)…", async () => await ClearBrowserDataAsync()));
        SettingsFlyout.Items.Add(Item("Clear browsing history…", async () => await ClearHistoryAsync()));
        SettingsFlyout.Items.Add(new MenuFlyoutSeparator());
        SettingsFlyout.Items.Add(Toggle("Open folder when a download finishes", () => S.OpenFolderWhenDone, v => S.OpenFolderWhenDone = v));
        SettingsFlyout.Items.Add(Toggle("Sound when downloads finish", () => S.NotifyWhenDone, v => S.NotifyWhenDone = v));
        SettingsFlyout.Items.Add(Toggle("Keep the original file after converting", () => S.KeepOriginalAfterConvert, v => S.KeepOriginalAfterConvert = v));
        SettingsFlyout.Items.Add(new MenuFlyoutSeparator());
        SettingsFlyout.Items.Add(Item("Install / update ffmpeg & yt-dlp…", async () => await InstallToolsAsync()));
        SettingsFlyout.Items.Add(new MenuFlyoutSeparator());
        SettingsFlyout.Items.Add(Item("About viddl (F1)", async () => await ShowAboutAsync()));
        SettingsFlyout.Opening += (_, _) =>
        {
            // Keep the check marks in sync with changes made from the protection menu.
            foreach (var t in SettingsFlyout.Items.OfType<ToggleMenuFlyoutItem>()) t.IsChecked = ((Func<bool>)t.Tag)();
        };
    }

    private static MenuFlyoutItem Item(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static ToggleMenuFlyoutItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = get(), Tag = get };
        item.Click += (_, _) => set(item.IsChecked);
        return item;
    }

    private void AddKeyboardShortcuts()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
            a.Invoked += (_, e) => { action(); e.Handled = true; };
            Root.KeyboardAccelerators.Add(a);
        }

        Add(VirtualKey.F5, VirtualKeyModifiers.None, () => Web.CoreWebView2?.Reload());
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => { if (Web.CanGoBack) Web.GoBack(); });
        Add(VirtualKey.Right, VirtualKeyModifiers.Menu, () => { if (Web.CanGoForward) Web.GoForward(); });
        Add(VirtualKey.L, VirtualKeyModifiers.Control, () => { Address.Focus(FocusState.Keyboard); _addressText?.SelectAll(); });
        Add(VirtualKey.D, VirtualKeyModifiers.Control, ToggleFavorite);
        Add(VirtualKey.F1, VirtualKeyModifiers.None, async () => await ShowAboutAsync());
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, () => { if (Services.Queue.IsRunning || _toolBusy || _converting) StopAll(); });
    }

    private bool _aboutOpen;

    private async Task ShowAboutAsync()
    {
        if (_aboutOpen) return; // only one dialog can be open at a time
        _aboutOpen = true;
        try { await new AboutDialog(Root.XamlRoot, Services.Paths.DataDir).ShowAsync(); }
        finally { _aboutOpen = false; }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        int pending = Services.Queue.Items.Count(i => i.State is Viddl.Downloads.Queue.QueueItemState.Running or Viddl.Downloads.Queue.QueueItemState.Waiting);
        if (pending == 0 || _closeConfirmed) return;
        args.Cancel = true;
        if (await Dialogs.ConfirmAsync(Root.XamlRoot, "Quit viddl?", $"{pending} download(s) are still running or waiting. Quit anyway?", "Quit", "Keep downloading"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnClosed()
    {
        DisposePreview();
        Services.Queue.Stop();
        _toolCts?.Cancel();
        SaveSettings();
    }

    // =====================================================================================
    // Status, log and small helpers
    // =====================================================================================
    private void SetStatus(double? percent, string text)
    {
        StatusText.Text = text;
        Progress.IsIndeterminate = percent == null;
        Progress.Value = percent ?? 0;
        Title = Services.Queue.IsRunning && percent is double p ? $"{p:F0}% - viddl" : WindowTitle();
    }

    private string WindowTitle()
    {
        var t = Web.CoreWebView2?.DocumentTitle;
        return string.IsNullOrWhiteSpace(t) || t.StartsWith("data:") ? "viddl" : $"{t} - viddl";
    }

    private void Log(string line)
    {
        if (_logLines++ > 3000)
        {
            // Keep the log from growing without bound in long sessions.
            LogBox.Text = LogBox.Text[(LogBox.Text.Length / 2)..];
            _logLines = 1500;
        }
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}\r\n";
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private static void CopyToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no app associated */ }
    }

    private static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text))
            e.AcceptedOperation = DataPackageOperation.Link | DataPackageOperation.Copy;
        await Task.CompletedTask;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        string? text = null;
        if (e.DataView.Contains(StandardDataFormats.WebLink)) text = (await e.DataView.GetWebLinkAsync()).ToString();
        else if (e.DataView.Contains(StandardDataFormats.Text)) text = await e.DataView.GetTextAsync();
        if (AddressInput.FirstUrl(text) is { } url) Navigate(url);
    }

    private Viddl.Downloads.Engines.ConversionPreset SelectedConversion =>
        Viddl.Downloads.Engines.ConversionPreset.All[Math.Max(0, ConvertBox.SelectedIndex)];

    private DownloadOptions? BuildOptions(string url, string? referer, string? name, string? userAgent, string? cookies, long? expectedBytes = null)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            _ = Dialogs.ShowAsync(Root.XamlRoot, "No folder", "Choose a folder to save videos to first.");
            return null;
        }
        return new DownloadOptions
        {
            Url = url,
            OutputFolder = folder,
            FileName = string.IsNullOrWhiteSpace(name) ? null : Viddl.Core.Text.Formatting.SafeFileName(name),
            Referer = referer,
            UserAgent = userAgent,
            Cookies = cookies,
            MaxHeight = Qualities[Math.Max(0, QualityBox.SelectedIndex)].MaxHeight,
            Concurrency = double.IsNaN(ParallelBox.Value) ? 4 : (int)ParallelBox.Value,
            CookiesFromBrowser = S.CookieBrowser > 0 ? CookieBrowsers[S.CookieBrowser] : null,
            ExpectedBytes = expectedBytes,
            ConversionPresetId = SelectedConversion.IsNone ? null : SelectedConversion.Id,
            KeepOriginal = S.KeepOriginalAfterConvert,
        };
    }
}
