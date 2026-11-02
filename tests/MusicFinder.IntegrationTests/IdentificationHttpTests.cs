using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

public class IdentificationHttpTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeHttpHandler Net { get; }
        public RateLimiter Limiter { get; }
        public IdentificationHttp Http { get; }

        public Rig()
        {
            Net = new FakeHttpHandler(() => Time);
            Limiter = new RateLimiter(Time);
            Http = new IdentificationHttp(Limiter, Time, () => Net);
        }

        /// <summary>Lets fake time run (in small steps) until the call finishes: waits are real code paths, only the clock is fake.</summary>
        public async Task<T> RunAsync<T>(Task<T> call)
        {
            for (var i = 0; i < 2000 && !call.IsCompleted; i++)
            {
                Time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
            return await call;
        }

        public void Dispose() => Http.Dispose();
    }

    private static HttpRequestMessage Get(string path = "recording/abc?inc=releases") =>
        new(HttpMethod.Get, new Uri("https://musicbrainz.org/ws/2/" + path));

    [Fact]
    public async Task ASuccessfulRequest_IsSentOnce_WithItsHeaders()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.OK, "{}");
        var request = Get();
        request.Headers.UserAgent.ParseAdd("MusicFinder/1.0 ( dj@example.com )");

        using var response = await rig.RunAsync(rig.Http.MusicBrainz.SendAsync(request));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(rig.Net.Requests);
        Assert.Contains("MusicFinder/1.0", rig.Net.Requests[0].Headers["User-Agent"]);
        Assert.Contains("application/json", rig.Net.Requests[0].Headers["Accept"]);
    }

    [Fact]
    public async Task A503WithRetryAfter_WaitsThatLong_ThenSucceeds()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.ServiceUnavailable, configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)))
               .Then(HttpStatusCode.OK, "{}");

        using var response = await rig.RunAsync(rig.Http.MusicBrainz.SendAsync(Get()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Net.Requests.Count);
        Assert.True(rig.Net.Requests[1].At - rig.Net.Requests[0].At >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARetryAfterGivenAsADate_IsHonoured()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.TooManyRequests, configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(Start.AddSeconds(8)))
               .Then(HttpStatusCode.OK, "{}");

        using var response = await rig.RunAsync(rig.Http.MusicBrainz.SendAsync(Get()));

        Assert.Equal(2, rig.Net.Requests.Count);
        Assert.True(rig.Net.Requests[1].At >= Start.AddSeconds(8));
    }

    [Fact]
    public async Task AHostThatKeepsSayingSlowDown_EndsAsRateLimited_AfterFourTries()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Http.MusicBrainz.SendAsync(Get())));

        Assert.Equal(IdentificationFailure.RateLimited, ex.Failure);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(4, rig.Net.Requests.Count);          // the first try and three retries
    }

    [Fact]
    public async Task ARateLimitAnswer_AlsoHoldsBackOtherCallersToThatHost()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.ServiceUnavailable, configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)))
               .Then(HttpStatusCode.OK, "{}")
               .Then(HttpStatusCode.OK, "{}");
        var first = rig.Http.MusicBrainz.SendAsync(Get("recording/one"));
        await Task.Delay(50);                              // the first call has been refused and is now waiting

        var other = rig.Http.MusicBrainz.SendAsync(Get("recording/two"));
        await rig.RunAsync(other);

        Assert.True(rig.Net.Requests.Last().At - Start >= TimeSpan.FromSeconds(30));   // the other caller did not jump the queue
        await rig.RunAsync(first);
    }

    [Fact]
    public async Task NoAnswerAtAll_EndsAsOffline()
    {
        using var rig = new Rig();
        rig.Net.Respond(_ => throw new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Http.MusicBrainz.SendAsync(Get())));

        Assert.Equal(IdentificationFailure.Offline, ex.Failure);
        Assert.Null(ex.StatusCode);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task ARefusedRequest_IsReturnedAtOnce_NotRetried()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.NotFound, "{\"error\":\"Not Found\"}");

        using var response = await rig.RunAsync(rig.Http.MusicBrainz.SendAsync(Get()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(rig.Net.Requests);
    }

    [Fact]
    public async Task APostBody_IsSentAgainIntact_OnARetry()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.BadGateway).Then(HttpStatusCode.OK, "{}");
        var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await rig.RunAsync(rig.Http.AcoustId.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "lookup") { Content = content }));

        Assert.Equal(2, rig.Net.Requests.Count);
        Assert.All(rig.Net.Requests, r =>
        {
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, r.Body);
            Assert.Equal("gzip", r.Headers["Content-Encoding"]);
            Assert.Equal("api.acoustid.org", r.Uri.Host);
        });
    }

    [Fact]
    public async Task EveryAttempt_WaitsForItsTurn_SoTwoCallsAreASecondApart()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, "{}");

        var a = rig.Http.MusicBrainz.SendAsync(Get("recording/one"));
        var b = rig.Http.MusicBrainz.SendAsync(Get("recording/two"));
        await rig.RunAsync(a);
        await rig.RunAsync(b);

        var times = rig.Net.Requests.Select(r => r.At).OrderBy(t => t).ToList();
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancellingWhileWaitingToRetry_StopsWithoutWrappingTheCancellation()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.ServiceUnavailable);
        using var cts = new CancellationTokenSource();

        var call = rig.Http.MusicBrainz.SendAsync(Get(), cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(rig.Net.Requests);
    }
}
