using Dapper;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.IntegrationTests;

public class TrackAnalysisStoreTests
{
    private static readonly DateTime Modified = new(2025, 1, 1, 8, 30, 15, 123, DateTimeKind.Utc);
    private static readonly DateTime Analyzed = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static TrackAnalysis Sample(long trackId, int version = 1, bool bpm = true, bool key = true) => new(
        trackId, version,
        bpm ? new BpmEstimate(123.5, 0.82, Confidence.High, [new BpmAlternative(247, "double", 0.31), new BpmAlternative(61.75, "half", 0.22)], "octave-ambiguous") : null,
        key ? new KeyEstimate("8A", 0.74, Confidence.Medium, "8B", "relative-ambiguous", -12.5) : null,
        90, "MediaFoundation", bpm || key ? null : "mostly silent", Analyzed);

    private static async Task<long> AddTrackAsync(TestDb db, string name = "a.mp3", long? folderParent = null, string folder = "Music", double? bpm = null, string? key = null, bool missing = false)
    {
        var root = await db.Roots.AddAsync(@"C:\Music\" + Guid.NewGuid().ToString("N")[..6]);
        var folderId = await db.Folders.UpsertAsync(root.Id, folderParent, root.Path, folder);
        var track = TestDb.MakeTrack(folderId, Path.Combine(root.Path, name), "T", "A");
        track.Bpm = bpm;
        track.MusicalKey = key;
        track.Missing = missing;
        await db.Tracks.UpsertBatchAsync([track]);
        using var conn = db.Factory.Open();
        return conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FullPath = @p", new { p = track.FullPath });
    }

    [Fact]
    public async Task Migration0008_UpgradesAnOlderDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-analysis-upgrade-" + Guid.NewGuid().ToString("N"));
        SqliteConnectionFactory? factory = null;
        try
        {
            factory = new SqliteConnectionFactory(Path.Combine(dir, "old.db"));
            new MigrationRunner(factory).Migrate(maxVersion: 7);
            using (var conn = factory.Open())
                Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'TrackAnalysis'"));

            Assert.Equal(MigrationRunner.LatestVersion - 7, new MigrationRunner(factory).Migrate());

            using var after = factory.Open();
            Assert.Equal(1, after.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'TrackAnalysis'"));
        }
        finally
        {
            factory?.ReleasePool();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AnAnalysis_RoundTrips_WithItsAlternativesAndFlags_WhileTheFileIsUnchanged()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id), 5000, Modified, default);

        var back = await db.Analysis.GetAsync(id, 5000, Modified, 1, default);

        Assert.NotNull(back);
        Assert.Equal((123.5, Confidence.High, "octave-ambiguous"), (back!.Bpm!.Bpm, back.Bpm.Level, back.Bpm.Flags));
        Assert.Equal([("double", 247d), ("half", 61.75)], back.Bpm.Alternatives.Select(a => (a.Label, a.Bpm)));
        Assert.Equal(("8A", Confidence.Medium, "8B", -12.5), (back.Key!.Camelot, back.Key.Level, back.Key.Runner, back.Key.TuningCents));
        Assert.Equal(("MediaFoundation", 90d), (back.Decoder, back.AnalyzedSeconds));
        Assert.Equal(Analyzed, back.AnalyzedUtc);
    }

    [Fact]
    public async Task AnAnalysis_IsIgnored_WhenTheFileOrTheAnalyzerChanged()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id), 5000, Modified, default);

        Assert.Null(await db.Analysis.GetAsync(id, 5001, Modified, 1, default));                  // a different size
        Assert.Null(await db.Analysis.GetAsync(id, 5000, Modified.AddSeconds(1), 1, default));    // modified since
        Assert.Null(await db.Analysis.GetAsync(id, 5000, Modified, 2, default));                  // the analyzer was improved
        Assert.Null(await db.Analysis.GetAsync(id + 99, 5000, Modified, 1, default));
    }

    [Fact]
    public async Task NoEstimate_IsAStoredResult_NotAMissingOne()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id, bpm: false, key: false), 5000, Modified, default);

        var back = await db.Analysis.GetAsync(id, 5000, Modified, 1, default);

        Assert.NotNull(back);
        Assert.Null(back!.Bpm);
        Assert.Null(back.Key);
        Assert.Equal("mostly silent", back.Message);
    }

    [Fact]
    public async Task OneEstimateWithoutTheOther_IsKept()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id, key: false), 5000, Modified, default);

        var back = await db.Analysis.GetAsync(id, 5000, Modified, 1, default);

        Assert.NotNull(back!.Bpm);
        Assert.Null(back.Key);
    }

    [Fact]
    public async Task PuttingAgain_ReplacesTheRow()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id), 5000, Modified, default);

        await db.Analysis.PutAsync(Sample(id, version: 2, key: false), 6000, Modified.AddDays(1), default);

        Assert.Equal(1, await db.Analysis.CountAsync(default));
        Assert.Null(await db.Analysis.GetAsync(id, 5000, Modified, 1, default));
        var back = await db.Analysis.GetAsync(id, 6000, Modified.AddDays(1), 2, default);
        Assert.Null(back!.Key);
    }

    [Fact]
    public async Task AfterATagWrite_TheRowFollowsTheFilesNewIdentity()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Analysis.PutAsync(Sample(id), 5000, Modified, default);

        await db.Analysis.RebindAsync(id, 5200, Modified.AddMinutes(5), default);

        Assert.Null(await db.Analysis.GetAsync(id, 5000, Modified, 1, default));
        Assert.NotNull(await db.Analysis.GetAsync(id, 5200, Modified.AddMinutes(5), 1, default));
    }

    [Fact]
    public async Task TheRow_GoesWithItsTrack_AndClearRemovesAll()
    {
        await using var db = new TestDb();
        var a = await AddTrackAsync(db, "a.mp3");
        var b = await AddTrackAsync(db, "b.mp3");
        await db.Analysis.PutAsync(Sample(a), 5000, Modified, default);
        await db.Analysis.PutAsync(Sample(b), 5000, Modified, default);

        await db.Writes.EnqueueAsync((c, t) => c.Execute("DELETE FROM Tracks WHERE Id = @a", new { a }, t));
        Assert.Equal(1, await db.Analysis.CountAsync(default));

        await db.Analysis.ClearAsync(default);
        Assert.Equal(0, await db.Analysis.CountAsync(default));
    }

    [Fact]
    public async Task Candidates_AreTheTracksThatLackABpmOrAKey_NeverMissingFiles()
    {
        await using var db = new TestDb();
        var none = await AddTrackAsync(db, "none.mp3");
        var onlyBpm = await AddTrackAsync(db, "bpm.mp3", bpm: 128);
        var onlyKey = await AddTrackAsync(db, "key.mp3", key: "8A");
        var both = await AddTrackAsync(db, "both.mp3", bpm: 128, key: "8A");
        var blankKey = await AddTrackAsync(db, "blank.mp3", bpm: 128, key: "   ");
        var zeroBpm = await AddTrackAsync(db, "zero.mp3", bpm: 0, key: "8A");
        var gone = await AddTrackAsync(db, "gone.mp3", missing: true);

        var candidates = await db.Analysis.FindCandidatesAsync(new AnalysisScope(), default);

        Assert.Equal(new[] { none, onlyBpm, onlyKey, blankKey, zeroBpm }.Order(), candidates);
        Assert.DoesNotContain(both, candidates);
        Assert.DoesNotContain(gone, candidates);
    }

    [Fact]
    public async Task Candidates_CanBeLimitedToAFolder_WithOrWithoutItsSubfolders()
    {
        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Lib");
        var top = await db.Folders.UpsertAsync(root.Id, null, @"C:\Lib", "Lib");
        var sub = await db.Folders.UpsertAsync(root.Id, top, @"C:\Lib\Sub", "Sub");
        var other = await db.Folders.UpsertAsync(root.Id, null, @"C:\Other", "Other");
        await db.Tracks.UpsertBatchAsync([
            TestDb.MakeTrack(top, @"C:\Lib\a.mp3", "A"), TestDb.MakeTrack(sub, @"C:\Lib\Sub\b.mp3", "B"), TestDb.MakeTrack(other, @"C:\Other\c.mp3", "C")]);
        using var conn = db.Factory.Open();
        long IdOf(string file) => conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @file", new { file });

        var recursive = await db.Analysis.FindCandidatesAsync(new AnalysisScope(top, Recursive: true), default);
        var direct = await db.Analysis.FindCandidatesAsync(new AnalysisScope(top, Recursive: false), default);

        Assert.Equal([IdOf("a.mp3"), IdOf("b.mp3")], recursive);
        Assert.Equal([IdOf("a.mp3")], direct);
    }

    [Fact]
    public async Task Candidates_AreCapped()
    {
        await using var db = new TestDb();
        for (var i = 0; i < 5; i++) await AddTrackAsync(db, $"t{i}.mp3");

        Assert.Equal(3, (await db.Analysis.FindCandidatesAsync(new AnalysisScope(Limit: 3), default)).Count);
    }
}
