using Viddl.Core.Blocking;
using Viddl.Core.Settings;

namespace Viddl.Core.Browsing;

/// <summary>Why a request or navigation was blocked (null = allowed).</summary>
public enum BlockReason
{
    MaliciousDomain,
    ThirdPartyScript,
    AutomaticRedirect,
    Ad,
}

/// <summary>Whether strict mode applies to a page, and why.</summary>
public enum StrictState
{
    /// <summary>Strict mode doesn't apply.</summary>
    Off,

    /// <summary>On because strict mode is enabled for every site.</summary>
    Everywhere,

    /// <summary>On for this site because it behaved suspiciously.</summary>
    Detected,

    /// <summary>Would apply, but the user allowed third-party scripts on this site.</summary>
    AllowedHere,
}

/// <summary>
/// The browser's blocking decisions, independent of the browser control so they can be tested.
/// </summary>
public sealed class BrowserProtection
{
    private readonly DomainBlocklist _ads;
    private readonly DomainBlocklist _malware;
    private readonly AppSettings _settings;

    public BrowserProtection(DomainBlocklist ads, DomainBlocklist malware, AppSettings settings)
    {
        _ads = ads;
        _malware = malware;
        _settings = settings;
    }

    public bool ProtectionOn => _settings.Protection;
    public bool StrictOn => _settings.StrictScripts;
    public bool AdBlockOn => _settings.AdBlock;

    /// <summary>Whether strict mode applies to the page, and why.</summary>
    public StrictState StrictStateFor(string? pageUrl)
    {
        var site = SiteKey.SiteOfUrl(pageUrl);
        bool detected = site != null && _settings.AutoStrict && _settings.FindAutoStrict(site) != null;
        if (!_settings.StrictScripts && !detected) return StrictState.Off;
        if (site != null && _settings.IsStrictAllowed(site)) return StrictState.AllowedHere;
        return _settings.StrictScripts ? StrictState.Everywhere : StrictState.Detected;
    }

    /// <summary>True when strict mode applies to the page (on everywhere or detected, and the site has no exception).</summary>
    public bool StrictAppliesTo(string? pageUrl) => StrictStateFor(pageUrl) is StrictState.Everywhere or StrictState.Detected;

    /// <summary>A navigation to another site that the page started by itself (not a click, not a server redirect).</summary>
    public static bool IsAutomaticCrossSiteNavigation(string url, string? currentPageUrl, bool userInitiated, bool isRedirect) =>
        !userInitiated && !isRedirect && AddressInput.IsHttpUrl(currentPageUrl) && SiteKey.IsThirdParty(url, currentPageUrl!);

    /// <summary>Decision for a sub-resource request (script, image, media, XHR...).</summary>
    public BlockReason? CheckRequest(string url, bool isScript, string? pageUrl)
    {
        if (_settings.Protection && _malware.IsBlocked(url)) return BlockReason.MaliciousDomain;
        if (isScript && pageUrl != null && StrictAppliesTo(pageUrl) && SiteKey.IsThirdParty(url, pageUrl)) return BlockReason.ThirdPartyScript;
        if (_settings.AdBlock && _ads.IsBlocked(url)) return BlockReason.Ad;
        return null;
    }

    /// <summary>Decision for a top-level navigation.</summary>
    public BlockReason? CheckNavigation(string url, string? currentPageUrl, bool userInitiated, bool isRedirect)
    {
        if (_settings.Protection && _malware.IsBlocked(url)) return BlockReason.MaliciousDomain;
        if (_settings.AdBlock && _ads.IsBlocked(url)) return BlockReason.Ad;
        // Strict: stop pages from silently sending you to another site (ad redirects, scam pages).
        if (IsAutomaticCrossSiteNavigation(url, currentPageUrl, userInitiated, isRedirect) && StrictAppliesTo(currentPageUrl))
            return BlockReason.AutomaticRedirect;
        return null;
    }

    /// <summary>Frames are only blocked for known malicious sites.</summary>
    public bool BlockFrame(string url) => _settings.Protection && _malware.IsBlocked(url);

    /// <summary>Pop-up windows are blocked whenever protection or ad blocking is on.</summary>
    public bool BlockPopups => _settings.Protection || _settings.AdBlock;

    public static string Describe(BlockReason reason) => reason switch
    {
        BlockReason.MaliciousDomain => "malicious/miner domain",
        BlockReason.ThirdPartyScript => "third-party script (strict mode)",
        BlockReason.AutomaticRedirect => "automatic redirect to another site",
        _ => "ad",
    };
}
