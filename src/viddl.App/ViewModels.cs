using System.ComponentModel;
using Viddl.Core.Browsing;
using Viddl.Core.Streams;
using Viddl.Downloads.Queue;

namespace Viddl.App;

/// <summary>A row in the detected-streams list.</summary>
public sealed class StreamRow : INotifyPropertyChanged
{
    internal StreamRow(SniffedStream stream) => Stream = stream;

    // Internal so XAML's generated type information doesn't try to describe library types.
    internal SniffedStream Stream { get; }

    public string Name => Stream.DisplayName;
    public string Kind => Stream.KindLabel;
    public string Info => Stream.Info;
    public double RowOpacity => Stream.Unsupported ? 0.45 : 1;
    public string Tooltip => $"{Stream.DisplayName}\n{Stream.Uri}\nFound {Stream.FoundAt:HH:mm:ss} on {Stream.PageUrl}";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Call after the underlying stream changed.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A row in the downloads list; mirrors a <see cref="QueueItem"/> for data binding.</summary>
public sealed class QueueRow : INotifyPropertyChanged
{
    internal QueueRow(QueueItem item)
    {
        Item = item;
        item.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
    }

    internal QueueItem Item { get; }

    public string Name => Item.Name;
    public string Url => Item.Url;
    public string Status => Item.Status;
    public double Percent => Item.Percent;
    public string OutputPath => Item.OutputPath ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>An address-bar suggestion (favorite or recently visited site).</summary>
public sealed class SuggestionRow
{
    internal SuggestionRow(Suggestion s)
    {
        Title = s.Title;
        Url = s.Url;
        Glyph = s.Glyph;
    }

    public string Title { get; }
    public string Url { get; }
    public string Glyph { get; }

    public override string ToString() => Url;
}
