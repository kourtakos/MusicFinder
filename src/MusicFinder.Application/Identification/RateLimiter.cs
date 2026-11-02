namespace MusicFinder.Application.Identification;

/// <summary>
/// Keeps the whole process within the public services' published limits (MusicBrainz about 1 request a second,
/// AcoustID at most 3 a second). Shared by every caller, so a batch and a single lookup cannot add up past the limit.
/// </summary>
public interface IRateLimiter
{
    /// <summary>Completes when a request to this host may start. Requests are released in the order they asked.</summary>
    Task WaitAsync(string host, CancellationToken ct);
    /// <summary>The host answered "too many requests" (503/429): nothing is sent to it before now + <paramref name="cooldown"/>.</summary>
    void Penalize(string host, TimeSpan cooldown);
}

public sealed class RateLimiter : IRateLimiter
{
    public static readonly TimeSpan MusicBrainzInterval = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan AcoustIdInterval = TimeSpan.FromMilliseconds(350);

    private readonly TimeProvider _time;
    private readonly Func<string, TimeSpan> _intervalFor;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _nextStart = new(StringComparer.OrdinalIgnoreCase);

    public RateLimiter(TimeProvider time, Func<string, TimeSpan>? intervalFor = null)
    {
        _time = time;
        _intervalFor = intervalFor ?? DefaultInterval;
    }

    /// <summary>Unknown hosts get the strictest interval: it is safer to be slow than to be refused.</summary>
    public static TimeSpan DefaultInterval(string host) =>
        host.Equals("api.acoustid.org", StringComparison.OrdinalIgnoreCase) ? AcoustIdInterval : MusicBrainzInterval;

    public async Task WaitAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TimeSpan wait;
        DateTimeOffset slot;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            slot = _nextStart.TryGetValue(host, out var next) && next > now ? next : now;
            _nextStart[host] = slot + _intervalFor(host);   // the slot is reserved now, so a cancelled waiter just leaves a gap
            wait = slot - now;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct);
    }

    public void Penalize(string host, TimeSpan cooldown)
    {
        lock (_gate)
        {
            var until = _time.GetUtcNow() + cooldown;
            if (!_nextStart.TryGetValue(host, out var next) || next < until) _nextStart[host] = until;
        }
    }
}

/// <summary>When, and whether, to try a failed request again. Pure; the HTTP handler adds a little random jitter.</summary>
public static class RetryPolicy
{
    public const int MaxRetries = 3;
    /// <summary>A server that asks for a longer pause than this is not waited for: the user is looking at a screen.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <param name="attempt">0 for the first failure.</param>
    /// <param name="statusCode">The HTTP status, or null when no response arrived (network error, timeout).</param>
    /// <param name="retryAfter">The server's Retry-After, if it sent one.</param>
    /// <returns>How long to wait before the next try, or null to give up.</returns>
    public static TimeSpan? Next(int attempt, int? statusCode, TimeSpan? retryAfter)
    {
        if (attempt >= MaxRetries) return null;
        if (statusCode is { } code && !IsRetryable(code)) return null;

        if (retryAfter is { } ra)
            return ra > MaxWait ? null : (ra < TimeSpan.Zero ? TimeSpan.Zero : ra);

        return TimeSpan.FromSeconds(1 << attempt);   // 1 s, 2 s, 4 s
    }

    public static bool IsRetryable(int statusCode) => statusCode is 429 or 502 or 503 or 504 or 408;

    /// <summary>"Too many requests" answers: the host must also be given time to recover, for every caller.</summary>
    public static bool IsThrottle(int statusCode) => statusCode is 429 or 503;
}
