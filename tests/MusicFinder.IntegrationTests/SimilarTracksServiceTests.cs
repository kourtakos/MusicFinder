using Dapper;
using MusicFinder.Application.Similar;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence.Repositories;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>Find Similar end to end on a real database: candidates, exclusions, scoring and the order of the list.</summary>
public class SimilarTracksServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public SimilarTracksService Service { get; }
        public Dictionary<string, long> Ids { get; } = [];
        public long FolderId { get; private set; }

        public Env()
        {
            var search = new SqliteSearchService(Db.Factory);
            Service = new SimilarTracksService(new SimilarCandidateRepository(Db.Factory), Db.Genres, Db.TagService, search);
        }

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            await e.Db.SeedTaxonomyAsync();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            e.FolderId = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

            // The seed: a Disco track at 120 BPM in 8A, tagged Warm Up.
            await e.AddAsync("Seed", "Chic", "Disco", 120, "8A", year: 1978);
            await e.AddAsync("Same Everything", "Other", "Disco", 120, "8A");
            await e.AddAsync("Near Tempo, Adjacent Key", "Other", "Disco", 123, "9A");
            await e.AddAsync("Nu Disco Same Key", "Other", "Nu-Disco", 120, "8A");
            await e.AddAsync("Funk Same Tempo", "Other", "Funk", 120, "8A");
            await e.AddAsync("Double Time", "Other", "Disco", 240, "8A");
            await e.AddAsync("Far Away", "Other", "Techno", 170, "3B");
            await e.AddAsync("No BPM No Key", "Other", "Disco", null, null);
            await e.AddAsync("Same Artist", "Chic", "Disco", 121, "8A");
            await e.Db.GenreJob.RunAsync();
            return e;
        }

        public async Task AddAsync(string title, string artist, string genre, double? bpm, string? key, int? year = null)
        {
            var t = TestDb.MakeTrack(FolderId, $@"C:\Music\{title}.mp3", title, artist, genre);
            t.Bpm = bpm;
            t.MusicalKey = key;
            t.Year = year;
            await Db.Tracks.UpsertBatchAsync([t]);
            using var conn = Db.Factory.Open();
            Ids[title] = conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FullPath = @p", new { p = $@"C:\Music\{title}.mp3" });
        }

        public Task<SimilarTracksResult> FindAsync(string title, SimilarTracksOptions? options = null) =>
            Service.FindAsync(Ids[title], options ?? new SimilarTracksOptions(), default);

        public async Task<List<string>> TitlesAsync(string title, SimilarTracksOptions? options = null) =>
            (await FindAsync(title, options)).Items.Select(i => i.Row.Title).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task TheBestMatches_ComeFirst_WithTheirReasons()
    {
        await using var e = await Env.CreateAsync();

        var result = await e.FindAsync("Seed");

        Assert.Equal(SimilarityStatus.Ok, result.Status);
        Assert.Equal("Seed", result.Seed!.Title);
        var first = result.Items[0];
        Assert.Equal(("Same Everything", 100), (first.Row.Title, first.Score));
        Assert.Equal(["same genre (Disco)", "same BPM", "same key (8A)"], first.Reasons);
        // Double Time ties at 100 but needed doubled tempo, so the direct match goes first; then the near-tempo same-artist track at 96.
        Assert.Equal(["Same Everything", "Double Time", "Same Artist", "Nu Disco Same Key"], result.Items.Take(4).Select(i => i.Row.Title));
    }

    [Fact]
    public async Task TheSameFile_IsNeverInItsOwnList()
    {
        await using var e = await Env.CreateAsync();

        Assert.DoesNotContain("Seed", await e.TitlesAsync("Seed"));
    }

    [Fact]
    public async Task ATrackWithoutBpmOrKey_IsNotComparedByThem_AndNeedsEnoughOtherData()
    {
        await using var e = await Env.CreateAsync();

        // "No BPM No Key" only shares the genre: 35 of 100 available, below the floor, so it is not offered with an inflated score.
        Assert.DoesNotContain("No BPM No Key", await e.TitlesAsync("Seed"));
    }

    [Fact]
    public async Task ATrackWithTooLittleOfItsOwn_SaysSo_InsteadOfListingGuesses()
    {
        await using var e = await Env.CreateAsync();

        var result = await e.FindAsync("No BPM No Key");

        Assert.Equal(SimilarityStatus.InsufficientData, result.Status);
        Assert.Empty(result.Items);
        Assert.Contains("at least two of genre, BPM and key", result.Message);
        Assert.Contains("does not guess", result.Message);
    }

    [Fact]
    public async Task AMissingSeed_IsReported()
    {
        await using var e = await Env.CreateAsync();

        var result = await e.Service.FindAsync(987654, new SimilarTracksOptions(), default);

        Assert.Equal(SimilarityStatus.SeedNotFound, result.Status);
        Assert.Null(result.Seed);
    }

    [Fact]
    public async Task HalfAndDoubleTempo_AreOfferedOnlyWhenAllowed_AndLabelled()
    {
        await using var e = await Env.CreateAsync();

        var on = await e.FindAsync("Seed");
        var doubled = on.Items.Single(i => i.Row.Title == "Double Time");
        Assert.True(doubled.UsedHalfDoubleTempo);
        Assert.Contains("BPM ×2 (+0)", doubled.Reasons);

        var off = await e.TitlesAsync("Seed", new SimilarTracksOptions { AllowHalfDoubleTempo = false });
        Assert.Contains("Double Time", off);                                     // still similar by genre and key, but...
        var offDoubled = (await e.FindAsync("Seed", new SimilarTracksOptions { AllowHalfDoubleTempo = false })).Items.Single(i => i.Row.Title == "Double Time");
        Assert.False(offDoubled.UsedHalfDoubleTempo);
        Assert.True(offDoubled.Score < doubled.Score);                           // ...with no credit for the tempo
    }

    [Fact]
    public async Task ATrackThatIsFarInEveryWay_IsNotFound()
    {
        await using var e = await Env.CreateAsync();

        Assert.DoesNotContain("Far Away", await e.TitlesAsync("Seed"));
    }

    [Fact]
    public async Task TheListIsLimitedToMaxResults()
    {
        await using var e = await Env.CreateAsync();

        var result = await e.FindAsync("Seed", new SimilarTracksOptions { MaxResults = 2 });

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task TracksPlayedRecently_CanBeLeftOut()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.Plays.AddAsync(new PlayRecord
        {
            TrackId = e.Ids["Same Everything"], PlayedUtc = T0, Status = "Possible", Source = PlaySources.InactivityHeuristic, ExternalEventKey = "k1"
        });

        Assert.Contains("Same Everything", await e.TitlesAsync("Seed"));
        Assert.DoesNotContain("Same Everything", await e.TitlesAsync("Seed", new SimilarTracksOptions { ExcludePlayedSinceUtc = T0.AddHours(-1) }));
        Assert.Contains("Same Everything", await e.TitlesAsync("Seed", new SimilarTracksOptions { ExcludePlayedSinceUtc = T0.AddHours(1) }));   // played before that
    }

    [Fact]
    public async Task TracksAlreadyPlayedInTheSession_CanBeLeftOut()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Db.Sessions.StartAsync(T0, null, "tonight", false);
        await e.Db.Plays.AddAsync(new PlayRecord
        {
            TrackId = e.Ids["Same Artist"], PlayedUtc = T0, Status = "Confirmed", Source = PlaySources.User, SessionId = session, ExternalEventKey = "k2"
        });

        Assert.DoesNotContain("Same Artist", await e.TitlesAsync("Seed", new SimilarTracksOptions { ExcludeSessionId = session }));
        Assert.Contains("Same Artist", await e.TitlesAsync("Seed", new SimilarTracksOptions { ExcludeSessionId = session + 99 }));
    }

    [Fact]
    public async Task MissingFiles_AreNotSuggested()
    {
        await using var e = await Env.CreateAsync();
        using (var conn = e.Db.Factory.Open())
            conn.Execute("UPDATE Tracks SET Missing = 1 WHERE Id = @id", new { id = e.Ids["Same Everything"] });

        Assert.DoesNotContain("Same Everything", await e.TitlesAsync("Seed"));
    }

    [Fact]
    public async Task SharedDjTags_AddToTheScore_AndAreExplained()
    {
        await using var e = await Env.CreateAsync();
        var warmUp = (await e.Db.Tags.GetAllAsync()).Single(t => t.Name == "Warm Up").Id;
        await e.Db.TagService.AssignAsync([e.Ids["Seed"], e.Ids["Near Tempo, Adjacent Key"]], warmUp);

        var result = await e.FindAsync("Seed");

        var near = result.Items.Single(i => i.Row.Title == "Near Tempo, Adjacent Key");
        Assert.Contains("shared tag: Warm Up", near.Reasons);
        Assert.Equal(100, near.AvailableWeight);                                  // tags now count (the seed has one)
        Assert.Equal(90, result.Items.Single(i => i.Row.Title == "Same Everything").Score);   // lacks the tag: 90 of 100
    }

    [Fact]
    public async Task TheOrder_IsTheSameEveryTime()
    {
        await using var e = await Env.CreateAsync();

        var first = await e.TitlesAsync("Seed");
        var second = await e.TitlesAsync("Seed");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TheRowsCarryTheirScoreInformationAlongsideTheLibraryColumns()
    {
        await using var e = await Env.CreateAsync();

        var item = (await e.FindAsync("Seed")).Items.Single(i => i.Row.Title == "Near Tempo, Adjacent Key");

        Assert.Equal(("Other", 123d, "9A"), (item.Row.Artist, item.Row.Bpm, item.Row.MusicalKey));
        Assert.Equal(["same genre (Disco)", "BPM +3", "adjacent key (8A → 9A)"], item.Reasons);
        Assert.Equal(82, item.Score);
    }
}
