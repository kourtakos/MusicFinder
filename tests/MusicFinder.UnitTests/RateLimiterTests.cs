using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;

namespace MusicFinder.UnitTests;

public class RateLimiterTests
{
    private const string Mb = "musicbrainz.org";
    private const string Aid = "api.acoustid.org";

    private static (RateLimiter Limiter, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        return (new RateLimiter(time), time);
    }

    [Fact]
    public async Task TheFirstRequest_StartsAtOnce()
    {
        var (limiter, _) = Make();

        var task = limiter.WaitAsync(Mb, default);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task MusicBrainz_IsHeldToOneRequestASecond()
    {
        var (limiter, time) = Make();
        await limiter.WaitAsync(Mb, default);

        var second = limiter.WaitAsync(Mb, default);
        var third = limiter.WaitAsync(Mb, default);
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await second;
        Assert.False(third.IsCompleted);                 // a full second after the second one, not after the first
        time.Advance(TimeSpan.FromSeconds(1));
        await third;
    }

    [Fact]
    public async Task AcoustId_MayRunAboutThreeAtATime_AndHostsAreIndependent()
    {
        var (limiter, time) = Make();
        await limiter.WaitAsync(Mb, default);
        await limiter.WaitAsync(Aid, default);           // another host: not delayed by MusicBrainz

        var next = limiter.WaitAsync(Aid, default);
        Assert.False(next.IsCompleted);
        time.Advance(RateLimiter.AcoustIdInterval);
        await next;
        Assert.True(RateLimiter.AcoustIdInterval.TotalMilliseconds >= 1000.0 / 3);   // never above 3 a second
    }

    [Fact]
    public async Task APenalty_HoldsEveryoneBackForTheCooldown()
    {
        var (limiter, time) = Make();
        await limiter.WaitAsync(Mb, default);
        limiter.Penalize(Mb, TimeSpan.FromSeconds(10));

        var waiting = limiter.WaitAsync(Mb, default);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
    }

    [Fact]
    public async Task ASmallerPenalty_NeverShortensALongerOne()
    {
        var (limiter, time) = Make();
        limiter.Penalize(Mb, TimeSpan.FromSeconds(30));
        limiter.Penalize(Mb, TimeSpan.FromSeconds(2));

        var waiting = limiter.WaitAsync(Mb, default);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await waiting;
    }

    [Fact]
    public async Task CancellingAWaiter_ThrowsAndDoesNotBlockTheNext()
    {
        var (limiter, time) = Make();
        await limiter.WaitAsync(Mb, default);
        using var cts = new CancellationTokenSource();
        var cancelled = limiter.WaitAsync(Mb, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = limiter.WaitAsync(Mb, default);
        time.Advance(TimeSpan.FromSeconds(2));           // its own slot, one interval after the abandoned one
        await next;
    }

    [Fact]
    public async Task AnAlreadyCancelledToken_NeverStartsARequest()
    {
        var (limiter, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(Mb, cts.Token));
    }

    [Fact]
    public async Task AnUnknownHost_GetsTheStrictestInterval()
    {
        var (limiter, time) = Make();
        await limiter.WaitAsync("example.org", default);

        var next = limiter.WaitAsync("example.org", default);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await next;
    }
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    public void WithoutRetryAfter_TheWaitDoubles(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Next(attempt, 503, null));

    [Fact]
    public void ItGivesUp_AfterThreeRetries() =>
        Assert.Null(RetryPolicy.Next(RetryPolicy.MaxRetries, 503, null));

    [Fact]
    public void TheServersRetryAfter_IsHonoured_UpToAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.Next(0, 503, TimeSpan.FromSeconds(7)));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Next(0, 429, TimeSpan.FromSeconds(60)));
        Assert.Null(RetryPolicy.Next(0, 503, TimeSpan.FromSeconds(61)));   // not worth making the user wait
        Assert.Equal(TimeSpan.Zero, RetryPolicy.Next(0, 503, TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void ARequestTheServerRejected_IsNeverRepeated(int status) =>
        Assert.Null(RetryPolicy.Next(0, status, null));

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    public void TemporaryFailures_AreRetried(int status) =>
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Next(0, status, null));

    [Fact]
    public void ANetworkErrorWithNoResponse_IsRetried() =>
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Next(0, null, null));

    [Fact]
    public void OnlyTooManyRequests_CountsAsAThrottle()
    {
        Assert.True(RetryPolicy.IsThrottle(503));
        Assert.True(RetryPolicy.IsThrottle(429));
        Assert.False(RetryPolicy.IsThrottle(502));
    }
}
