using Viddl.Core.Net;
using Viddl.Tests;

namespace Viddl.Core.Tests;

public class HttpClientsTests
{
    private static string Header(HttpClient c, string name) =>
        name == "User-Agent" ? c.DefaultRequestHeaders.UserAgent.ToString()
        : c.DefaultRequestHeaders.TryGetValues(name, out var v) ? string.Join(",", v) : "";

    [Fact]
    public void Create_sends_browser_headers_and_referer()
    {
        var fake = new FakeHttp();
        using var c = HttpClients.Create("https://site.example/page?x=1", handlerFactory: fake.Factory);

        Assert.Equal(HttpClients.DefaultUserAgent, Header(c, "User-Agent"));
        Assert.Equal("https://site.example/page?x=1", c.DefaultRequestHeaders.Referrer!.ToString());
        Assert.Equal("https://site.example", Header(c, "Origin"));
        Assert.Contains("*/*", Header(c, "Accept"));
        Assert.True(fake.UseCookiesRequests.Single());
    }

    [Fact]
    public void Create_with_cookies_sends_raw_header_and_disables_cookie_container()
    {
        var fake = new FakeHttp();
        using var c = HttpClients.Create(null, "MyAgent/1.0", "a=1; b=2", fake.Factory);

        Assert.Equal("MyAgent/1.0", Header(c, "User-Agent"));
        Assert.Equal("a=1; b=2", Header(c, "Cookie"));
        Assert.Null(c.DefaultRequestHeaders.Referrer);
        Assert.False(fake.UseCookiesRequests.Single());
    }

    [Fact]
    public void Create_ignores_invalid_referer()
    {
        using var c = HttpClients.Create("not a url", handlerFactory: new FakeHttp().Factory);
        Assert.Null(c.DefaultRequestHeaders.Referrer);
    }

    [Fact]
    public void PageClient_sends_navigation_headers_and_client_hints_only_for_default_agent()
    {
        using var chrome = HttpClients.CreatePageClient(handlerFactory: new FakeHttp().Factory);
        Assert.Contains("text/html", Header(chrome, "Accept"));
        Assert.Equal("navigate", Header(chrome, "Sec-Fetch-Mode"));
        Assert.Contains("Chromium", Header(chrome, "sec-ch-ua"));

        using var custom = HttpClients.CreatePageClient("Edge/1", "c=1", new FakeHttp().Factory);
        Assert.Equal("", Header(custom, "sec-ch-ua"));
        Assert.Equal("c=1", Header(custom, "Cookie"));
    }

    [Fact]
    public void DefaultHandler_is_a_real_handler_with_requested_cookie_setting()
    {
        using var on = (HttpClientHandler)HttpClients.DefaultHandler(true);
        using var off = (HttpClientHandler)HttpClients.DefaultHandler(false);
        Assert.True(on.UseCookies);
        Assert.False(off.UseCookies);
        Assert.True(on.AllowAutoRedirect);

        using var client = HttpClients.Create();
        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }
}
