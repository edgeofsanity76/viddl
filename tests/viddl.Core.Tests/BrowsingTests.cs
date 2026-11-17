using Viddl.Core.Blocking;
using Viddl.Core.Browsing;
using Viddl.Core.Settings;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class BrowsingTests
{
    private static SiteEntry E(string url, string? title = null, int visits = 1) => new() { Url = url, Title = title, Visits = visits };

    [Fact]
    public void Suggestions_put_favorites_first_and_match_title_or_url()
    {
        var favs = new[] { E("https://news.example/", "Daily News") };
        var hist = new[] { E("https://news.example/", "Daily News"), E("https://video.test/watch", "Funny clips"), E("https://other.org/", "Other") };

        var all = SuggestionEngine.Build("", favs, hist);
        Assert.Equal(3, all.Count);
        Assert.True(all[0].IsFavorite);
        Assert.Equal("★", all[0].Glyph);
        Assert.Equal("🕘", all[1].Glyph);
        Assert.Equal("Daily News", all[0].Title);
        Assert.Equal("https://news.example/", all[0].Url);

        var clips = SuggestionEngine.Build("CLIPS", favs, hist);
        Assert.Equal("https://video.test/watch", clips.Single().Url);

        Assert.Empty(SuggestionEngine.Build("zzz", favs, hist));
        Assert.Single(SuggestionEngine.Build(null, Array.Empty<SiteEntry>(), new[] { E("https://x.com") }));
    }

    [Fact]
    public void Typed_suggestions_rank_host_prefix_then_visits()
    {
        var hist = new[]
        {
            E("https://somewhere.com/?q=tube", "Search", 50),
            E("https://www.tube.com/", "Tube", 1),
            E("https://tubeless.net/", "Tubeless", 5),
        };
        var r = SuggestionEngine.Build("tube", Array.Empty<SiteEntry>(), hist);
        Assert.Equal(new[] { "Tubeless", "Tube", "Search" }, r.Select(x => x.Title));
    }

    [Fact]
    public void Suggestions_are_limited()
    {
        var hist = Enumerable.Range(0, 30).Select(i => E($"https://s{i}.com/")).ToList();
        Assert.Equal(10, SuggestionEngine.Build("", Array.Empty<SiteEntry>(), hist).Count);
        Assert.Equal(3, SuggestionEngine.Build("", Array.Empty<SiteEntry>(), hist, max: 3).Count);
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("funny cats", "https://www.bing.com/search?q=funny%20cats")]
    [InlineData("localhost", "https://www.bing.com/search?q=localhost")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AddressInput_turns_text_into_url(string? text, string? expected) =>
        Assert.Equal(expected, AddressInput.ToUrl(text));

    [Theory]
    [InlineData("https://a.com/x\nmore", "https://a.com/x")]
    [InlineData("  http://b.org  ", "http://b.org")]
    [InlineData("https://c.net/\0", "https://c.net/")]
    [InlineData("ftp://x", null)]
    [InlineData("hello", null)]
    [InlineData(null, null)]
    public void AddressInput_FirstUrl(string? text, string? expected) =>
        Assert.Equal(expected, AddressInput.FirstUrl(text));

    private static (BrowserProtection P, AppSettings S) Protection(TempDir dir, bool protect = true, bool strict = true, bool ads = true)
    {
        var s = new AppSettings { Protection = protect, StrictScripts = strict, AdBlock = ads };
        var adList = new DomainBlocklist(new[] { "ads.test" }, dir.Path, Array.Empty<BlocklistSource>(), null);
        var malware = new DomainBlocklist(new[] { "evil.test" }, dir.Path, Array.Empty<BlocklistSource>(), null);
        return (new BrowserProtection(adList, malware, s), s);
    }

    [Fact]
    public void Protection_blocks_requests_by_priority()
    {
        using var dir = new TempDir();
        var (p, _) = Protection(dir);
        const string page = "https://www.site.com/watch";

        Assert.True(p.ProtectionOn && p.StrictOn && p.AdBlockOn && p.BlockPopups);
        Assert.Equal(BlockReason.MaliciousDomain, p.CheckRequest("https://evil.test/m.js", true, page));
        Assert.Equal(BlockReason.ThirdPartyScript, p.CheckRequest("https://cdn.other.com/player.js", true, page));
        Assert.Equal(BlockReason.Ad, p.CheckRequest("https://ads.test/banner.png", false, page));
        Assert.Null(p.CheckRequest("https://cdn.other.com/video.m3u8", false, page)); // not a script
        Assert.Null(p.CheckRequest("https://static.site.com/app.js", true, page));   // same site
        Assert.Null(p.CheckRequest("https://cdn.other.com/player.js", true, null));
    }

    [Fact]
    public void Strict_mode_respects_site_exceptions()
    {
        using var dir = new TempDir();
        var (p, s) = Protection(dir);
        const string page = "https://www.site.com/watch";
        Assert.True(p.StrictAppliesTo(page));
        Assert.True(p.StrictAppliesTo("about:blank"));

        s.ToggleStrictAllowed("site.com");
        Assert.False(p.StrictAppliesTo(page));
        Assert.Null(p.CheckRequest("https://cdn.other.com/player.js", true, page));

        s.StrictScripts = false;
        Assert.False(p.StrictAppliesTo("https://elsewhere.com/"));
    }

    [Fact]
    public void Protection_checks_navigations_and_frames()
    {
        using var dir = new TempDir();
        var (p, _) = Protection(dir);
        const string page = "https://www.site.com/watch";

        Assert.Equal(BlockReason.MaliciousDomain, p.CheckNavigation("https://evil.test/", page, true, false));
        Assert.Equal(BlockReason.Ad, p.CheckNavigation("https://ads.test/click", page, true, false));
        Assert.Equal(BlockReason.AutomaticRedirect, p.CheckNavigation("https://scam.com/", page, false, false));
        Assert.Null(p.CheckNavigation("https://scam.com/", page, true, false));   // user clicked
        Assert.Null(p.CheckNavigation("https://login.com/", page, false, true));  // server redirect
        Assert.Null(p.CheckNavigation("https://www.site.com/next", page, false, false));
        Assert.Null(p.CheckNavigation("https://scam.com/", "about:blank", false, false));
        Assert.True(p.BlockFrame("https://evil.test/frame"));
        Assert.False(p.BlockFrame("https://site.com/frame"));
    }

    [Fact]
    public void Protection_can_be_switched_off()
    {
        using var dir = new TempDir();
        var (p, _) = Protection(dir, protect: false, strict: false, ads: false);
        Assert.Null(p.CheckRequest("https://evil.test/m.js", true, "https://site.com/"));
        Assert.Null(p.CheckRequest("https://ads.test/a.js", true, "https://site.com/"));
        Assert.Null(p.CheckNavigation("https://evil.test/", "https://site.com/", false, false));
        Assert.False(p.BlockFrame("https://evil.test/"));
        Assert.False(p.BlockPopups);
    }

    [Theory]
    [InlineData(BlockReason.MaliciousDomain, "malicious/miner domain")]
    [InlineData(BlockReason.ThirdPartyScript, "third-party script (strict mode)")]
    [InlineData(BlockReason.AutomaticRedirect, "automatic redirect to another site")]
    [InlineData(BlockReason.Ad, "ad")]
    public void Describe_block_reasons(BlockReason reason, string text) =>
        Assert.Equal(text, BrowserProtection.Describe(reason));

    [Fact]
    public void Browser_scripts_contain_their_protections()
    {
        Assert.Contains("webdriver", BrowserScripts.HideAutomation);
        Assert.Contains("CoinHive", BrowserScripts.Guard);
        Assert.Contains("beforeunload", BrowserScripts.Guard);
        Assert.Contains("pushState", BrowserScripts.Guard);
        Assert.Contains("window.open", BrowserScripts.Guard);
        Assert.Contains("<title>viddl</title>", BrowserScripts.StartPage);
    }
}
