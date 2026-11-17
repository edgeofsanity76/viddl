using Viddl.Core.Blocking;
using Viddl.Core.Browsing;
using Viddl.Core.Settings;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class StrictModeTests
{
    private const string Page = "https://www.shady.com/watch/1";
    private static readonly DateTime T0 = new(2026, 3, 4, 5, 6, 7);

    private static StrictModeAdvisor Advisor(AppSettings s) => new(s, () => T0);

    [Fact]
    public void A_single_pop_up_or_a_few_ads_are_not_enough()
    {
        var s = new AppSettings();
        var advisor = Advisor(s);
        Assert.Null(advisor.Record(Page, ThreatSignal.PopUp));
        for (int i = 0; i < 20; i++) Assert.Null(advisor.Record(Page, ThreatSignal.Ad));
        Assert.Equal(3.5, advisor.Score("shady.com"), 3);
        Assert.Empty(s.AutoStrictSites);
    }

    [Fact]
    public void Repeated_redirect_attempts_switch_strict_on_for_the_site()
    {
        var s = new AppSettings();
        var advisor = Advisor(s);

        Assert.Null(advisor.Record(Page, ThreatSignal.AutomaticRedirect));
        var decision = advisor.Record("https://cdn.shady.com/other", ThreatSignal.AutomaticRedirect);

        Assert.Equal(new StrictDecision("shady.com", "It tried to send you to other sites"), decision);
        var entry = s.FindAutoStrict("shady.com")!;
        Assert.Equal(T0, entry.Since);
        Assert.Null(advisor.Record(Page, ThreatSignal.AutomaticRedirect)); // already decided
    }

    [Theory]
    [InlineData(ThreatSignal.MaliciousRequest, ThreatSignal.PopUp, ThreatSignal.PageDialog)]
    [InlineData(ThreatSignal.ExternalProgram, ThreatSignal.PageDownload, ThreatSignal.Ad)]
    [InlineData(ThreatSignal.PermissionRequest, ThreatSignal.PageDownload, ThreatSignal.PopUp)]
    public void Mixed_signals_add_up(ThreatSignal a, ThreatSignal b, ThreatSignal c)
    {
        var s = new AppSettings();
        var advisor = Advisor(s);
        StrictDecision? decision = null;
        foreach (var sig in new[] { a, b, c, a, b, c }) decision ??= advisor.Record(Page, sig);
        Assert.NotNull(decision);
    }

    [Fact]
    public void Reason_lists_the_biggest_contributors()
    {
        var reason = StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int>
        {
            [ThreatSignal.Ad] = 1, [ThreatSignal.PopUp] = 2, [ThreatSignal.AutomaticRedirect] = 1, [ThreatSignal.PageDownload] = 2,
        });
        Assert.Equal("It started downloads by itself, tried to send you to other sites and opened pop-ups", reason);
        Assert.Equal("It showed page dialogs and asked for permissions",
            StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int> { [ThreatSignal.PermissionRequest] = 1, [ThreatSignal.PageDialog] = 1 }));
        Assert.Equal("It loaded content from known malicious sites",
            StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int> { [ThreatSignal.MaliciousRequest] = 1, [ThreatSignal.Ad] = 0 }));
        Assert.Equal("It behaved suspiciously", StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int>()));
        Assert.Contains("tried to open other programs", StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int> { [ThreatSignal.ExternalProgram] = 1 }));
        Assert.Contains("showed lots of ads", StrictModeAdvisor.Describe(new Dictionary<ThreatSignal, int> { [ThreatSignal.Ad] = 60 }));
    }

    [Fact]
    public void No_decision_when_detection_is_off_strict_is_on_everywhere_or_site_is_allowed()
    {
        var off = new AppSettings { AutoStrict = false };
        var everywhere = new AppSettings { StrictScripts = true };
        var allowed = new AppSettings();
        allowed.ToggleStrictAllowed("shady.com");

        foreach (var s in new[] { off, everywhere, allowed })
        {
            var advisor = Advisor(s);
            for (int i = 0; i < 5; i++) Assert.Null(advisor.Record(Page, ThreatSignal.MaliciousRequest));
            Assert.Empty(s.AutoStrictSites);
        }
    }

    [Fact]
    public void Pages_without_a_site_are_ignored_and_scores_can_be_forgotten()
    {
        var s = new AppSettings();
        var advisor = new StrictModeAdvisor(s);
        Assert.Null(advisor.Record("about:blank", ThreatSignal.MaliciousRequest));
        Assert.Null(advisor.Record(null, ThreatSignal.MaliciousRequest));
        Assert.Equal(0, advisor.Score("unknown.com"));

        advisor.Record(Page, ThreatSignal.AutomaticRedirect);
        advisor.Forget("shady.com");
        Assert.Equal(0, advisor.Score("shady.com"));
        Assert.NotNull(advisor.Record(Page, ThreatSignal.ExternalProgram) ?? advisor.Record(Page, ThreatSignal.ExternalProgram));
        Assert.True((DateTime.Now - s.FindAutoStrict("shady.com")!.Since).Duration() < TimeSpan.FromMinutes(1));
    }

    private static BrowserProtection Protection(AppSettings s, TempDir dir) =>
        new(new DomainBlocklist(Array.Empty<string>(), dir.Path, Array.Empty<BlocklistSource>(), null),
            new DomainBlocklist(Array.Empty<string>(), dir.Path, Array.Empty<BlocklistSource>(), null), s);

    [Fact]
    public void Strict_state_explains_why_strict_applies()
    {
        using var dir = new TempDir();
        var s = new AppSettings();
        var p = Protection(s, dir);

        Assert.Equal(StrictState.Off, p.StrictStateFor(Page));
        Assert.Equal(StrictState.Off, p.StrictStateFor(null));

        s.AddAutoStrict("shady.com", "r", T0);
        Assert.Equal(StrictState.Detected, p.StrictStateFor(Page));
        Assert.True(p.StrictAppliesTo(Page));
        Assert.Equal(StrictState.Off, p.StrictStateFor("https://fine.org/"));

        s.AutoStrict = false; // detection off: detected sites no longer apply
        Assert.Equal(StrictState.Off, p.StrictStateFor(Page));
        s.AutoStrict = true;

        s.ToggleStrictAllowed("shady.com");
        Assert.Equal(StrictState.AllowedHere, p.StrictStateFor(Page));
        Assert.False(p.StrictAppliesTo(Page));

        s.StrictScripts = true;
        Assert.Equal(StrictState.Everywhere, p.StrictStateFor("https://fine.org/"));
        Assert.Equal(StrictState.Everywhere, p.StrictStateFor("about:blank"));
        Assert.Equal(StrictState.AllowedHere, p.StrictStateFor(Page));
    }

    [Fact]
    public void Detected_sites_get_strict_blocking()
    {
        using var dir = new TempDir();
        var s = new AppSettings();
        var p = Protection(s, dir);
        Assert.Null(p.CheckRequest("https://cdn.other.com/a.js", true, Page));
        Assert.Null(p.CheckNavigation("https://scam.com/", Page, false, false));

        s.AddAutoStrict("shady.com", "r", T0);
        Assert.Equal(BlockReason.ThirdPartyScript, p.CheckRequest("https://cdn.other.com/a.js", true, Page));
        Assert.Equal(BlockReason.AutomaticRedirect, p.CheckNavigation("https://scam.com/", Page, false, false));
    }

    [Theory]
    [InlineData("https://scam.com/", Page, false, false, true)]
    [InlineData("https://scam.com/", Page, true, false, false)]
    [InlineData("https://scam.com/", Page, false, true, false)]
    [InlineData("https://shady.com/next", Page, false, false, false)]
    [InlineData("https://scam.com/", "about:blank", false, false, false)]
    [InlineData("https://scam.com/", null, false, false, false)]
    public void Automatic_cross_site_navigation(string url, string? page, bool user, bool redirect, bool expected) =>
        Assert.Equal(expected, BrowserProtection.IsAutomaticCrossSiteNavigation(url, page, user, redirect));
}
