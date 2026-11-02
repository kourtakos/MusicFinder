using System.Net;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

public class MusicBrainzJsonTests
{
    // Shaped like a live "recording?query=..." answer (trimmed): releases carry release-group, media[].track[] (singular in search).
    private const string SearchJson = """
        {"created":"2026-11-02T09:14:59.458Z","count":231,"offset":0,"recordings":[
          {"id":"CC3E8691-321F-49AF-98A1-D4A11F363E1D","score":100,"title":"Bohemian Rhapsody","length":145800,"disambiguation":"live","video":null,
           "artist-credit":[{"name":"Queen","artist":{"id":"0383dadf-2a4e-4d10-a46a-e9e041da8eb3","name":"Queen"}}],
           "first-release-date":"1991-03-01",
           "releases":[{"id":"3763af37-0dea-41d2-a54e-1ce6ffde9a5e","title":"Merry Christmas","status":"Bootleg","date":"1991-03-01","country":"IT",
              "release-group":{"id":"9bef2f1c-6231-4dac-a899-421929dd63e8","primary-type":"Album","secondary-types":["Live"]},
              "track-count":12,"media":[{"position":1,"track":[{"id":"7a94","number":"3","title":"Bohemian Rhapsody"}],"track-count":12}]}]},
          {"id":"edc25cef-b180-407d-847d-4787ddfd429f","score":87,"title":"Bohemian Rhapsody","artist-credit":[{"name":"Queen"}],"releases":[]}
        ]}
        """;

    // A recording with two credited artists (join phrase), community genres out of order, and ISRCs.
    private const string GreekRecordingJson = """
        {"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"Ξανά στη ζωή μου","length":225000,"video":false,
         "artist-credit":[{"name":"Χάρις Αλεξίου","joinphrase":" & ","artist":{"id":"a1","name":"Χάρις Αλεξίου"}},
                          {"name":"Δημήτρης Μητροπάνος","joinphrase":"","artist":{"id":"a2","name":"Δημήτρης Μητροπάνος"}}],
         "genres":[{"name":"laiko","count":2},{"name":"pop","count":9},{"name":"folk","count":0}],
         "isrcs":["GRA018500123"],
         "releases":[{"id":"11111111-1111-1111-1111-111111111111","title":"Το Τραγούδι","date":"1985","country":"GR","status":"Official",
                      "release-group":{"id":"22222222-2222-2222-2222-222222222222","primary-type":"Album","secondary-types":[]}}]}
        """;

    private const string ReleaseJson = """
        {"id":"3763AF37-0DEA-41D2-A54E-1CE6FFDE9A5E","title":"Merry Christmas","date":"1991-03-01","country":"IT",
         "artist-credit":[{"name":"Queen","joinphrase":""}],
         "label-info":[{"label":{"name":"Great Dane Records"},"catalog-number":null},{"label":{"name":"Great Dane Records"}}],
         "media":[
           {"position":1,"track-count":2,"tracks":[
              {"position":1,"number":"1","title":"Now I’m Here","recording":{"id":"3D123F7C-0099-4ED0-AF69-948BA5C54D04"},"artist-credit":[{"name":"Queen","joinphrase":""}]},
              {"position":2,"number":"2","title":"Ogre Battle","recording":{"id":"ccfa3d3b-0925-4940-bbf9-211dbbf5c75b"},"artist-credit":[{"name":"Queen","joinphrase":""}]}]},
           {"position":2,"track-count":1,"tracks":[
              {"position":1,"number":"1","title":"Bohemian Rhapsody","recording":{"id":"cc3e8691-321f-49af-98a1-d4a11f363e1d"},"artist-credit":[{"name":"Queen","joinphrase":""}]}]}
         ]}
        """;

    [Fact]
    public void ASearchAnswer_BecomesScoredHits_EachWithItsRecording()
    {
        var hits = MusicBrainzJson.ParseSearch(SearchJson);

        Assert.Equal(2, hits.Count);
        Assert.Equal(("cc3e8691-321f-49af-98a1-d4a11f363e1d", 100), (hits[0].RecordingId, hits[0].Score));   // ids are lower-cased
        var rec = hits[0].Recording;
        Assert.Equal(("Bohemian Rhapsody", "Queen", 145800L), (rec.Title, rec.ArtistCredit, rec.DurationMs));
        var release = Assert.Single(rec.Releases);
        Assert.Equal(("Merry Christmas", "1991-03-01", "IT", "Bootleg", "Album"), (release.Title, release.Date, release.Country, release.Status, release.PrimaryType));
        Assert.Equal(["Live"], release.SecondaryTypes);
        Assert.Equal("9bef2f1c-6231-4dac-a899-421929dd63e8", release.ReleaseGroupId);
        Assert.Equal(87, hits[1].Score);
        Assert.Null(hits[1].Recording.DurationMs);
    }

    [Fact]
    public void GreekNamesAndJoinPhrases_AreKeptExactly_AndGenresComeMostVotedFirst()
    {
        var rec = MusicBrainzJson.ParseRecording(GreekRecordingJson)!;

        Assert.Equal("Ξανά στη ζωή μου", rec.Title);
        Assert.Equal("Χάρις Αλεξίου & Δημήτρης Μητροπάνος", rec.ArtistCredit);
        Assert.Equal(["pop", "laiko", "folk"], rec.Genres);
        Assert.Equal(["GRA018500123"], rec.Isrcs);
        Assert.Equal("1985", rec.Releases[0].Date);
    }

    [Fact]
    public void AReleaseAnswer_ListsTheTracksOfEveryDisc_AndDeduplicatesLabels()
    {
        var rel = MusicBrainzJson.ParseRelease(ReleaseJson)!;

        Assert.Equal("3763af37-0dea-41d2-a54e-1ce6ffde9a5e", rel.Id);
        Assert.Equal("Queen", rel.AlbumArtistCredit);
        Assert.Equal(["Great Dane Records"], rel.Labels);
        Assert.Equal(3, rel.Tracks.Count);
        var last = rel.Tracks[2];
        Assert.Equal(("cc3e8691-321f-49af-98a1-d4a11f363e1d", 2, 1, 1), (last.RecordingId, last.DiscNumber, last.Position, last.TrackCount));
        Assert.Equal("3d123f7c-0099-4ed0-af69-948ba5c54d04", rel.Tracks[0].RecordingId);
        Assert.Equal("Now I’m Here", rel.Tracks[0].Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"id\":42,\"title\":[1]}")]
    public void UnusableAnswers_GiveNothing_AndNeverThrow(string json)
    {
        Assert.Null(MusicBrainzJson.ParseRecording(json));
        Assert.Null(MusicBrainzJson.ParseRelease(json));
        Assert.Empty(MusicBrainzJson.ParseSearch(json));
    }

    [Fact]
    public void ARecordingWithoutGenresOfItsOwn_FallsBackToTheArtistsGenres_AndSaysSo()
    {
        var rec = MusicBrainzJson.ParseRecording("""
            {"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"T","genres":[],
             "artist-credit":[{"name":"Queen","joinphrase":"","artist":{"name":"Queen","genres":[{"name":"rock","count":45},{"name":"glam rock","count":26},{"name":"afro house","count":1}]}}]}
            """)!;

        Assert.Equal(["rock", "glam rock", "afro house"], rec.Genres);          // most voted first
        Assert.Equal("artist", rec.GenreLevel);
    }

    [Fact]
    public void TheRecordingsOwnGenres_WinOverTheArtists()
    {
        var rec = MusicBrainzJson.ParseRecording("""
            {"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"T","genres":[{"name":"disco","count":3}],
             "artist-credit":[{"name":"Queen","artist":{"genres":[{"name":"rock","count":45}]}}]}
            """)!;

        Assert.Equal(["disco"], rec.Genres);
        Assert.Equal("recording", rec.GenreLevel);
    }

    [Fact]
    public void NoGenresAnywhere_LeavesTheLevelEmpty()
    {
        var rec = MusicBrainzJson.ParseRecording("""{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"T","genres":[],"artist-credit":[{"name":"X","artist":{"genres":[]}}]}""")!;

        Assert.Empty(rec.Genres);
        Assert.Null(rec.GenreLevel);
    }

    [Fact]
    public void OddlyShapedFields_AreIgnored_NotFatal()
    {
        var rec = MusicBrainzJson.ParseRecording("""
            {"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"T","length":"long","artist-credit":"Queen","releases":{"x":1},
             "genres":[{"name":5},{"name":"disco"}],"isrcs":[1,"X"]}
            """)!;

        Assert.Equal(("T", "", (long?)null), (rec.Title, rec.ArtistCredit, rec.DurationMs));
        Assert.Empty(rec.Releases);
        Assert.Equal(["disco"], rec.Genres);
        Assert.Equal(["X"], rec.Isrcs);
    }
}

public class MusicBrainzClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Id = "5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33";

    private sealed class Rig : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeHttpHandler Net { get; }
        public IdentificationHttp Http { get; }
        public MusicBrainzClient Client { get; }
        public FakeSettings Settings { get; } = new();

        public Rig()
        {
            Net = new FakeHttpHandler(() => Time);
            Http = new IdentificationHttp(new RateLimiter(Time), Time, () => Net);
            Client = new MusicBrainzClient(Http, Settings, new AppInfo("MusicFinder", "1.0.0"), Db.Identification, Time);
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

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Db.DisposeAsync();
        }
    }

    public sealed class FakeSettings : MusicFinder.Domain.Contracts.ISettingsStore
    {
        public AppSettings Current { get; set; } = new() { MusicBrainzContact = "dj@example.com" };
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    [Fact]
    public async Task ARecordingLookup_IsParsed_SentWithTheContact_AndRemembered()
    {
        await using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"Ξανά στη ζωή μου","artist-credit":[{"name":"Χάρις Αλεξίου"}]}""");

        var first = await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default));
        var second = await rig.RunAsync(rig.Client.GetRecordingAsync(Id.ToUpperInvariant(), default));

        Assert.Equal("Ξανά στη ζωή μου", first!.Title);
        Assert.Equal(first.Title, second!.Title);
        var request = Assert.Single(rig.Net.Requests);                      // the second answer came from the cache
        Assert.Equal("/ws/2/recording/" + Id, request.Uri.AbsolutePath);
        Assert.Contains("fmt=json", request.Uri.Query);
        Assert.Contains("inc=artist-credits+releases+release-groups+isrcs+genres", request.Uri.Query);
        Assert.Equal("MusicFinder/1.0.0 ( dj@example.com )", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task ACachedRecord_ExpiresAfterThirtyDays()
    {
        await using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","title":"T"}""");
        await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default));

        rig.Time.Advance(TimeSpan.FromDays(29));
        await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default));
        Assert.Single(rig.Net.Requests);

        rig.Time.Advance(TimeSpan.FromDays(2));
        await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default));
        Assert.Equal(2, rig.Net.Requests.Count);
    }

    [Fact]
    public async Task AnUnknownRecording_IsNull_AndNotCached()
    {
        await using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.NotFound, """{"error":"Not Found"}""");

        Assert.Null(await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default)));
        Assert.Null(await rig.RunAsync(rig.Client.GetRecordingAsync(Id, default)));

        Assert.Equal(2, rig.Net.Requests.Count);
        Assert.Equal(0, await rig.Db.Identification.CountAsync(default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33/../../x")]
    [InlineData("5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33?inc=all")]
    public async Task AnIdThatIsNotAUuid_NeverReachesTheNetwork(string id)
    {
        await using var rig = new Rig();

        Assert.Null(await rig.Client.GetRecordingAsync(id, default));
        Assert.Null(await rig.Client.GetReleaseAsync(id, default));
        Assert.Empty(rig.Net.Requests);
    }

    [Fact]
    public async Task AReleaseLookup_ReadsTheTrackList()
    {
        await using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """
            {"id":"11111111-1111-1111-1111-111111111111","title":"Το Τραγούδι","date":"1985","artist-credit":[{"name":"Χάρις Αλεξίου"}],
             "media":[{"position":1,"track-count":1,"tracks":[{"position":1,"title":"Ξανά στη ζωή μου","recording":{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33"}}]}]}
            """);

        var release = await rig.RunAsync(rig.Client.GetReleaseAsync("11111111-1111-1111-1111-111111111111", default));

        Assert.Equal("Το Τραγούδι", release!.Title);
        Assert.Equal(Id, release.Tracks.Single().RecordingId);
        Assert.Contains("inc=artist-credits+recordings+release-groups+labels+media", rig.Net.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task ASearch_EscapesTheQueryIntoOneParameter_AndLimitsTheCount()
    {
        await using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.OK, """{"recordings":[{"id":"5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33","score":91,"title":"T","artist-credit":[{"name":"A"}]}]}""");
        const string query = "recording:\"Σ & x=1#frag\" AND artist:\"A\"";

        var hits = await rig.RunAsync(rig.Client.SearchRecordingsAsync(query, 500, default));

        Assert.Equal(91, Assert.Single(hits).Score);
        var uri = rig.Net.Requests[0].Uri;
        Assert.Equal("/ws/2/recording", uri.AbsolutePath);
        Assert.Equal("", uri.Fragment);                                      // the '#' stayed inside the query value
        var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(query, parameters["query"]);
        Assert.Equal("25", parameters["limit"]);                             // clamped
        Assert.Equal("json", parameters["fmt"]);
        Assert.Equal(["query", "limit", "fmt"], parameters.AllKeys);         // nothing smuggled in
    }

    [Fact]
    public async Task ASearchThatFails_GivesNoHits_ButANetworkOutageSurfaces()
    {
        await using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.BadRequest, "{}");
        Assert.Empty(await rig.RunAsync(rig.Client.SearchRecordingsAsync("x", 5, default)));

        rig.Net.Respond(_ => throw new HttpRequestException("offline"));
        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Client.SearchRecordingsAsync("x", 5, default)));
        Assert.Equal(IdentificationFailure.Offline, ex.Failure);
    }

    [Fact]
    public async Task WithoutAContact_NoRequestIsSent()
    {
        await using var rig = new Rig();
        rig.Settings.Current = new AppSettings { MusicBrainzContact = "  " };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Client.SearchRecordingsAsync("x", 5, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Client.GetRecordingAsync(Id, default));

        Assert.Empty(rig.Net.Requests);
    }
}
