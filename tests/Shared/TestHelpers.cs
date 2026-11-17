using System.Net;
using System.Text;

namespace Viddl.Tests;

/// <summary>A temporary folder deleted when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "viddl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, string content)
    {
        var p = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// In-memory HTTP server for tests. Routes match on the full URL, or on the URL without its query string.
/// Records every request it receives.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<bool> UseCookiesRequests { get; } = new();

    public FakeHttp Route(string url, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _routes[url] = responder;
        return this;
    }

    public FakeHttp Text(string url, string body, string contentType = "text/plain") =>
        Route(url, _ => Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), contentType));

    public FakeHttp Bytes(string url, byte[] body, string contentType = "application/octet-stream") =>
        Route(url, _ => Response(HttpStatusCode.OK, body, contentType));

    public FakeHttp Status(string url, HttpStatusCode code) => Route(url, _ => new HttpResponseMessage(code) { Content = new ByteArrayContent(Array.Empty<byte>()) });

    /// <summary>Responds with each status in turn, then with the final responder.</summary>
    public FakeHttp Sequence(string url, params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        int i = 0;
        return Route(url, req => responders[Math.Min(i++, responders.Length - 1)](req));
    }

    public static HttpResponseMessage Response(HttpStatusCode code, byte[] body, string? contentType = null)
    {
        var content = new ByteArrayContent(body);
        if (contentType != null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return new HttpResponseMessage(code) { Content = content };
    }

    /// <summary>Use as an <c>HttpHandlerFactory</c>; records the cookie setting requested.</summary>
    public HttpMessageHandler Factory(bool useCookies)
    {
        lock (UseCookiesRequests) UseCookiesRequests.Add(useCookies);
        return new NonDisposing(this);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Requests) Requests.Add(request);
        var url = request.RequestUri!.ToString();
        var noQuery = request.RequestUri.GetLeftPart(UriPartial.Path);
        HttpResponseMessage resp;
        Func<HttpRequestMessage, HttpResponseMessage>? route;
        lock (_routes) _routes.TryGetValue(url, out route);
        if (route == null) lock (_routes) _routes.TryGetValue(noQuery, out route);
        resp = route != null ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        resp.RequestMessage ??= request;
        return Task.FromResult(resp);
    }

    /// <summary>Wrapper so disposing an HttpClient doesn't dispose the shared fake.</summary>
    private sealed class NonDisposing : DelegatingHandler
    {
        public NonDisposing(HttpMessageHandler inner) : base(inner) { }

        protected override void Dispose(bool disposing) { /* keep the inner handler alive */ }
    }
}

/// <summary>Synchronization context that queues posted callbacks so tests can run them on demand.</summary>
public sealed class QueueingSyncContext : SynchronizationContext
{
    public Queue<(SendOrPostCallback Callback, object? State)> Posted { get; } = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (Posted) Posted.Enqueue((d, state));
    }

    public int RunAll()
    {
        int n = 0;
        while (true)
        {
            (SendOrPostCallback, object?) item;
            lock (Posted)
            {
                if (Posted.Count == 0) return n;
                item = Posted.Dequeue();
            }
            item.Item1(item.Item2);
            n++;
        }
    }
}
