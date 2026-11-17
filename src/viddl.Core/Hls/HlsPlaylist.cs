using System.Globalization;
using System.Text.RegularExpressions;

namespace Viddl.Core.Hls;

public sealed class HlsVariant
{
    public required Uri Uri { get; init; }
    public long Bandwidth { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? AudioGroup { get; init; }

    public override string ToString() =>
        Height > 0 ? $"{Height}p ({Bandwidth / 1000} kbps)" : $"{Bandwidth / 1000} kbps";
}

public sealed class HlsAudioRendition
{
    public required string GroupId { get; init; }
    public Uri? Uri { get; init; }
    public string? Name { get; init; }
    public string? Language { get; init; }
    public bool IsDefault { get; init; }
}

public sealed class HlsKey
{
    /// <summary>NONE, AES-128, SAMPLE-AES, ...</summary>
    public required string Method { get; init; }
    public Uri? Uri { get; init; }
    public byte[]? Iv { get; init; }
    public string? KeyFormat { get; init; }

    /// <summary>Plain AES-128 with a key from the server (not DRM).</summary>
    public bool IsStandardAes => Method == "AES-128" && (KeyFormat == null || KeyFormat == "identity");
}

public sealed class HlsByteRange
{
    public long Length { get; init; }
    public long Offset { get; init; }
}

public sealed class HlsSegment
{
    public required Uri Uri { get; init; }
    public double Duration { get; init; }
    public long Sequence { get; init; }
    public HlsKey? Key { get; init; }
    public HlsByteRange? ByteRange { get; init; }

    /// <summary>EXT-X-MAP initialization section (fragmented MP4 streams).</summary>
    public HlsSegment? InitSection { get; init; }
}

/// <summary>Parser for HLS (.m3u8) master and media playlists.</summary>
public sealed class HlsPlaylist
{
    private static readonly Regex AttrRegex = new(@"([A-Z0-9\-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

    public bool IsMaster => Variants.Count > 0;
    public List<HlsVariant> Variants { get; } = new();
    public List<HlsAudioRendition> AudioRenditions { get; } = new();
    public List<HlsSegment> Segments { get; } = new();
    public bool IsEndList { get; private set; }

    /// <summary>EXT-X-PLAYLIST-TYPE: VOD, EVENT or null.</summary>
    public string? PlaylistType { get; private set; }

    public double TargetDuration { get; private set; }
    public double TotalDuration => Segments.Sum(s => s.Duration);

    /// <summary>A finished video (as opposed to a live stream that keeps growing).</summary>
    public bool IsVod => IsEndList || PlaylistType == "VOD";

    public static Dictionary<string, string> ParseAttributes(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex.Matches(s))
            dict[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return dict;
    }

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        var pl = new HlsPlaylist();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U"))
            throw new InvalidDataException("Not an HLS playlist (missing #EXTM3U).");

        long sequence = 0;
        double pendingDuration = 0;
        HlsKey? currentKey = null;
        HlsSegment? currentMap = null;
        HlsByteRange? pendingRange = null;
        long lastRangeEnd = 0;
        Dictionary<string, string>? pendingStreamInf = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                pendingStreamInf = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var a = ParseAttributes(line["#EXT-X-MEDIA:".Length..]);
                if (a.TryGetValue("TYPE", out var type) && type == "AUDIO")
                {
                    pl.AudioRenditions.Add(new HlsAudioRendition
                    {
                        GroupId = a.GetValueOrDefault("GROUP-ID") ?? "",
                        Uri = a.TryGetValue("URI", out var u) ? new Uri(baseUri, u) : null,
                        Name = a.GetValueOrDefault("NAME"),
                        Language = a.GetValueOrDefault("LANGUAGE"),
                        IsDefault = a.GetValueOrDefault("DEFAULT") == "YES",
                    });
                }
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], out sequence);
            }
            else if (line.StartsWith("#EXTINF:"))
            {
                var v = line["#EXTINF:".Length..].Split(',')[0];
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
            }
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var a = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                var method = a.GetValueOrDefault("METHOD") ?? "NONE";
                currentKey = method == "NONE" ? null : new HlsKey
                {
                    Method = method,
                    Uri = a.TryGetValue("URI", out var u) ? new Uri(baseUri, u) : null,
                    Iv = a.TryGetValue("IV", out var iv) ? ParseHex(iv) : null,
                    KeyFormat = a.GetValueOrDefault("KEYFORMAT"),
                };
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                currentMap = new HlsSegment
                {
                    Uri = new Uri(baseUri, a["URI"]),
                    ByteRange = a.TryGetValue("BYTERANGE", out var br) ? ParseRange(br, 0) : null,
                    Key = currentKey,
                };
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                pendingRange = ParseRange(line["#EXT-X-BYTERANGE:".Length..], lastRangeEnd);
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:"))
            {
                pl.PlaylistType = line["#EXT-X-PLAYLIST-TYPE:".Length..].Trim().ToUpperInvariant();
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:"))
            {
                double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var td);
                pl.TargetDuration = td;
            }
            else if (line.StartsWith("#EXT-X-ENDLIST"))
            {
                pl.IsEndList = true;
            }
            else if (!line.StartsWith('#'))
            {
                var uri = new Uri(baseUri, line);
                if (pendingStreamInf != null)
                {
                    var res = pendingStreamInf.GetValueOrDefault("RESOLUTION")?.Split('x');
                    pl.Variants.Add(new HlsVariant
                    {
                        Uri = uri,
                        Bandwidth = long.TryParse(pendingStreamInf.GetValueOrDefault("BANDWIDTH"), out var bw) ? bw : 0,
                        Width = res?.Length == 2 && int.TryParse(res[0], out var w) ? w : 0,
                        Height = res?.Length == 2 && int.TryParse(res[1], out var h) ? h : 0,
                        AudioGroup = pendingStreamInf.GetValueOrDefault("AUDIO"),
                    });
                    pendingStreamInf = null;
                }
                else
                {
                    pl.Segments.Add(new HlsSegment
                    {
                        Uri = uri,
                        Duration = pendingDuration,
                        Sequence = sequence++,
                        Key = currentKey,
                        ByteRange = pendingRange,
                        InitSection = currentMap,
                    });
                    if (pendingRange != null) lastRangeEnd = pendingRange.Offset + pendingRange.Length;
                    pendingRange = null;
                    pendingDuration = 0;
                }
            }
        }
        return pl;
    }

    /// <summary>Picks a variant: null = best, 0 = lowest, otherwise the best at or below that height.</summary>
    public static HlsVariant ChooseVariant(IReadOnlyList<HlsVariant> variants, int? maxHeight)
    {
        if (variants.Count == 0) throw new ArgumentException("No variants to choose from.", nameof(variants));
        var ordered = variants.OrderBy(v => v.Height).ThenBy(v => v.Bandwidth).ToList();
        if (maxHeight == 0) return ordered[0];
        if (maxHeight is int max)
            return ordered.LastOrDefault(v => v.Height > 0 && v.Height <= max) ?? ordered[0];
        return ordered[^1];
    }

    /// <summary>The first key that is DRM (anything other than standard AES-128), or null.</summary>
    public HlsKey? FindDrmKey() => Segments.Select(s => s.Key).FirstOrDefault(k => k != null && !k.IsStandardAes);

    /// <summary>The audio rendition to use with a variant (default one first), or null.</summary>
    public Uri? AudioFor(HlsVariant variant)
    {
        if (variant.AudioGroup == null) return null;
        var group = AudioRenditions.Where(a => a.GroupId == variant.AudioGroup && a.Uri != null).ToList();
        return (group.FirstOrDefault(a => a.IsDefault) ?? group.FirstOrDefault())?.Uri;
    }

    private static HlsByteRange ParseRange(string s, long defaultOffset)
    {
        var parts = s.Split('@');
        return new HlsByteRange
        {
            Length = long.Parse(parts[0], CultureInfo.InvariantCulture),
            Offset = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : defaultOffset,
        };
    }

    private static byte[] ParseHex(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return Convert.FromHexString(hex.PadLeft(32, '0'));
    }
}
