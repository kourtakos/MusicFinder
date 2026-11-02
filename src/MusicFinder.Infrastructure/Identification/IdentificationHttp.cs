using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using MusicFinder.Application.Identification;

namespace MusicFinder.Infrastructure.Identification;

/// <summary>
/// The two HTTP clients used for identification, sharing one rate limiter. Each request goes through
/// retry -> rate limit -> network, so every attempt (including retries) waits its turn. Tests substitute the innermost handler.
/// </summary>
public sealed class IdentificationHttp : IDisposable
{
    public const string MusicBrainzHost = "musicbrainz.org";
    public const string AcoustIdHost = "api.acoustid.org";
    public const string CoverArtHost = "coverartarchive.org";

    public HttpClient MusicBrainz { get; }
    public HttpClient AcoustId { get; }
    public HttpClient CoverArt { get; }

    public IdentificationHttp(IRateLimiter limiter, TimeProvider time, Func<HttpMessageHandler>? innerFactory = null, ILogger<IdentificationHttp>? log = null)
    {
        MusicBrainz = Create(limiter, time, innerFactory, log, "https://musicbrainz.org/ws/2/");
        MusicBrainz.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AcoustId = Create(limiter, time, innerFactory, log, "https://api.acoustid.org/v2/");
        CoverArt = Create(limiter, time, innerFactory, log, "https://coverartarchive.org/");
    }

    private static HttpClient Create(IRateLimiter limiter, TimeProvider time, Func<HttpMessageHandler>? innerFactory, ILogger? log, string baseAddress)
    {
        var inner = innerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var pipeline = new RetryHandler(time, limiter, log) { InnerHandler = new RateLimitingHandler(limiter) { InnerHandler = inner } };
        return new HttpClient(pipeline) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        MusicBrainz.Dispose();
        AcoustId.Dispose();
        CoverArt.Dispose();
    }
}

/// <summary>Waits for the host's turn before every attempt.</summary>
internal sealed class RateLimitingHandler : DelegatingHandler
{
    private readonly IRateLimiter _limiter;
    public RateLimitingHandler(IRateLimiter limiter) => _limiter = limiter;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _limiter.WaitAsync(request.RequestUri!.Host, ct);
        return await base.SendAsync(request, ct);
    }
}

/// <summary>
/// Retries temporary failures as <see cref="RetryPolicy"/> says, and tells the shared limiter when a host asked us to slow down.
/// A request message cannot be sent twice, so every attempt is a copy. Logs the host, path and status, never the query or body.
/// </summary>
internal sealed class RetryHandler : DelegatingHandler
{
    private readonly TimeProvider _time;
    private readonly IRateLimiter _limiter;
    private readonly ILogger? _log;
    

    public RetryHandler(TimeProvider time, IRateLimiter limiter, ILogger? log)
    {
        _time = time;
        _limiter = limiter;
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var host = request.RequestUri!.Host;

        for (var attempt = 0; ; attempt++)
        {
            using var copy = Clone(request, body);
            var clock = Stopwatch.StartNew();
            HttpResponseMessage? response = null;
            Exception? error = null;
            try
            {
                response = await base.SendAsync(copy, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                error = ex;   // no answer, or our own 30 s timeout
            }

            var status = response is null ? (int?)null : (int)response.StatusCode;
            _log?.LogDebug("{Method} {Host}{Path} -> {Status} in {Ms} ms (attempt {Attempt})",
                request.Method, host, request.RequestUri.AbsolutePath, status?.ToString() ?? "no response", clock.ElapsedMilliseconds, attempt + 1);

            if (response is not null && status is >= 200 and < 400) return response;
            if (response is not null && !RetryPolicy.IsRetryable(status!.Value)) return response;   // 400/401/404...: the caller decides

            var retryAfter = response is null ? null : ReadRetryAfter(response);
            var wait = RetryPolicy.Next(attempt, status, retryAfter);
            if (status is { } s && RetryPolicy.IsThrottle(s))
                _limiter.Penalize(host, wait ?? RetryPolicy.MaxWait);

            if (wait is null)
            {
                var failure = status is { } st && RetryPolicy.IsThrottle(st) ? IdentificationFailure.RateLimited
                    : response is null ? IdentificationFailure.Offline
                    : IdentificationFailure.ServerError;
                response?.Dispose();
                throw new IdentificationHttpException(failure, status,
                    failure switch
                    {
                        IdentificationFailure.RateLimited => $"{host} asked us to slow down.",
                        IdentificationFailure.Offline => $"Could not reach {host}.",
                        _ => $"{host} answered with an error ({status})."
                    }, error);
            }

            response?.Dispose();
            await Task.Delay(wait.Value + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), _time, ct);
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        return header.Date is { } date ? date - _time.GetUtcNow() : null;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var h in original.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var h in original.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return copy;
    }
}
