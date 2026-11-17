using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Viddl.Core.Browsing;
using Viddl.Core.Models;
using Viddl.Core.Streams;
using Viddl.Core.Text;
using Viddl.Downloads;
using Viddl.Downloads.Queue;
using Windows.System;

namespace Viddl.App;

/// <summary>The detected-streams panel, the download queue and tool installation.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<SniffedStream, StreamRow> _rows = new();
    private bool _settingName;
    private bool _toolBusy;
    private CancellationTokenSource? _toolCts;

    // =====================================================================================
    // Detected streams
    // =====================================================================================
    private static readonly (string Label, int MinHeight)[] QualityFilters =
    {
        ("All", 0), ("360p and up", 360), ("480p and up", 480), ("720p and up", 720),
        ("1080p and up", 1080), ("1440p and up", 1440), ("2160p and up", 2160),
    };

    private void WireStreams()
    {
        foreach (var f in QualityFilters) MinQualityBox.Items.Add(f.Label);
        var selected = Array.FindIndex(QualityFilters, f => f.MinHeight == S.MinStreamHeight);
        MinQualityBox.SelectedIndex = selected < 0 ? 0 : selected;
        ShowUnknownQuality.IsChecked = S.ShowUnknownQuality;
        ShowUnknownQuality.IsEnabled = S.MinStreamHeight > 0;

        _catalog.Added += s =>
        {
            _rows[s] = new StreamRow(s);
            SyncStreamRows();
            if (StreamList.SelectedItems.Count == 0 && StreamRows.FirstOrDefault(r => r.Stream == s) is { } row) StreamList.SelectedItem = row;
        };
        _catalog.Reordered += SyncStreamRows;
        _catalog.Removed += s =>
        {
            _rows.Remove(s);
            SyncStreamRows();
        };
        _catalog.Changed += s =>
        {
            if (_rows.TryGetValue(s, out var row)) row.Refresh();
            SyncStreamRows(); // a newly known resolution can move a stream into or out of the filter
            if (SelectedStreams().ToList() is [var only] && only == s) OnStreamSelectionChanged();
        };
        _catalog.Cleared += () =>
        {
            _rows.Clear();
            SyncStreamRows();
        };
    }

    /// <summary>
    /// Makes the list show the streams that pass the quality filter, best quality first. Rows are moved, added and
    /// removed in place (rather than rebuilt) so the selection is kept.
    /// </summary>
    private void SyncStreamRows()
    {
        var visible = _catalog.Filtered(S.MinStreamHeight, S.ShowUnknownQuality)
            .Where(_rows.ContainsKey).Select(s => _rows[s]).ToList();
        for (int i = StreamRows.Count - 1; i >= 0; i--)
            if (!visible.Contains(StreamRows[i])) StreamRows.RemoveAt(i);
        for (int i = 0; i < visible.Count; i++)
        {
            int current = StreamRows.IndexOf(visible[i]);
            if (current < 0) StreamRows.Insert(i, visible[i]);
            else if (current != i) StreamRows.Move(current, i);
        }
        UpdateStreamsHeader();
    }

    private void UpdateStreamsHeader()
    {
        int total = _catalog.Items.Count, shown = StreamRows.Count;
        StreamsHeader.Text = total == 0 ? "Detected streams"
            : shown == total ? $"Detected streams ({total})"
            : $"Detected streams ({shown} of {total})";
        StreamsHint.Text = total > 0 && shown == 0
            ? "All detected streams are below the quality filter. Choose a lower minimum to see them."
            : "Play a video in the browser - its streams appear here, best quality first. Select one (or several) and click Download.";
        StreamsHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MinQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MinQualityBox.SelectedIndex < 0) return;
        S.MinStreamHeight = QualityFilters[MinQualityBox.SelectedIndex].MinHeight;
        ShowUnknownQuality.IsEnabled = S.MinStreamHeight > 0;
        SyncStreamRows();
    }

    private void ShowUnknownQuality_Click(object sender, RoutedEventArgs e)
    {
        S.ShowUnknownQuality = ShowUnknownQuality.IsChecked == true;
        SyncStreamRows();
    }

    private IEnumerable<SniffedStream> SelectedStreams() => StreamList.SelectedItems.OfType<StreamRow>().Select(r => r.Stream);

    private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnStreamSelectionChanged();

    private void OnStreamSelectionChanged()
    {
        var sel = SelectedStreams().ToList();
        DownloadSelectedButton.IsEnabled = sel.Any(s => !s.Unsupported);
        DownloadSelectedText.Text = sel.Count > 1 ? $"Download {sel.Count} selected" : "Download selected";
        CopyUrlButton.IsEnabled = sel.Count == 1;
        PreviewStreamButton.IsEnabled = sel.Count == 1 && !sel[0].Unsupported;
        NameBox.IsEnabled = sel.Count == 1;
        _settingName = true;
        NameBox.Text = sel.Count == 1 ? sel[0].DisplayName : "";
        _settingName = false;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingName || SelectedStreams().ToList() is not [var s]) return;
        _catalog.Rename(s, NameBox.Text);
    }

    private async void StreamList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await DownloadSelectedStreamsAsync();

    private async void StreamList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await DownloadSelectedStreamsAsync(); }
        else if (e.Key == VirtualKey.Delete) { e.Handled = true; RemoveSelectedStreams(); }
    }

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e) => await DownloadSelectedStreamsAsync();

    private void CopyStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStreams().FirstOrDefault() is { } s) CopyToClipboard(s.Uri.ToString());
    }

    private void RemoveStreams_Click(object sender, RoutedEventArgs e) => RemoveSelectedStreams();

    private void ClearStreams_Click(object sender, RoutedEventArgs e) => _catalog.Clear();

    private void ClearOnNavigate_Click(object sender, RoutedEventArgs e) => S.ClearStreamsOnNavigate = ClearOnNavigate.IsChecked == true;

    private void RemoveSelectedStreams()
    {
        foreach (var s in SelectedStreams().ToList()) _catalog.Remove(s);
    }

    private async Task DownloadSelectedStreamsAsync()
    {
        var picked = SelectedStreams().Where(s => !s.Unsupported).ToList();
        if (picked.Count == 0) return;
        foreach (var s in picked)
        {
            var (ua, cookies) = await IdentityAsync(s.Uri);
            var opt = BuildOptions(s.Uri.ToString(), s.PageUrl, s.DisplayName, ua, cookies, s.SizeBytes);
            if (opt == null) return;
            if (Services.Queue.Enqueue(opt, DownloadEngine.BuiltIn, s.DisplayName) == null) SetStatus(0, $"Already in the queue: {s.DisplayName}");
        }
        Tabs.SelectedItem = DownloadsTab;
        _ = RunQueueAsync();
    }

    private async void DownloadUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var url = AddressInput.FirstUrl(Address.Text);
        if (url == null)
        {
            await Dialogs.ShowAsync(Root.XamlRoot, "Download URL", "Put an http(s) link in the address bar first.");
            return;
        }
        var (ua, cookies) = await IdentityAsync(new Uri(url));
        var title = Web.CoreWebView2?.Source == url ? Web.CoreWebView2.DocumentTitle : null;
        var opt = BuildOptions(url, null, title, ua, cookies);
        if (opt == null) return;
        var name = string.IsNullOrWhiteSpace(title) ? Formatting.ShortName(url) : title;
        if (Services.Queue.Enqueue(opt, (DownloadEngine)S.Engine, name) == null) SetStatus(0, $"Already in the queue: {name}");
        Tabs.SelectedItem = DownloadsTab;
        _ = RunQueueAsync();
    }

    // =====================================================================================
    // Queue
    // =====================================================================================
    private void WireQueue()
    {
        var q = Services.Queue;
        // Mirror the queue's items as bindable rows, newest first. (The queue itself still runs oldest first.)
        q.Items.CollectionChanged += (_, _) =>
        {
            var current = QueueRows.ToDictionary(r => r.Item);
            QueueRows.Clear();
            foreach (var item in q.Items.Reverse()) QueueRows.Add(current.TryGetValue(item, out var row) ? row : new QueueRow(item));
            UpdateQueueButtons();
        };
        q.Log += Log;
        q.StateChanged += UpdateQueueButtons;
        q.Progress += (item, p) => SetStatus(p.Percent, $"{item.Name}: {p.Status}");
        q.ItemFinished += item =>
        {
            UpdateQueueButtons();
            switch (item.State)
            {
                case QueueItemState.Done:
                    SetStatus(100, $"Saved: {item.OutputPath}");
                    if (S.OpenFolderWhenDone && item.OutputPath != null) ShowInFolder(item.OutputPath);
                    break;
                case QueueItemState.Cancelled:
                    SetStatus(0, $"{item.Name}: cancelled.");
                    break;
                default:
                    SetStatus(0, $"{item.Name}: {item.Status}");
                    break;
            }
        };
        q.Finished += summary =>
        {
            SetStatus(summary.Failed == 0 && !summary.StoppedWithItemsWaiting ? 100 : 0, summary.ToString());
            if (!S.NotifyWhenDone) return;
            Native.Beep(summary.Failed > 0);
            Native.FlashTaskbar(_hwnd);
        };
    }

    private async Task RunQueueAsync()
    {
        if (_toolBusy) return;
        await Services.Queue.RunAsync();
        Title = WindowTitle();
    }

    private void UpdateQueueButtons()
    {
        var q = Services.Queue;
        StopButton.IsEnabled = q.IsRunning || _toolBusy || _converting;
        StartQueueButton.IsEnabled = !q.IsRunning && !_toolBusy && q.WaitingCount > 0;
        ClearListButton.IsEnabled = q.Items.Any(i => i.State != QueueItemState.Running);
        DownloadsTab.Header = q.WaitingCount > 0 ? $"Downloads ({q.WaitingCount} waiting)" : "Downloads";
    }

    private async void ClearList_Click(object sender, RoutedEventArgs e)
    {
        var q = Services.Queue;
        if (q.WaitingCount > 0 && !await Dialogs.ConfirmAsync(Root.XamlRoot, "Clear list",
                $"{q.WaitingCount} download(s) haven't started yet and will be removed from the queue. Clear the list anyway?",
                "Clear", "Cancel"))
            return;
        var removed = q.Clear();
        SetStatus(0, removed == 0 ? "Nothing to clear." :
            q.IsRunning ? $"Cleared {removed} item(s); the download in progress keeps going." : $"Cleared {removed} item(s).");
    }

    private void StartQueue_Click(object sender, RoutedEventArgs e) => _ = RunQueueAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAll();

    private void StopAll()
    {
        Services.Queue.Stop();
        _toolCts?.Cancel();
        SetStatus(null, "Stopping...");
    }

    private IEnumerable<QueueItem> SelectedQueueItems() => QueueList.SelectedItems.OfType<QueueRow>().Select(r => r.Item);

    private void QueueMenu_Opening(object sender, object e)
    {
        var sel = SelectedQueueItems().ToList();
        var files = sel.Where(i => i.State == QueueItemState.Done && i.OutputPath != null && File.Exists(i.OutputPath))
            .Select(i => i.OutputPath!).ToList();
        ConvertItem.Items.Clear();
        foreach (var preset in Viddl.Downloads.Engines.ConversionPreset.All.Where(p => !p.IsNone))
        {
            var item = new MenuFlyoutItem { Text = preset.Name };
            item.Click += async (_, _) => await ConvertFilesAsync(files, preset);
            ConvertItem.Items.Add(item);
        }
        ConvertItem.IsEnabled = files.Count > 0 && !_converting;
        PlayItem.IsEnabled = ShowItem.IsEnabled = sel.Any(i => i.OutputPath != null && File.Exists(i.OutputPath));
        RetryItem.IsEnabled = sel.Any(i => i.State is QueueItemState.Failed or QueueItemState.Cancelled);
        PreviewQueueMenuItem.IsEnabled = sel.Count > 0;
        CopyQueueUrlItem.IsEnabled = sel.Count > 0;
        RemoveQueueItem.IsEnabled = sel.Any(i => i.State != QueueItemState.Running);
    }

    private void QueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault() is { OutputPath: { } p } && File.Exists(p)) OpenFile(p);
    }

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;
        Services.Queue.Remove(SelectedQueueItems().ToList());
        e.Handled = true;
    }

    private void PlayItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault(i => i.OutputPath != null) is { } i) OpenFile(i.OutputPath!);
    }

    private void ShowItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault(i => i.OutputPath != null) is { } i) ShowInFolder(i.OutputPath!);
    }

    private void RetryItem_Click(object sender, RoutedEventArgs e)
    {
        Services.Queue.Retry(SelectedQueueItems().ToList());
        _ = RunQueueAsync();
    }

    private void CopyQueueUrl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault() is { } i) CopyToClipboard(i.Url);
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e) => Services.Queue.Remove(SelectedQueueItems().ToList());

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => Services.Queue.ClearFinished();

    // =====================================================================================
    // Converting saved files
    // =====================================================================================
    private bool _converting;

    /// <summary>Converts finished downloads one after another, showing progress in the status bar.</summary>
    private async Task ConvertFilesAsync(IReadOnlyList<string> files, Viddl.Downloads.Engines.ConversionPreset preset)
    {
        if (_converting || files.Count == 0) return;
        _converting = true;
        _toolCts = new CancellationTokenSource();
        StopButton.IsEnabled = true;
        bool active = true;
        var progress = new Progress<DownloadProgress>(p => { if (active) SetStatus(p.Percent, p.Status); });
        var converter = new Viddl.Downloads.Engines.Converter(Services.Environment);
        int done = 0;
        try
        {
            foreach (var file in files)
            {
                Log($"Converting {Path.GetFileName(file)} to {preset.Name}...");
                var output = await converter.ConvertAsync(file, preset, S.KeepOriginalAfterConvert, progress, _toolCts.Token);
                Log($"Converted: {output}");
                done++;
            }
            active = false;
            SetStatus(100, done == 1 ? $"Converted to {preset.Name}." : $"Converted {done} files to {preset.Name}.");
        }
        catch (OperationCanceledException)
        {
            active = false;
            SetStatus(0, "Conversion cancelled.");
        }
        catch (Exception ex)
        {
            active = false;
            SetStatus(0, "Conversion failed: " + ex.Message.Split('\n')[0]);
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            _converting = false;
            _toolCts.Dispose();
            _toolCts = null;
            UpdateQueueButtons();
        }
    }

    // =====================================================================================
    // Folder and tools
    // =====================================================================================
    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) FolderBox.Text = folder.Path;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0) return;
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void RefreshToolStatus()
    {
        var missing = new List<string>();
        if (Services.Tools.FindFfmpeg() == null) missing.Add("ffmpeg");
        if (Services.Tools.FindYtDlp() == null) missing.Add("yt-dlp");
        ToolsWarning.Text = missing.Count == 0 ? "" : $"Missing: {string.Join(", ", missing)} - Settings → Install";
        ToolsWarning.Visibility = missing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (missing.Contains("ffmpeg")) Log("ffmpeg isn't installed yet: open Settings → Install / update ffmpeg & yt-dlp (needed to save MP4 files).");
    }

    private async Task InstallToolsAsync()
    {
        if (Services.Queue.IsRunning || _toolBusy) return;
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Install tools",
                "viddl will download two free helper programs into its data folder:\n\n" +
                "• ffmpeg (github.com/BtbN/FFmpeg-Builds, about 130 MB) - needed to save MP4 files\n" +
                "• yt-dlp (github.com/yt-dlp/yt-dlp, about 18 MB) - extracts videos from web pages",
                "Install", "Cancel")) return;

        _toolBusy = true;
        _toolCts = new CancellationTokenSource();
        UpdateQueueButtons();
        bool active = true;
        var progress = new Progress<DownloadProgress>(p => { if (active) SetStatus(p.Percent, p.Status); });
        try
        {
            Log("Installing yt-dlp...");
            await Services.Installer.InstallYtDlpAsync(progress, _toolCts.Token);
            Log("Installing ffmpeg...");
            await Services.Installer.InstallFfmpegAsync(progress, _toolCts.Token);
            active = false; // ignore progress messages still on their way to the UI thread
            SetStatus(100, "Tools installed.");
            Log($"Tools installed to {Services.Installer.ToolsDir}");
        }
        catch (OperationCanceledException)
        {
            active = false;
            SetStatus(0, "Cancelled.");
        }
        catch (Exception ex)
        {
            active = false;
            SetStatus(0, "Install failed: " + ex.Message);
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            _toolBusy = false;
            _toolCts.Dispose();
            _toolCts = null;
            UpdateQueueButtons();
            RefreshToolStatus();
        }
    }
}
