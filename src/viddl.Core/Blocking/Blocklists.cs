namespace Viddl.Core.Blocking;

/// <summary>The ad blocker and malware blocker used by the browser, with their built-in lists.</summary>
public static class Blocklists
{
    public static readonly IReadOnlyList<BlocklistSource> AdSources = new[]
    {
        new BlocklistSource("https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts", "adblock-hosts.txt"),
    };

    public static readonly IReadOnlyList<BlocklistSource> MalwareSources = new[]
    {
        new BlocklistSource("https://urlhaus.abuse.ch/downloads/hostfile/", "malware-urlhaus.txt"),
        new BlocklistSource("https://raw.githubusercontent.com/hoshsadiq/adblock-nocoin-list/master/hosts.txt", "malware-nocoin.txt"),
    };

    /// <summary>Common ad networks, pop-up networks and trackers.</summary>
    public static readonly string[] BuiltInAds =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagservices.com", "adnxs.com", "adsrvr.org", "advertising.com", "taboola.com", "outbrain.com",
        "criteo.com", "criteo.net", "pubmatic.com", "rubiconproject.com", "openx.net", "casalemedia.com",
        "amazon-adsystem.com", "adform.net", "smartadserver.com", "teads.tv", "yieldmo.com", "media.net",
        "revcontent.com", "mgid.com", "zedo.com", "adroll.com", "bidswitch.net", "sharethrough.com",
        "indexww.com", "33across.com", "moatads.com", "serving-sys.com", "adcolony.com", "applovin.com",
        "propellerads.com", "popads.net", "popcash.net", "adcash.com", "exoclick.com", "exosrv.com",
        "juicyads.com", "trafficjunky.com", "trafficjunky.net", "trafficstars.com", "tsyndicate.com",
        "adsterra.com", "hilltopads.net", "clickadu.com", "onclickads.net", "onclkds.com", "realsrv.com",
        "a-ads.com", "adspyglass.com", "magsrv.com", "pemsrv.com", "ad-maven.com", "admaven.com",
        "richpush.co", "pushame.com", "monetag.com", "galaksion.com", "clickaine.com", "twinrdsyn.com",
        "plugrush.com", "trafficfactory.biz", "ero-advertising.com", "adxadserv.com",
        "google-analytics.com", "googletagmanager.com", "scorecardresearch.com", "quantserve.com",
        "hotjar.com", "mixpanel.com", "segment.io", "chartbeat.com", "nr-data.net", "mc.yandex.ru",
        "histats.com", "statcounter.com", "clarity.ms", "connect.facebook.net", "ads-twitter.com",
        "analytics.tiktok.com",
    };

    /// <summary>Well-known in-browser cryptocurrency miners.</summary>
    public static readonly string[] BuiltInMalware =
    {
        "coinhive.com", "coin-hive.com", "authedmine.com", "authedmine.eu", "crypto-loot.com", "cryptoloot.pro",
        "crypto-loot.org", "webmine.cz", "webmine.pro", "jsecoin.com", "coinimp.com", "coinimp.net", "minero.cc",
        "monerominer.rocks", "coinerra.com", "coin-have.com", "ppoi.org", "projectpoi.com", "minemytraffic.com",
        "cpufan.club", "coinblind.com", "papoto.com", "cryptaloot.pro", "browsermine.com", "hashforcash.us",
        "deepminer.org", "mineralt.io", "gridcash.net", "nerohut.com", "bmst.pw", "webminepool.com",
        "statdynamic.com", "freecontent.bid", "freecontent.date", "moneromining.online",
    };

    public static DomainBlocklist CreateAdBlocker(string dataDir) =>
        new(BuiltInAds, dataDir, AdSources, "adblock-custom.txt");

    public static DomainBlocklist CreateMalwareBlocker(string dataDir) =>
        new(BuiltInMalware, dataDir, MalwareSources, "malware-custom.txt");

    /// <summary>Rejects downloads that clearly aren't hosts files (e.g. an HTML error page).</summary>
    public static bool LooksLikeHostsFile(string text) =>
        text.Contains("0.0.0.0") || text.Contains("127.0.0.1");
}
