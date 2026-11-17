namespace Viddl.Core.Models;

/// <summary>Progress report shared by all download engines.</summary>
/// <param name="Percent">0-100, or null when unknown.</param>
/// <param name="Status">Short human-readable status line.</param>
public sealed record DownloadProgress(double? Percent, string Status);

/// <summary>Maps a sub-task's 0-100% onto a slice of the overall progress (e.g. converting = 92-100%).</summary>
public sealed class ScaledProgress : IProgress<DownloadProgress>
{
    private readonly IProgress<DownloadProgress> _inner;
    private readonly double _from;
    private readonly double _to;

    public ScaledProgress(IProgress<DownloadProgress> inner, double from, double to)
    {
        _inner = inner;
        _from = from;
        _to = to;
    }

    public void Report(DownloadProgress value) =>
        _inner.Report(value with
        {
            Percent = value.Percent is double v ? _from + (_to - _from) * Math.Clamp(v, 0, 100) / 100 : null,
        });
}

/// <summary>An <see cref="IProgress{T}"/> that invokes its callback immediately on the reporting thread.</summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    public InlineProgress(Action<T> callback) => _callback = callback;

    public void Report(T value) => _callback(value);
}
