namespace Viddl.Core.Blocking;

/// <summary>A downloadable list: where to fetch it and the local file it is stored in.</summary>
public sealed record BlocklistSource(string Url, string FileName);

/// <summary>
/// A set of blocked domains made from a built-in list plus hosts-format files
/// ("0.0.0.0 domain", "127.0.0.1 domain" or just "domain"; '#' starts a comment).
/// A host is blocked when it or any parent domain is on the list.
/// </summary>
public sealed class DomainBlocklist
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost", "0.0.0.0", "127.0.0.1",
    };

    private readonly string[] _builtIn;
    private readonly string _dataDir;
    private readonly IReadOnlyList<BlocklistSource> _sources;
    private readonly string? _customFile;
    private HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="builtIn">Domains that are always blocked.</param>
    /// <param name="dataDir">Folder holding the downloaded and custom list files.</param>
    /// <param name="sources">Lists that <see cref="UpdateAsync"/> downloads.</param>
    /// <param name="customFileName">Optional user-editable list in <paramref name="dataDir"/>.</param>
    public DomainBlocklist(IEnumerable<string> builtIn, string dataDir, IReadOnlyList<BlocklistSource> sources, string? customFileName)
    {
        _builtIn = builtIn.Where(d => !d.Contains('/')).ToArray();
        _dataDir = dataDir;
        _sources = sources;
        _customFile = customFileName == null ? null : Path.Combine(dataDir, customFileName);
        Reload();
    }

    public int Count => _domains.Count;

    /// <summary>Full path of the user-editable list (may not exist yet).</summary>
    public string? CustomListPath => _customFile;

    /// <summary>Re-reads all list files.</summary>
    public void Reload()
    {
        var set = new HashSet<string>(_builtIn, StringComparer.OrdinalIgnoreCase);
        var files = _sources.Select(s => Path.Combine(_dataDir, s.FileName)).ToList();
        if (_customFile != null) files.Add(_customFile);
        foreach (var path in files)
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                    if (ParseLine(line) is { } domain) set.Add(domain);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked or unreadable list must never break browsing.
            }
        }
        _domains = set;
    }

    /// <summary>Extracts the domain from one hosts-file line, or null for comments/blank/invalid lines.</summary>
    public static string? ParseLine(string raw)
    {
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0) return null;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var domain = parts[^1].Trim().TrimStart('.').ToLowerInvariant();
        if (IgnoredNames.Contains(domain) || !domain.Contains('.')) return null;
        if (domain.All(c => char.IsDigit(c) || c == '.')) return null; // bare IP address
        return domain;
    }

    public bool IsBlocked(Uri uri)
    {
        var host = uri.Host;
        var domains = _domains;
        while (true)
        {
            if (domains.Contains(host)) return true;
            int dot = host.IndexOf('.');
            if (dot < 0 || host.IndexOf('.', dot + 1) < 0) return false; // stop at "example.com"
            host = host[(dot + 1)..];
        }
    }

    public bool IsBlocked(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && IsBlocked(u);

    /// <summary>
    /// Downloads every source list and reloads. Lists that fail are skipped; throws only if all fail.
    /// </summary>
    /// <param name="validate">Optional check on the downloaded text (return false to reject it).</param>
    public async Task<int> UpdateAsync(HttpClient http, Func<string, bool>? validate = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dataDir);
        int ok = 0;
        Exception? last = null;
        foreach (var source in _sources)
        {
            try
            {
                var text = await http.GetStringAsync(source.Url, ct).ConfigureAwait(false);
                if (validate != null && !validate(text))
                    throw new InvalidDataException($"The list from {source.Url} doesn't look like a hosts file.");
                var target = Path.Combine(_dataDir, source.FileName);
                await File.WriteAllTextAsync(target + ".tmp", text, ct).ConfigureAwait(false);
                File.Move(target + ".tmp", target, overwrite: true);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        if (ok == 0 && last != null) throw last;
        Reload();
        return Count;
    }
}
