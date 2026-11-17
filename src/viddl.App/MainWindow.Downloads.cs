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
    private void WireStreams()
    {
        _catalog.Added += s =>
        {
            var row = new StreamRow(s);
            _rows[s] = row;
            // The catalog keeps the list best quality first; put the row where it belongs.
            StreamRows.Insert(Math.Min(_catalog.Items.ToList().IndexOf(s), StreamRows.Count), row);
            if (StreamList.SelectedItems.Count == 0) StreamList.SelectedItem = row;
            UpdateStreamsHeader();
        };
        _catalog.Reordered += SyncStreamOrder;
        _catalog.Removed += s =>
        {
            if (_rows.Remove(s, out var row)) StreamRows.Remove(row);
            UpdateStreamsHeader();
        };
        _catalog.Changed += s =>
        {
            if (_rows.TryGetValue(s, out var row)) row.Refresh();
            if (SelectedStreams().ToList() is [var only] && only == s) OnStreamSelectionChanged();
        };
        _catalog.Cleared += () =>
        {
            _rows.Clear();
            StreamRows.Clear();
            UpdateStreamsHeader();
        };
    }

    /// <summary>Moves rows to match the catalog's order (moving keeps the selection, unlike rebuilding).</summary>
    private void SyncStreamOrder()
    {
        var order = _catalog.Items;
        for (int i = 0; i < order.Count && i < StreamRows.Count; i++)
        {
            if (!_rows.TryGetValue(order[i], out var row)) continue;
            int current = StreamRows.IndexOf(row);
            if (current >= 0 && current != i) StreamRows.Move(current, i);
        }
    }

    private void UpdateStreamsHeader()
    {
        StreamsHeader.Text = StreamRows.Count == 0 ? "Detected streams" : $"Detected streams ({StreamRows.Count})";
        StreamsHint.Visibility = StreamRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<SniffedStream> SelectedStreams() => StreamList.SelectedItems.OfType<StreamRow>().Select(r => r.Stream);

    private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnStreamSelectionChanged();

    private void OnStreamSelectionChanged()
    {
        var sel = SelectedStreams().ToList();
        DownloadSelectedButton.IsEnabled = sel.Any(s => !s.Unsupported);
        DownloadSelectedText.Text = sel.Count > 1 ? $"Download {sel.Count} selected" : "Download selected";
        CopyUrlButton.IsEnabled = sel.Count == 1;
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
        StopButton.IsEnabled = q.IsRunning || _toolBusy;
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
        PlayItem.IsEnabled = ShowItem.IsEnabled = sel.Any(i => i.OutputPath != null && File.Exists(i.OutputPath));
        RetryItem.IsEnabled = sel.Any(i => i.State is QueueItemState.Failed or QueueItemState.Cancelled);
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
