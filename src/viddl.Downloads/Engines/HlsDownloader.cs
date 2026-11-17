using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Viddl.Core.Hls;
using Viddl.Core.Models;
using Viddl.Core.Net;
using Viddl.Core.Text;

namespace Viddl.Downloads.Engines;

/// <summary>
/// Downloads HLS (.m3u8) streams: picks a quality from the master playlist, downloads segments in parallel
/// (decrypting standard AES-128), joins them and remuxes to MP4 with ffmpeg without re-encoding.
/// Live playlists are recorded with ffmpeg until cancelled. DRM-protected streams are refused.
/// </summary>
public sealed class HlsDownloader
{
    /// <summary>Share of the progress bar used for downloading segments; converting uses the rest.</summary>
    public const double DownloadShare = 92;

    private readonly DownloadOptions _opt;
    private readonly IProgress<DownloadProgress> _progress;
    private readonly DownloadEnvironment _env;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Uri, byte[]> _keyCache = new();

    // Progress state shared by the parallel segment downloads.
    private readonly ConcurrentDictionary<object, double> _partial = new();
    private Stopwatch _clock = new();
    private long _totalBytes;
    private int _segmentsDone;
    private int _totalSegments;

    // Throttling: when the server pushes back, all workers pause together.
    private long _pauseUntilTicks;
    private int _throttleHits;

    public HlsDownloader(DownloadOptions options, IProgress<DownloadProgress> progress, DownloadEnvironment env)
    {
        _opt = options;
        _progress = progress;
        _env = env;
        _http = HttpClients.Create(options.Referer, options.UserAgent, options.Cookies, env.HttpHandlers);
    }

    public async Task<string> DownloadAsync(CancellationToken ct)
    {
        _env.RequireFfmpeg();
        var playlistUri = new Uri(_opt.Url);
        _progress.Report(new DownloadProgress(null, "Reading playlist..."));
        var playlist = await LoadPlaylistAsync(playlistUri, ct).ConfigureAwait(false);

        Uri? audioUri = null;
        if (playlist.IsMaster)
        {
            var variant = HlsPlaylist.ChooseVariant(playlist.Variants, _opt.MaxHeight);
            _progress.Report(new DownloadProgress(null, $"Selected quality: {variant}"));
            audioUri = playlist.AudioFor(variant);
            playlistUri = variant.Uri;
            playlist = await LoadPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
        }

        var name = Formatting.SafeFileName(_opt.FileName ?? Formatting.GuessName(playlistUri, _env.Now()));
        var output = Formatting.UniquePath(Path.Combine(_opt.OutputFolder, name + ".mp4"));

        if (!playlist.IsVod)
        {
            // Some sites forget the end marker on normal videos. A real live playlist keeps growing,
            // so check again after one segment duration before treating it as live.
            _progress.Report(new DownloadProgress(null, "Playlist has no end marker - checking whether it's live..."));
            await _env.Delay(TimeSpan.FromSeconds(Math.Clamp(playlist.TargetDuration, 2, 6)), ct).ConfigureAwait(false);
            var again = await LoadPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
            bool growing = !again.IsEndList &&
                (again.Segments.Count != playlist.Segments.Count
                 || again.Segments.LastOrDefault()?.Uri != playlist.Segments.LastOrDefault()?.Uri);
            if (growing)
            {
                await RecordLiveAsync(playlistUri, audioUri, output, ct).ConfigureAwait(false);
                return output;
            }
            playlist = again;
        }

        var audioPlaylist = audioUri != null ? await LoadPlaylistAsync(audioUri, ct).ConfigureAwait(false) : null;
        EnsureNoDrm(playlist);
        if (audioPlaylist != null) EnsureNoDrm(audioPlaylist);

        var tempDir = Path.Combine(_env.TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            _totalSegments = playlist.Segments.Count + (audioPlaylist?.Segments.Count ?? 0);
            _clock = Stopwatch.StartNew();
            ReportSegments();

            // Report several times a second (not only when a segment completes) so progress keeps moving
            // even when segments are large.
            using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reporter = Task.Run(async () =>
            {
                while (!reporterCts.IsCancellationRequested)
                {
                    try { await Task.Delay(_env.ProgressInterval, reporterCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    ReportSegments();
                }
            }, CancellationToken.None);

            string videoFile;
            string? audioFile;
            try
            {
                videoFile = await DownloadTrackAsync(playlist, Path.Combine(tempDir, "video"), ct).ConfigureAwait(false);
                audioFile = audioPlaylist != null
                    ? await DownloadTrackAsync(audioPlaylist, Path.Combine(tempDir, "audio"), ct).ConfigureAwait(false)
                    : null;
            }
            finally
            {
                reporterCts.Cancel();
                await reporter.ConfigureAwait(false);
            }
            ReportSegments();

            var args = new List<string> { "-i", videoFile };
            if (audioFile != null) args.AddRange(new[] { "-i", audioFile, "-map", "0:v:0?", "-map", "1:a:0" });
            else args.AddRange(new[] { "-map", "0:v?", "-map", "0:a?" });
            args.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", output });

            await new FfmpegRunner(_env).RunAsync(args, new ScaledProgress(_progress, DownloadShare, 100), ct,
                playlist.TotalDuration, "Converting to MP4").ConfigureAwait(false);
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { }
        }
    }

    private async Task RecordLiveAsync(Uri videoUri, Uri? audioUri, string output, CancellationToken ct)
    {
        _progress.Report(new DownloadProgress(null, "Live stream detected - recording until you press Stop..."));
        var args = new List<string>();
        args.AddRange(FfmpegRunner.HttpInputArgs(_opt));
        args.AddRange(new[] { "-i", videoUri.ToString() });
        if (audioUri != null)
        {
            args.AddRange(FfmpegRunner.HttpInputArgs(_opt));
            args.AddRange(new[] { "-i", audioUri.ToString(), "-map", "0:v:0?", "-map", "1:a:0" });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:v?", "-map", "0:a?" });
        }
        // Fragmented MP4 so the file stays playable even if recording is interrupted.
        args.AddRange(new[] { "-c", "copy", "-movflags", "+frag_keyframe+empty_moov", output });
        await new FfmpegRunner(_env).RunAsync(args, _progress, ct, null, "Recording", gracefulStopOnCancel: true).ConfigureAwait(false);
    }

    private void ReportSegments()
    {
        if (_totalSegments == 0) return;
        int done = Volatile.Read(ref _segmentsDone);
        double frac = Math.Min(1, (done + _partial.Values.Sum()) / _totalSegments);
        long bytes = Interlocked.Read(ref _totalBytes);
        _progress.Report(new DownloadProgress(frac * DownloadShare,
            $"Downloading segments {done}/{_totalSegments} · {bytes / 1048576.0:F1} MB" +
            Formatting.SpeedEta(bytes, _clock.Elapsed, frac)));
    }

    private async Task<string> DownloadTrackAsync(HlsPlaylist pl, string prefix, CancellationToken ct)
    {
        var segFiles = new string[pl.Segments.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pl.Segments.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.Concurrency), CancellationToken = ct },
            async (i, token) =>
            {
                var slot = new object();
                try
                {
                    var data = await FetchSegmentAsync(pl.Segments[i], token, onProgress: f => _partial[slot] = f).ConfigureAwait(false);
                    var path = $"{prefix}_{i:D6}.seg";
                    await File.WriteAllBytesAsync(path, data, token).ConfigureAwait(false);
                    segFiles[i] = path;
                    Interlocked.Increment(ref _segmentsDone);
                }
                finally
                {
                    _partial.TryRemove(slot, out _);
                }
            }).ConfigureAwait(false);

        // Join segments in order, writing the fMP4 init section whenever it changes.
        bool fmp4 = pl.Segments.Any(s => s.InitSection != null);
        var joined = prefix + (fmp4 ? ".mp4" : ".ts");
        await using (var outStream = File.Create(joined))
        {
            HlsSegment? lastInit = null;
            for (int i = 0; i < segFiles.Length; i++)
            {
                var init = pl.Segments[i].InitSection;
                if (init != null && !ReferenceEquals(init, lastInit))
                {
                    var initData = await FetchSegmentAsync(init, ct, sequenceForIv: 0).ConfigureAwait(false);
                    await outStream.WriteAsync(initData, ct).ConfigureAwait(false);
                    lastInit = init;
                }
                await using var segStream = File.OpenRead(segFiles[i]);
                await segStream.CopyToAsync(outStream, ct).ConfigureAwait(false);
            }
        }
        foreach (var f in segFiles) TryDelete(f);
        return joined;
    }

    private async Task<byte[]> FetchSegmentAsync(HlsSegment seg, CancellationToken ct, long? sequenceForIv = null,
        Action<double>? onProgress = null)
    {
        var data = await WithRetry(async () =>
        {
            await WaitIfThrottledAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, seg.Uri);
            if (seg.ByteRange != null)
                req.Headers.Range = new RangeHeaderValue(seg.ByteRange.Offset, seg.ByteRange.Offset + seg.ByteRange.Length - 1);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            CheckThrottle(resp);
            resp.EnsureSuccessStatusCode();

            // Read in chunks so progress moves while a (possibly large) segment is downloading.
            var length = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            long counted = 0;
            try
            {
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    counted += read;
                    Interlocked.Add(ref _totalBytes, read);
                    if (length > 0) onProgress?.Invoke(Math.Min(1, (double)ms.Length / length.Value));
                }
            }
            catch
            {
                Interlocked.Add(ref _totalBytes, -counted); // a retry downloads these bytes again
                onProgress?.Invoke(0);
                throw;
            }
            return ms.ToArray();
        }, ct, attempts: 8).ConfigureAwait(false);

        if (seg.Key is { Method: "AES-128" } key)
        {
            var keyBytes = await GetKeyAsync(key.Uri!, ct).ConfigureAwait(false);
            var iv = key.Iv ?? SequenceIv(sequenceForIv ?? seg.Sequence);
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            data = aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
        }
        return data;
    }

    private async Task<byte[]> GetKeyAsync(Uri uri, CancellationToken ct)
    {
        if (_keyCache.TryGetValue(uri, out var k)) return k;
        var bytes = await WithRetry(() => _http.GetByteArrayAsync(uri, ct), ct).ConfigureAwait(false);
        if (bytes.Length != 16) throw new InvalidDataException("Unexpected AES key length - the stream may be DRM protected.");
        _keyCache[uri] = bytes;
        return bytes;
    }

    /// <summary>The IV HLS uses when none is given: the media sequence number as a 128-bit big-endian integer.</summary>
    internal static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--) { iv[i] = (byte)(sequence & 0xFF); sequence >>= 8; }
        return iv;
    }

    private async Task WaitIfThrottledAsync(CancellationToken ct)
    {
        var wait = new DateTime(Interlocked.Read(ref _pauseUntilTicks), DateTimeKind.Utc) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await _env.Delay(wait, ct).ConfigureAwait(false);
    }

    private void CheckThrottle(HttpResponseMessage resp)
    {
        int code = (int)resp.StatusCode;
        if (code is not (429 or 503 or 403)) return;
        int hits = Interlocked.Increment(ref _throttleHits);
        var delay = resp.Headers.RetryAfter?.Delta
                    ?? (resp.Headers.RetryAfter?.Date is DateTimeOffset d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                    ?? TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(hits, 5))));
        var until = DateTime.UtcNow + delay;
        long current;
        do
        {
            current = Interlocked.Read(ref _pauseUntilTicks);
            if (current >= until.Ticks) break;
        } while (Interlocked.CompareExchange(ref _pauseUntilTicks, until.Ticks, current) != current);
        _progress.Report(new DownloadProgress(null, $"Server is limiting requests (HTTP {code}) - pausing {delay.TotalSeconds:F0}s..."));
    }

    private async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct, int attempts = 5)
    {
        for (int i = 1; ; i++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception) when (i < attempts && !ct.IsCancellationRequested)
            {
                // Exponential back-off with jitter.
                await _env.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, 750 * Math.Pow(2, i - 1)) + Random.Shared.Next(250)), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<HlsPlaylist> LoadPlaylistAsync(Uri uri, CancellationToken ct)
    {
        using var resp = await WithRetry(() => _http.GetAsync(uri, ct), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return HlsPlaylist.Parse(text, resp.RequestMessage?.RequestUri ?? uri); // resolve relative paths after redirects
    }

    private static void EnsureNoDrm(HlsPlaylist pl)
    {
        if (pl.FindDrmKey() is { } key)
            throw new NotSupportedException(
                $"This stream is DRM protected ({key.Method}{(key.KeyFormat != null ? ", " + key.KeyFormat : "")}) and cannot be downloaded.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
