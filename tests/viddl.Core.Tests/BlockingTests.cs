using System.Net;
using Viddl.Core.Blocking;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class BlockingTests
{
    private static readonly BlocklistSource[] Sources =
    {
        new("https://lists.test/a.txt", "a.txt"),
        new("https://lists.test/b.txt", "b.txt"),
    };

    [Theory]
    [InlineData("0.0.0.0 ads.example.com", "ads.example.com")]
    [InlineData("127.0.0.1   Tracker.Example.org  # comment", "tracker.example.org")]
    [InlineData(".leading.dot.net", "leading.dot.net")]
    [InlineData("plain.example", "plain.example")]
    [InlineData("# only a comment", null)]
    [InlineData("   ", null)]
    [InlineData("0.0.0.0 localhost", null)]
    [InlineData("0.0.0.0 0.0.0.0", null)]
    [InlineData("nodots", null)]
    [InlineData("10.1.2.3", null)]
    public void ParseLine_extracts_domains(string line, string? expected) =>
        Assert.Equal(expected, DomainBlocklist.ParseLine(line));

    [Fact]
    public void Blocks_listed_domains_and_their_subdomains()
    {
        using var dir = new TempDir();
        var list = new DomainBlocklist(new[] { "ads.test", "path.test/ignored" }, dir.Path, Sources, null);

        Assert.Equal(1, list.Count); // entries containing '/' are ignored
        Assert.True(list.IsBlocked("https://ads.test/x.js"));
        Assert.True(list.IsBlocked("http://a.b.ads.test/"));
        Assert.False(list.IsBlocked("https://myads.test/"));
        Assert.False(list.IsBlocked("https://example.com/"));
        Assert.False(list.IsBlocked("ftp://ads.test/"));
        Assert.False(list.IsBlocked("not a url"));
        Assert.False(list.IsBlocked(new Uri("https://localhost/")));
        Assert.Null(list.CustomListPath);
    }

    [Fact]
    public void Loads_downloaded_and_custom_files()
    {
        using var dir = new TempDir();
        dir.Write("a.txt", "0.0.0.0 from-a.test\n# comment\n");
        dir.Write("custom.txt", "mine.test\n");
        var list = new DomainBlocklist(Array.Empty<string>(), dir.Path, Sources, "custom.txt");

        Assert.Equal(dir.File("custom.txt"), list.CustomListPath);
        Assert.True(list.IsBlocked("https://from-a.test/"));
        Assert.True(list.IsBlocked("https://mine.test/"));

        dir.Write("b.txt", "later.test");
        Assert.False(list.IsBlocked("https://later.test/"));
        list.Reload();
        Assert.True(list.IsBlocked("https://later.test/"));
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Locked_list_file_is_skipped()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.txt", "locked.test");
        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var list = new DomainBlocklist(new[] { "builtin.test" }, dir.Path, Sources, null);
        Assert.True(list.IsBlocked("https://builtin.test/"));
        Assert.False(list.IsBlocked("https://locked.test/"));
    }

    [Fact]
    public async Task UpdateAsync_downloads_lists_and_reloads()
    {
        using var dir = new TempDir();
        var http = new FakeHttp().Text("https://lists.test/a.txt", "0.0.0.0 new-a.test").Text("https://lists.test/b.txt", "0.0.0.0 new-b.test");
        var list = new DomainBlocklist(Array.Empty<string>(), Path.Combine(dir.Path, "sub"), Sources, null);
        using var client = new HttpClient(http);

        var count = await list.UpdateAsync(client, Blocklists.LooksLikeHostsFile);

        Assert.Equal(2, count);
        Assert.True(list.IsBlocked("https://new-a.test/"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "sub", "b.txt")));
    }

    [Fact]
    public async Task UpdateAsync_skips_failed_lists_but_throws_when_all_fail()
    {
        using var dir = new TempDir();
        var partial = new FakeHttp().Text("https://lists.test/a.txt", "0.0.0.0 ok.test").Text("https://lists.test/b.txt", "<html>error</html>");
        var list = new DomainBlocklist(Array.Empty<string>(), dir.Path, Sources, null);
        using (var client = new HttpClient(partial))
            Assert.Equal(1, await list.UpdateAsync(client, Blocklists.LooksLikeHostsFile));

        var none = new FakeHttp().Status("https://lists.test/a.txt", HttpStatusCode.InternalServerError);
        using var failing = new HttpClient(none);
        await Assert.ThrowsAnyAsync<Exception>(() => list.UpdateAsync(failing));
    }

    [Fact]
    public async Task UpdateAsync_honours_cancellation()
    {
        using var dir = new TempDir();
        var list = new DomainBlocklist(Array.Empty<string>(), dir.Path, Sources, null);
        using var client = new HttpClient(new FakeHttp());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => list.UpdateAsync(client, ct: new CancellationToken(true)));
    }

    [Fact]
    public void Built_in_blockers_are_configured()
    {
        using var dir = new TempDir();
        var ads = Blocklists.CreateAdBlocker(dir.Path);
        var malware = Blocklists.CreateMalwareBlocker(dir.Path);

        Assert.True(ads.IsBlocked("https://securepubads.g.doubleclick.net/tag.js"));
        Assert.EndsWith("adblock-custom.txt", ads.CustomListPath);
        Assert.True(malware.IsBlocked("https://coinhive.com/lib.js"));
        Assert.EndsWith("malware-custom.txt", malware.CustomListPath);
        Assert.False(malware.IsBlocked("https://doubleclick.net/"));
        Assert.Single(Blocklists.AdSources);
        Assert.Equal(2, Blocklists.MalwareSources.Count);
        Assert.True(Blocklists.LooksLikeHostsFile("127.0.0.1 a.b"));
        Assert.False(Blocklists.LooksLikeHostsFile("<html>"));
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("news.bbc.co.uk", "bbc.co.uk")]
    [InlineData("CDN.Example.COM", "example.com")]
    public void SiteOf_returns_registrable_domain(string host, string expected) =>
        Assert.Equal(expected, SiteKey.SiteOf(host));

    [Fact]
    public void Third_party_and_display_host()
    {
        Assert.Equal("example.com", SiteKey.SiteOfUrl("https://www.example.com/x"));
        Assert.Null(SiteKey.SiteOfUrl("ftp://example.com/"));
        Assert.Null(SiteKey.SiteOfUrl(null));
        Assert.True(SiteKey.IsThirdParty("https://cdn.other.net/a.js", "https://www.example.com/"));
        Assert.False(SiteKey.IsThirdParty("https://static.example.com/a.js", "https://www.example.com/"));
        Assert.False(SiteKey.IsThirdParty("https://cdn.other.net/a.js", "about:blank"));
        Assert.False(SiteKey.IsThirdParty("nonsense", "https://www.example.com/"));
        Assert.Equal("www.example.com", SiteKey.DisplayHost("https://www.example.com/x"));
        Assert.Equal("ms-settings:", SiteKey.DisplayHost("ms-settings:display"));
        Assert.Equal("junk", SiteKey.DisplayHost("junk"));
    }
}
