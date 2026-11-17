using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Viddl.Core.Models;
using Viddl.Core.Text;

namespace Viddl.Downloads.Queue;

public enum QueueItemState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One download in the queue. Raises <see cref="PropertyChanged"/> for data binding.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    private QueueItemState _state;
    private string _status = "Waiting";
    private double _percent;
    private string? _outputPath;

    public required DownloadOptions Options { get; init; }
    public required DownloadEngine Engine { get; init; }
    public required string Name { get; init; }
    public string Url => Options.Url;

    public QueueItemState State { get => _state; internal set => Set(ref _state, value); }

    public string Status { get => _status; internal set => Set(ref _status, value); }

    /// <summary>0-100; only ever increases while the item runs.</summary>
    public double Percent { get => _percent; internal set => Set(ref _percent, value); }

    public string? OutputPath { get => _outputPath; internal set => Set(ref _outputPath, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Result of a queue run.</summary>
public sealed record QueueSummary(int Saved, int Failed, bool StoppedWithItemsWaiting)
{
    public override string ToString() =>
        $"Finished: {Saved} saved" + (Failed > 0 ? $", {Failed} failed" : "") +
        (StoppedWithItemsWaiting ? " (stopped - others still waiting)" : "");
}

/// <summary>
/// Runs downloads one after another. UI-independent: progress and state changes are delivered on the
/// synchronization context that called <see cref="RunAsync"/> (the UI thread in the app).
/// </summary>
public sealed class DownloadQueue
{
    private readonly IDownloader _downloader;
    private CancellationTokenSource? _cts;
    private bool _stopRequested;
    private int _runId;

    public DownloadQueue(IDownloader downloader) => _downloader = downloader;

    public ObservableCollection<QueueItem> Items { get; } = new();

    public bool IsRunning { get; private set; }

    public QueueItem? Current { get; private set; }

    public int WaitingCount => Items.Count(i => i.State == QueueItemState.Waiting);

    /// <summary>Raised when <see cref="IsRunning"/> or the number of waiting items changes.</summary>
    public event Action? StateChanged;

    /// <summary>Overall progress of the running item (percent may be null = unknown).</summary>
    public event Action<QueueItem, DownloadProgress>? Progress;

    public event Action<string>? Log;

    public event Action<QueueItem>? ItemFinished;

    public event Action<QueueSummary>? Finished;

    /// <summary>Adds a download. Returns null if the same URL is already waiting or running.</summary>
    public QueueItem? Enqueue(DownloadOptions options, DownloadEngine engine, string name)
    {
        if (Items.Any(i => i.Url == options.Url && i.State is QueueItemState.Waiting or QueueItemState.Running)) return null;
        var item = new QueueItem { Options = options, Engine = engine, Name = name };
        Items.Add(item);
        Log?.Invoke($"Queued: {name} - {options.Url}");
        StateChanged?.Invoke();
        return item;
    }

    /// <summary>Removes items that aren't running.</summary>
    public void Remove(IEnumerable<QueueItem> items)
    {
        foreach (var i in items.Where(i => i.State != QueueItemState.Running).ToList()) Items.Remove(i);
        StateChanged?.Invoke();
    }

    public void ClearFinished() => Remove(Items.Where(i => i.State == QueueItemState.Done).ToList());

    /// <summary>Puts failed or cancelled items back in the queue.</summary>
    public void Retry(IEnumerable<QueueItem> items)
    {
        foreach (var i in items.Where(i => i.State is QueueItemState.Failed or QueueItemState.Cancelled))
        {
            i.Percent = 0;
            i.State = QueueItemState.Waiting;
            i.Status = "Waiting";
        }
        StateChanged?.Invoke();
    }

    /// <summary>Cancels the running download and stops after it; waiting items stay queued.</summary>
    public void Stop()
    {
        _stopRequested = true;
        _cts?.Cancel();
    }

    /// <summary>Runs waiting items until none are left or <see cref="Stop"/> is called. Does nothing if already running.</summary>
    public async Task<QueueSummary?> RunAsync()
    {
        if (IsRunning) return null;
        IsRunning = true;
        _stopRequested = false;
        StateChanged?.Invoke();
        int saved = 0, failed = 0;
        try
        {
            while (!_stopRequested && Items.FirstOrDefault(i => i.State == QueueItemState.Waiting) is { } item)
            {
                await RunItemAsync(item);
                if (item.State == QueueItemState.Done) saved++;
                else if (item.State == QueueItemState.Failed) failed++;
            }
        }
        finally
        {
            IsRunning = false;
            Current = null;
            StateChanged?.Invoke();
        }

        if (saved + failed == 0) return null;
        var summary = new QueueSummary(saved, failed, WaitingCount > 0);
        Log?.Invoke(summary.ToString());
        Finished?.Invoke(summary);
        return summary;
    }

    private async Task RunItemAsync(QueueItem item)
    {
        var context = SynchronizationContext.Current;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var runId = ++_runId;
        Current = item;
        item.Percent = 0;
        item.State = QueueItemState.Running;
        item.Status = "Starting...";
        Log?.Invoke($"=== {item.Name}: {item.Url}");

        void OnProgress(DownloadProgress p)
        {
            if (runId != _runId || item.State != QueueItemState.Running) return; // late report from a finished item
            double? pct = null;
            if (p.Percent is double v)
            {
                item.Percent = Math.Max(item.Percent, Math.Clamp(v, 0, 100)); // never move backwards
                pct = item.Percent;
            }
            else if (item.Percent > 0)
            {
                pct = item.Percent; // keep the bar where it was rather than switching to "unknown"
            }
            item.Status = (pct is double x ? $"{x:F0}%  ·  " : "") + p.Status;
            Progress?.Invoke(item, p with { Percent = pct });
        }

        var progress = new InlineProgress<DownloadProgress>(p =>
        {
            if (context != null) context.Post(_ => OnProgress(p), null);
            else OnProgress(p);
        });
        void LogLine(string line)
        {
            if (context != null) context.Post(_ => Log?.Invoke(line), null);
            else Log?.Invoke(line);
        }

        try
        {
            var path = await _downloader.DownloadAsync(item.Options, item.Engine, progress, LogLine, cts.Token);
            item.OutputPath = path;
            item.Percent = 100;
            item.State = QueueItemState.Done;
            item.Status = "Done" + (File.Exists(path) ? $"  ·  {Formatting.FileSize(new FileInfo(path).Length)}" : "");
            Log?.Invoke($"Saved: {path}");
        }
        catch (OperationCanceledException)
        {
            item.State = QueueItemState.Cancelled;
            item.Status = "Cancelled";
            Log?.Invoke($"Cancelled: {item.Name}");
        }
        catch (PageBlockedException ex)
        {
            item.State = QueueItemState.Failed;
            item.Status = $"Website refused the request (HTTP {ex.StatusCode})";
            Log?.Invoke("ERROR: " + ex.Message);
        }
        catch (Exception ex)
        {
            item.State = QueueItemState.Failed;
            item.Status = "Failed: " + ex.Message.Split('\n')[0].Trim();
            Log?.Invoke("ERROR: " + ex.Message);
        }
        finally
        {
            _runId++; // invalidate progress still queued for this item
            _cts = null;
        }
        ItemFinished?.Invoke(item);
    }
}
