using Viddl.Core.Blocking;
using Viddl.Core.Settings;

namespace Viddl.Core.Browsing;

/// <summary>Suspicious things a page can do, used to decide whether a site needs strict mode.</summary>
public enum ThreatSignal
{
    MaliciousRequest,
    AutomaticRedirect,
    PopUp,
    PageDialog,
    ExternalProgram,
    PageDownload,
    PermissionRequest,
    Ad,
}

/// <summary>Strict mode was switched on for <paramref name="Site"/> because of <paramref name="Reason"/>.</summary>
public sealed record StrictDecision(string Site, string Reason);

/// <summary>
/// Scores each site by what its pages try to do. When a site's score reaches <see cref="Threshold"/>, strict mode is
/// switched on for that site (recorded in <see cref="AppSettings.AutoStrictSites"/>). A single pop-up or a few ads
/// aren't enough; repeated redirects, malicious requests or attempts to start downloads or programs are.
/// </summary>
public sealed class StrictModeAdvisor
{
    public const double Threshold = 5;

    private static readonly Dictionary<ThreatSignal, (double Weight, string Phrase)> Signals = new()
    {
        [ThreatSignal.MaliciousRequest] = (3, "loaded content from known malicious sites"),
        [ThreatSignal.AutomaticRedirect] = (3, "tried to send you to other sites"),
        [ThreatSignal.ExternalProgram] = (3, "tried to open other programs"),
        [ThreatSignal.PageDownload] = (3, "started downloads by itself"),
        [ThreatSignal.PopUp] = (1.5, "opened pop-ups"),
        [ThreatSignal.PageDialog] = (1.5, "showed page dialogs"),
        [ThreatSignal.PermissionRequest] = (1, "asked for permissions"),
        [ThreatSignal.Ad] = (0.1, "showed lots of ads"),
    };

    private readonly AppSettings _settings;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Dictionary<ThreatSignal, int>> _counts = new(StringComparer.OrdinalIgnoreCase);

    public StrictModeAdvisor(AppSettings settings, Func<DateTime>? now = null)
    {
        _settings = settings;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Current score of a site this session.</summary>
    public double Score(string site) =>
        _counts.TryGetValue(site, out var c) ? c.Sum(kv => Signals[kv.Key].Weight * kv.Value) : 0;

    /// <summary>
    /// Records a signal from a page. Returns a decision the first time the page's site crosses the threshold
    /// (strict mode is then on for that site); otherwise null.
    /// </summary>
    public StrictDecision? Record(string? pageUrl, ThreatSignal signal)
    {
        var site = SiteKey.SiteOfUrl(pageUrl);
        if (site == null) return null;
        if (!_counts.TryGetValue(site, out var counts)) _counts[site] = counts = new Dictionary<ThreatSignal, int>();
        counts[signal] = counts.GetValueOrDefault(signal) + 1;

        // Nothing to decide if strict mode already applies, detection is off, or the user allowed this site.
        if (!_settings.AutoStrict || _settings.StrictScripts || _settings.IsStrictAllowed(site) || _settings.FindAutoStrict(site) != null)
            return null;
        if (Score(site) < Threshold) return null;

        var reason = Describe(counts);
        _settings.AddAutoStrict(site, reason, _now());
        return new StrictDecision(site, reason);
    }

    /// <summary>Forgets a site's score (e.g. after the user undoes an automatic decision).</summary>
    public void Forget(string site) => _counts.Remove(site);

    /// <summary>"It tried to send you to other sites and opened pop-ups" - the biggest contributors first.</summary>
    internal static string Describe(IReadOnlyDictionary<ThreatSignal, int> counts)
    {
        var phrases = counts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => Signals[kv.Key].Weight * kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(3)
            .Select(kv => Signals[kv.Key].Phrase)
            .ToList();
        var list = phrases.Count switch
        {
            0 => "behaved suspiciously",
            1 => phrases[0],
            _ => string.Join(", ", phrases.Take(phrases.Count - 1)) + " and " + phrases[^1],
        };
        return "It " + list;
    }
}
