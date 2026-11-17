using Viddl.Core.Models;
using Viddl.Core.Text;

namespace Viddl.Core.Streams;

/// <summary>A video stream seen in the browser's network traffic.</summary>
public sealed class SniffedStream
{
    public required Uri Uri { get; init; }
    public required StreamKind Kind { get; init; }
    public required string PageUrl { get; init; }
    public string? Title { get; set; }
    public string Info { get; set; } = "checking…";
    public bool Unsupported { get; set; }
    public long? SizeBytes { get; set; }
    public string? CustomName { get; set; }
    public DateTime FoundAt { get; init; } = DateTime.Now;

    /// <summary>Best known video height (from the playlist, or a hint in the address); null if unknown.</summary>
    public int? Height { get; set; }

    /// <summary>"1080p", or empty when the quality isn't known.</summary>
    public string QualityLabel => Height is int h ? $"{h}p" : "";

    /// <summary>Order of detection (higher = newer); breaks ties when sorting.</summary>
    internal long Sequence { get; init; }

    /// <summary>Identity ignoring the query string (players re-fetch playlists with fresh tokens).</summary>
    public string Key => KeyOf(Uri);

    public static string KeyOf(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomName) ? CustomName! :
        !string.IsNullOrWhiteSpace(Title) ? Title! :
        Uri.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } last ? last : Uri.Host;

    public string KindLabel => Kind switch { StreamKind.Hls => "HLS", StreamKind.Dash => "DASH", _ => "File" };
}

/// <summary>
/// The list of detected streams: de-duplicates repeated requests, folds the per-quality child playlists of an
/// HLS master into the master, keeps titles up to date and keeps the list sorted best quality first.
/// UI-independent; the view listens to the events.
/// </summary>
public sealed class StreamCatalog
{
    private readonly List<SniffedStream> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;
    private long _sequence;

    public StreamCatalog(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>Best quality first (see <see cref="CompareQuality"/>).</summary>
    public IReadOnlyList<SniffedStream> Items => _items;

    public event Action<SniffedStream>? Added;
    public event Action<SniffedStream>? Removed;
    public event Action<SniffedStream>? Changed;
    public event Action? Cleared;

    /// <summary>Raised when the order of <see cref="Items"/> changed.</summary>
    public event Action? Reordered;

    /// <summary>
    /// Sort order for the list: supported streams first, then the highest resolution, then adaptive streams
    /// (HLS/DASH, usually the main video) before plain files, then the largest file, then the newest.
    /// </summary>
    public static int CompareQuality(SniffedStream a, SniffedStream b)
    {
        int c = a.Unsupported.CompareTo(b.Unsupported);
        if (c != 0) return c;
        c = (b.Height ?? 0).CompareTo(a.Height ?? 0);
        if (c != 0) return c;
        c = (a.Kind == StreamKind.Direct).CompareTo(b.Kind == StreamKind.Direct);
        if (c != 0) return c;
        c = (b.SizeBytes ?? 0).CompareTo(a.SizeBytes ?? 0);
        if (c != 0) return c;
        return b.Sequence.CompareTo(a.Sequence);
    }

    private void Sort()
    {
        var before = _items.ToList();
        _items.Sort(CompareQuality);
        if (!before.SequenceEqual(_items)) Reordered?.Invoke();
    }

    /// <summary>
    /// Handles a network response. Returns the new stream (so the caller can probe it), or null if the response
    /// isn't a new stream.
    /// </summary>
    public SniffedStream? OnResponse(string url, string? contentType, int statusCode, string pageUrl, string? title,
        string? contentRange = null, string? contentLength = null)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || statusCode >= 400) return null;
        var kind = StreamClassifier.ClassifyResponse(url, contentType);
        if (kind == null) return null;

        var uri = new Uri(url);
        var key = SniffedStream.KeyOf(uri);
        if (_children.Contains(key) || !_seen.Add(key)) return null;

        var s = new SniffedStream
        {
            Uri = uri, Kind = kind.Value, PageUrl = pageUrl, Title = title, FoundAt = _now(),
            Sequence = ++_sequence, Height = StreamClassifier.HeightFromUrl(url),
        };
        if (kind == StreamKind.Direct)
        {
            s.SizeBytes = StreamClassifier.TotalSize(contentRange, contentLength, statusCode);
            s.Info = s.SizeBytes is long b ? Formatting.FileSize(b) : "video file";
        }
        else if (kind == StreamKind.Dash)
        {
            s.Info = "DASH";
        }
        _items.Add(s);
        _items.Sort(CompareQuality);
        Added?.Invoke(s);
        return s;
    }

    /// <summary>Applies what probing an HLS playlist found out.</summary>
    public void ApplyDescription(SniffedStream s, HlsDescription d)
    {
        s.Info = d.Info;
        s.Unsupported = d.Unsupported;
        if (d.MaxHeight is int h && h > (s.Height ?? 0)) s.Height = h;
        foreach (var childKey in d.ChildKeys)
        {
            _children.Add(childKey);
            var child = _items.FirstOrDefault(i => string.Equals(i.Key, childKey, StringComparison.OrdinalIgnoreCase));
            if (child != null && !ReferenceEquals(child, s))
            {
                _items.Remove(child);
                Removed?.Invoke(child);
            }
        }
        if (!_items.Contains(s)) return;
        Changed?.Invoke(s);
        Sort();
    }

    /// <summary>Gives streams found before the page title loaded their proper title.</summary>
    public void UpdateTitles(string pageUrl, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        foreach (var s in _items.Where(i => i.PageUrl == pageUrl && string.IsNullOrWhiteSpace(i.CustomName) && i.Title != title))
        {
            s.Title = title;
            Changed?.Invoke(s);
        }
    }

    public void Rename(SniffedStream s, string? name)
    {
        s.CustomName = name;
        Changed?.Invoke(s);
    }

    /// <summary>Removes a stream; it can be detected again if the page requests it again.</summary>
    public void Remove(SniffedStream s)
    {
        if (!_items.Remove(s)) return;
        _seen.Remove(s.Key);
        Removed?.Invoke(s);
    }

    /// <summary>Forgets everything (e.g. when a new page loads).</summary>
    public void Clear()
    {
        _items.Clear();
        _seen.Clear();
        _children.Clear();
        Cleared?.Invoke();
    }
}
