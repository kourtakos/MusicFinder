using System.Net;
using System.Text;

namespace MusicFinder.IntegrationTests;

/// <summary>The network, replaced: answers from a script and remembers what was asked. Nothing here ever leaves the process.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[]? Body, DateTimeOffset At);

    private readonly Func<TimeProvider>? _clock;
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _fallback;
    private readonly object _gate = new();
    public List<Seen> Requests { get; } = [];

    public FakeHttpHandler(Func<TimeProvider>? clock = null) => _clock = clock;

    public FakeHttpHandler Then(HttpStatusCode status, string body = "", string contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        _script.Enqueue(_ =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
            configure?.Invoke(r);
            return r;
        });
        return this;
    }

    public FakeHttpHandler ThenResponse(Func<HttpResponseMessage> make)
    {
        _script.Enqueue(_ => make());
        return this;
    }

    public FakeHttpHandler ThenThrow(Exception ex)
    {
        _script.Enqueue(_ => throw ex);
        return this;
    }

    /// <summary>Used when the script runs out (for example, a server that keeps answering 503).</summary>
    public FakeHttpHandler Always(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null)
    {
        _fallback = _ =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(r);
            return r;
        };
        return this;
    }

    public FakeHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _fallback = responder;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ",", h.Value), StringComparer.OrdinalIgnoreCase);
        Func<HttpRequestMessage, HttpResponseMessage> next;
        lock (_gate)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!, headers, body, _clock?.Invoke().GetUtcNow() ?? DateTimeOffset.UtcNow));
            next = _script.Count > 0 ? _script.Dequeue() : _fallback ?? throw new InvalidOperationException("Unexpected request: " + request.RequestUri);
        }
        return next(request);
    }
}

public static class FakeHttpHandlerExtensions
{
    /// <summary>Queues a 200 answer carrying image bytes.</summary>
    public static FakeHttpHandler ThenImage(this FakeHttpHandler handler, byte[] bytes, string contentType = "image/png") =>
        handler.ThenResponse(() =>
        {
            var r = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(bytes) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return r;
        });
}
