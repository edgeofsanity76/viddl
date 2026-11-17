using Viddl.Core.Hls;
using Viddl.Core.Net;
using Viddl.Core.Text;

namespace Viddl.Core.Streams;

/// <summary>A short description of an HLS playlist for the detected-streams list.</summary>
/// <param name="Info">"1080p · 720p", "12:34", "LIVE", "DRM protected - can't download"...</param>
/// <param name="Unsupported">True for DRM-protected streams.</param>
/// <param name="ChildKeys">For a master playlist: keys of its variant and audio playlists.</param>
public sealed record HlsDescription(string Info, bool Unsupported, IReadOnlyList<string> ChildKeys)
{
    public static HlsDescription Describe(HlsPlaylist pl)
    {
        if (pl.IsMaster)
        {
            var heights = pl.Variants.Where(v => v.Height > 0).Select(v => v.Height).Distinct().OrderByDescending(h => h).ToList();
            var info = heights.Count > 0 ? string.Join(" · ", heights.Select(h => $"{h}p")) : $"{pl.Variants.Count} qualities";
            var children = pl.Variants.Select(v => SniffedStream.KeyOf(v.Uri))
                .Concat(pl.AudioRenditions.Where(a => a.Uri != null).Select(a => SniffedStream.KeyOf(a.Uri!)))
                .ToList();
            return new HlsDescription(info, false, children);
        }
        if (pl.FindDrmKey() != null) return new HlsDescription("DRM protected - can't download", true, Array.Empty<string>());
        if (!pl.IsVod) return new HlsDescription("LIVE", false, Array.Empty<string>());
        var d = TimeSpan.FromSeconds(pl.TotalDuration);
        return new HlsDescription(d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"), false, Array.Empty<string>());
    }

    /// <summary>Fetches and describes a playlist; returns a plain "HLS" description if that fails.</summary>
    public static async Task<HlsDescription> ProbeAsync(Uri uri, string? referer, string? userAgent, string? cookies,
        HttpHandlerFactory? handlerFactory = null, CancellationToken ct = default)
    {
        try
        {
            using var http = HttpClients.Create(referer, userAgent, cookies, handlerFactory);
            http.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await http.GetAsync(uri, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Describe(HlsPlaylist.Parse(text, resp.RequestMessage?.RequestUri ?? uri));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException or FormatException)
        {
            return new HlsDescription("HLS", false, Array.Empty<string>());
        }
    }
}
