using Dapper;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.IntegrationTests;

public class IdentificationCacheTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2025, 1, 1, 8, 30, 15, 123, DateTimeKind.Utc);
    private static readonly FingerprintResult Fp = new("AQADtEmSJEmUJEmS", 215, 2, 120);

    private static async Task<long> AddTrackAsync(TestDb db, string name = "a.mp3")
    {
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\" + name, "T", "A")]);
        using var conn = db.Factory.Open();
        return conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @name", new { name });
    }

    [Fact]
    public async Task Migration0007_UpgradesAnOlderDatabase_AndRecreatesTheCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-ident-upgrade-" + Guid.NewGuid().ToString("N"));
        SqliteConnectionFactory? factory = null;
        try
        {
            factory = new SqliteConnectionFactory(Path.Combine(dir, "old.db"));
            new MigrationRunner(factory).Migrate(maxVersion: 6);
            using (var conn = factory.Open())
                Assert.Contains("AcoustId", conn.Query<string>("SELECT name FROM pragma_table_info('IdentificationCache')")); // the old shape

            Assert.Equal(MigrationRunner.LatestVersion - 6, new MigrationRunner(factory).Migrate());

            using var after = factory.Open();
            var columns = after.Query<string>("SELECT name FROM pragma_table_info('IdentificationCache')").ToList();
            Assert.Contains("FileSize", columns);
            Assert.DoesNotContain("AcoustId", columns);
            Assert.Equal(0, after.ExecuteScalar<int>("SELECT COUNT(*) FROM IdentificationLookups"));
            Assert.Equal(0, after.ExecuteScalar<int>("SELECT COUNT(*) FROM MusicBrainzCache"));
        }
        finally
        {
            factory?.ReleasePool();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AFingerprint_IsReused_OnlyWhileTheFileIsUnchanged()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutFingerprintAsync(id, 5000, Modified, Fp, Now, default);

        var same = await db.Identification.GetFingerprintAsync(id, 5000, Modified, default);
        Assert.Equal(("AQADtEmSJEmUJEmS", 215, 2, 120), (same!.Fingerprint, same.DurationSeconds, same.Algorithm, same.LengthSeconds));

        Assert.Null(await db.Identification.GetFingerprintAsync(id, 5001, Modified, default));                 // size changed
        Assert.Null(await db.Identification.GetFingerprintAsync(id, 5000, Modified.AddSeconds(1), default));   // retagged / replaced
        Assert.Null(await db.Identification.GetFingerprintAsync(id + 99, 5000, Modified, default));            // another track
    }

    [Fact]
    public async Task AFingerprint_IsReplaced_NotDuplicated()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutFingerprintAsync(id, 5000, Modified, Fp, Now, default);

        await db.Identification.PutFingerprintAsync(id, 6000, Modified.AddDays(1), Fp with { Fingerprint = "NEW" }, Now, default);

        Assert.Null(await db.Identification.GetFingerprintAsync(id, 5000, Modified, default));
        Assert.Equal("NEW", (await db.Identification.GetFingerprintAsync(id, 6000, Modified.AddDays(1), default))!.Fingerprint);
        Assert.Equal(1, await db.Identification.CountAsync(default));
    }

    [Fact]
    public async Task TheFingerprint_GoesWithItsTrack()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutFingerprintAsync(id, 5000, Modified, Fp, Now, default);

        await db.Writes.EnqueueAsync((c, t) => c.Execute("DELETE FROM Tracks WHERE Id = @id", new { id }, t));

        Assert.Equal(0, await db.Identification.CountAsync(default));
    }

    [Fact]
    public async Task ALookup_IsFoundUntilItExpires_AndFoundAndNoMatchAreKeptApart()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutLookupAsync("key-found", id, IdentificationMethod.AcoustIdFingerprint, LookupOutcome.Found, "{\"x\":1}", Now, Now.AddDays(30), default);
        await db.Identification.PutLookupAsync("key-none", id, IdentificationMethod.MusicBrainzTagSearch, LookupOutcome.NoMatch, "[]", Now, Now.AddDays(7), default);

        var found = await db.Identification.GetLookupAsync("key-found", Now.AddDays(29), default);
        Assert.Equal((LookupOutcome.Found, IdentificationMethod.AcoustIdFingerprint, "{\"x\":1}"), (found!.Outcome, found.Method, found.ResultJson));
        Assert.Equal(Now, found.CheckedUtc);

        Assert.NotNull(await db.Identification.GetLookupAsync("key-none", Now.AddDays(6), default));
        Assert.Null(await db.Identification.GetLookupAsync("key-none", Now.AddDays(8), default));              // negative result expired sooner
        Assert.NotNull(await db.Identification.GetLookupAsync("key-found", Now.AddDays(8), default));
        Assert.Null(await db.Identification.GetLookupAsync("key-found", Now.AddDays(31), default));
        Assert.Null(await db.Identification.GetLookupAsync("never-asked", Now, default));
    }

    [Fact]
    public async Task ALookup_SurvivesItsTrackBeingRemoved_AndIsSharedByKey()
    {
        await using var db = new TestDb();
        var a = await AddTrackAsync(db, "a.mp3");
        await db.Identification.PutLookupAsync("same-song", a, IdentificationMethod.AcoustIdFingerprint, LookupOutcome.Found, "{}", Now, Now.AddDays(30), default);

        await db.Writes.EnqueueAsync((c, t) => c.Execute("DELETE FROM Tracks WHERE Id = @a", new { a }, t));

        Assert.NotNull(await db.Identification.GetLookupAsync("same-song", Now, default));
    }

    [Fact]
    public async Task AStoredLookup_IsOverwrittenByTheSameKey()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutLookupAsync("k", id, IdentificationMethod.MusicBrainzTagSearch, LookupOutcome.NoMatch, "[]", Now, Now.AddDays(7), default);

        await db.Identification.PutLookupAsync("k", id, IdentificationMethod.MusicBrainzTagSearch, LookupOutcome.Found, "[1]", Now.AddDays(8), Now.AddDays(38), default);

        var row = await db.Identification.GetLookupAsync("k", Now.AddDays(9), default);
        Assert.Equal((LookupOutcome.Found, "[1]"), (row!.Outcome, row.ResultJson));
        Assert.Equal(1, await db.Identification.CountAsync(default));
    }

    [Fact]
    public async Task MusicBrainzRecords_AreCachedById_CaseInsensitively_UntilTheyExpire()
    {
        await using var db = new TestDb();
        const string id = "5F2C9A0E-7B1D-4C3A-9E84-0D6B2A1F7C33";
        await db.Identification.PutEntityAsync("recording", id, "{\"title\":\"Ξανά στη ζωή μου\"}", Now, Now.AddDays(30), default);

        Assert.Equal("{\"title\":\"Ξανά στη ζωή μου\"}", await db.Identification.GetEntityAsync("recording", id.ToLowerInvariant(), Now.AddDays(1), default));
        Assert.Null(await db.Identification.GetEntityAsync("release", id, Now, default));                       // another kind of record
        Assert.Null(await db.Identification.GetEntityAsync("recording", id, Now.AddDays(31), default));
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        await using var db = new TestDb();
        var id = await AddTrackAsync(db);
        await db.Identification.PutFingerprintAsync(id, 5000, Modified, Fp, Now, default);
        await db.Identification.PutLookupAsync("k", id, IdentificationMethod.AcoustIdFingerprint, LookupOutcome.Found, "{}", Now, Now.AddDays(1), default);
        await db.Identification.PutEntityAsync("release", "r", "{}", Now, Now.AddDays(1), default);
        Assert.Equal(3, await db.Identification.CountAsync(default));

        await db.Identification.ClearAsync(default);

        Assert.Equal(0, await db.Identification.CountAsync(default));
    }
}
