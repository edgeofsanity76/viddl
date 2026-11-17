using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Viddl.Core.Net;
using Viddl.Core.Streams;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage;

namespace Viddl.App;

/// <summary>The Preview tab: plays a detected stream, a queued download's source or a saved file.</summary>
public sealed partial class MainWindow
{
    private MediaPlayer? _mediaPlayer;
    private int _previewId;
    private bool _webAvailable = true;

    private void ViewTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ShowView(preview: sender.SelectedItem == PreviewTabItem);

    /// <summary>Shows the browser or the preview. Both stay loaded, so the browser keeps its page.</summary>
    private void ShowView(bool preview)
    {
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = !preview && _webAvailable ? Visibility.Visible : Visibility.Collapsed;
        WebViewMissing.Visibility = !preview && !_webAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!preview) _mediaPlayer?.Pause(); // don't keep playing sound behind the browser
    }

    /// <summary>Opens the request in the Preview tab and starts playing.</summary>
    private async Task PreviewAsync(PreviewRequest request)
    {
        int id = ++_previewId;
        ViewTabs.SelectedItem = PreviewTabItem;
        PreviewTitle.Text = request.Title;
        StopPlayback();
        ShowPreviewMessage("Loading preview...");

        try
        {
            MediaSource? source = request.Kind switch
            {
                PreviewKind.Adaptive => await OpenAdaptiveAsync(request),
                PreviewKind.Progressive => MediaSource.CreateFromUri(new Uri(request.Source)),
                PreviewKind.LocalFile => MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(request.Source)),
                _ => null,
            };
            if (id != _previewId) { source?.Dispose(); return; } // another preview was started meanwhile
            if (source == null)
            {
                ShowPreviewMessage(request.UnsupportedReason);
                return;
            }

            var player = EnsurePlayer();
            player.Source = source;
            player.Play();
            PreviewMessage.Visibility = Visibility.Collapsed;
            StopPreviewButton.IsEnabled = true;
            Log($"Previewing: {request.Title}");
        }
        catch (Exception ex)
        {
            if (id == _previewId) ShowPreviewMessage("Couldn't open the preview: " + ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>HLS/DASH with the site's referer, user agent and cookies, which many CDNs require.</summary>
    private static async Task<MediaSource> OpenAdaptiveAsync(PreviewRequest request)
    {
        var filter = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
        if (!string.IsNullOrEmpty(request.Cookies))
            filter.CookieUsageBehavior = Windows.Web.Http.Filters.HttpCookieUsageBehavior.NoCookies; // send ours as a header
        var http = new Windows.Web.Http.HttpClient(filter);
        var headers = http.DefaultRequestHeaders;
        headers.UserAgent.TryParseAdd(string.IsNullOrWhiteSpace(request.UserAgent) ? HttpClients.DefaultUserAgent : request.UserAgent);
        if (Uri.TryCreate(request.Referer, UriKind.Absolute, out var referer))
        {
            headers.Referer = referer;
            headers.TryAppendWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority));
        }
        if (!string.IsNullOrEmpty(request.Cookies)) headers.TryAppendWithoutValidation("Cookie", request.Cookies);

        var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(request.Source), http);
        if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
        {
            var http_ = result.HttpResponseMessage is { } response ? $" (HTTP {(int)response.StatusCode})" : "";
            throw new InvalidOperationException($"the stream couldn't be opened: {result.Status}{http_}");
        }
        return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_mediaPlayer != null) return _mediaPlayer;
        _mediaPlayer = new MediaPlayer { AutoPlay = true };
        _mediaPlayer.MediaFailed += (_, e) =>
        {
            var message = string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage;
            DispatcherQueue.TryEnqueue(() => ShowPreviewMessage("The preview couldn't be played: " + message));
        };
        Player.SetMediaPlayer(_mediaPlayer);
        return _mediaPlayer;
    }

    private void StopPlayback()
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Pause();
        (_mediaPlayer.Source as IDisposable)?.Dispose();
        _mediaPlayer.Source = null;
        StopPreviewButton.IsEnabled = false;
    }

    private void ShowPreviewMessage(string text)
    {
        PreviewMessage.Text = text;
        PreviewMessage.Visibility = Visibility.Visible;
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        _previewId++;
        StopPlayback();
        PreviewTitle.Text = "Preview";
        ShowPreviewMessage("Preview stopped.");
    }

    // ---- Entry points ----------------------------------------------------------------------
    private async void PreviewStream_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStreams().FirstOrDefault(s => !s.Unsupported) is not { } stream) return;
        var (ua, cookies) = await IdentityAsync(stream.Uri);
        await PreviewAsync(new PreviewRequest(stream.Uri.ToString(), stream.DisplayName, stream.PageUrl, ua, cookies));
    }

    private async void PreviewQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault() is not { } item) return;
        // A finished download plays from disk; anything else plays from its source while it downloads.
        var request = item.OutputPath is { } path && File.Exists(path)
            ? new PreviewRequest(path, item.Name)
            : new PreviewRequest(item.Url, item.Name, item.Options.Referer, item.Options.UserAgent, item.Options.Cookies);
        await PreviewAsync(request);
    }

    private void DisposePreview()
    {
        StopPlayback();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
    }
}
