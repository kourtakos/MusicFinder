using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

public class AcoustIdClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string SecretKey = "TOPSECRETKEY123";
    private const string Fingerprint = "AQADtEmSJEmUJEmS+/=?&x";

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeHttpHandler Net { get; }
        public IdentificationHttp Http { get; }
        public AcoustIdClient Client { get; }

        public Rig()
        {
            Net = new FakeHttpHandler(() => Time);
            Http = new IdentificationHttp(new RateLimiter(Time), Time, () => Net);
            Client = new AcoustIdClient(Http, new MusicBrainzClientTests.FakeSettings(), new AppInfo("MusicFinder", "1.0.0"));
        }

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

    private static string Decode(byte[] gzipped)
    {
        using var input = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task TheKeyAndFingerprint_TravelInAGzipBody_NeverInTheUrl()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """{"status":"ok","results":[]}""");

        await rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default));

        var request = Assert.Single(rig.Net.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.acoustid.org/v2/lookup", request.Uri.ToString());   // no query string at all
        Assert.Equal("gzip", request.Headers["Content-Encoding"]);
        Assert.StartsWith("application/x-www-form-urlencoded", request.Headers["Content-Type"]);
        var form = System.Web.HttpUtility.ParseQueryString(Decode(request.Body!));
        Assert.Equal(SecretKey, form["client"]);
        Assert.Equal(Fingerprint, form["fingerprint"]);                                  // '+', '/', '=', '?', '&' survived the encoding
        Assert.Equal("215", form["duration"]);
        Assert.Equal("recordingids", form["meta"]);
        Assert.DoesNotContain("@", request.Headers["User-Agent"]);                       // the MusicBrainz contact is not shared with AcoustID
    }

    [Fact]
    public async Task Matches_AreOrderedByScore_WithDistinctLowerCaseRecordingIds()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """
            {"status":"ok","results":[
              {"id":"aaaa","score":0.42,"recordings":[{"id":"11111111-1111-1111-1111-111111111111"}]},
              {"id":"bbbb","score":0.97,"recordings":[{"id":"5F2C9A0E-7B1D-4C3A-9E84-0D6B2A1F7C33"},{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33"},{"id":"not-an-id"}]},
              {"id":"cccc","score":0.99,"recordings":[]},
              {"id":"dddd","score":1.7,"recordings":[{"id":"22222222-2222-2222-2222-222222222222"}]}
            ]}
            """);

        var matches = await rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default));

        Assert.Equal(["dddd", "bbbb", "aaaa"], matches.Select(m => m.AcoustId));          // 'cccc' has no MusicBrainz recording
        Assert.Equal(1.0, matches[0].Score);                                              // clamped
        Assert.Equal(["5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33"], matches[1].RecordingIds);
    }

    [Fact]
    public async Task NoMatch_IsAnEmptyList()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """{"status":"ok","results":[]}""");

        Assert.Empty(await rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default)));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnErrorAnswer_BecomesAReadableFailure_WithoutTheKey(HttpStatusCode status)
    {
        using var rig = new Rig();
        rig.Net.Always(status, """{"status":"error","error":{"code":4,"message":"invalid API key"}}""");

        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default)));

        Assert.Equal("AcoustID: invalid API key", ex.Message);
        Assert.Equal(IdentificationFailure.ServerError, ex.Failure);
        Assert.DoesNotContain(SecretKey, ex.ToString());
    }

    [Fact]
    public async Task ATemporaryOutage_IsRetried_ThenSucceeds()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK, """{"status":"ok","results":[]}""");

        await rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default));

        Assert.Equal(2, rig.Net.Requests.Count);
        Assert.Equal(rig.Net.Requests[0].Body, rig.Net.Requests[1].Body);                // the same body, sent again intact
    }

    [Fact]
    public async Task AnUnexpectedStatusWithoutAMessage_StillFailsCleanly()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.NotFound, "<html>nope</html>");

        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Client.LookupAsync(SecretKey, Fingerprint, 215, default)));

        Assert.Contains("404", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"status\":\"ok\"}")]
    [InlineData("{\"status\":\"ok\",\"results\":{}}")]
    public void GarbageIsNeverAMatch(string json) => Assert.Empty(AcoustIdJson.ParseMatches(json));
}
