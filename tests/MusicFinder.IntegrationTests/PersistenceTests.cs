using Dapper;
using MusicFinder.Domain.Enums;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.IntegrationTests;

public class PersistenceTests
{
    [Fact]
    public async Task Migrations_AreIdempotent_AndFts5IsAvailable()
    {
        await using var db = new TestDb();
        Assert.Equal(0, new MigrationRunner(db.Factory).Migrate()); // already applied by TestDb

        using var conn = db.Factory.Open();
        Assert.Equal("wal", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, conn.ExecuteScalar<int>("PRAGMA foreign_keys"));
        Assert.Equal(MigrationRunner.LatestVersion, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SchemaVersions"));
    }

    [Fact]
    public async Task FtsTriggers_ReflectInsertUpdateDelete()
    {
        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

        await db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco")]);
        var id = (await db.Tracks.GetIndexedFilesAsync(root.Id)).Single().Id;

        Assert.Equal([id], db.Match("Freak*"));
        Assert.Equal([id], db.Match("Artist:chic"));
        Assert.Empty(db.Match("Superstition*"));

        // Update via upsert on the same path -> old terms gone, new terms present.
        await db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\Chic - Le Freak.mp3", "Superstition", "Stevie Wonder", "Funk")]);
        Assert.Empty(db.Match("Title:Freak*"));
        Assert.Equal([id], db.Match("Superstition"));

        await db.Writes.EnqueueAsync((c, t) => c.Execute("DELETE FROM Tracks WHERE Id = @id", new { id }, t));
        Assert.Empty(db.Match("Superstition"));
    }

    [Fact]
    public async Task NormalizedText_MatchesGreekWithoutAccents()
    {
        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(folder, @"C:\Music\Άλλο.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου")]);

        Assert.Single(db.Match("NormalizedText:αλεξιου*"));
        Assert.Single(db.Match("NormalizedText:χαρισ"));
    }

    [Fact]
    public async Task Roots_AddIsIdempotent_AndStatusUpdates()
    {
        await using var db = new TestDb();
        var a = await db.Roots.AddAsync(@"D:\DJ Music");
        var b = await db.Roots.AddAsync(@"D:\DJ Music");
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(ScanStatus.Pending, a.Status);

        var when = new DateTime(2025, 1, 26, 14, 27, 0, DateTimeKind.Utc);
        await db.Roots.UpdateStatusAsync(a.Id, ScanStatus.Indexed, when);
        var reloaded = (await db.Roots.GetAsync(a.Id))!;
        Assert.Equal(ScanStatus.Indexed, reloaded.Status);
        Assert.Equal(when, reloaded.LastScanUtc);
        Assert.Equal(DateTimeKind.Utc, reloaded.LastScanUtc!.Value.Kind);
    }

    [Fact]
    public async Task FolderCounts_AreDirectAndRecursive_AndMissingTracksExcluded()
    {
        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var top = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        var disco = await db.Folders.UpsertAsync(root.Id, top, @"C:\Music\Disco", "Disco");
        var nu = await db.Folders.UpsertAsync(root.Id, disco, @"C:\Music\Disco\Nu Disco", "Nu Disco");

        await db.Tracks.UpsertBatchAsync(
        [
            TestDb.MakeTrack(top, @"C:\Music\a.mp3"),
            TestDb.MakeTrack(disco, @"C:\Music\Disco\b.mp3"),
            TestDb.MakeTrack(disco, @"C:\Music\Disco\c.mp3"),
            TestDb.MakeTrack(nu, @"C:\Music\Disco\Nu Disco\d.mp3"),
        ]);
        await db.Folders.RecomputeCountsAsync(root.Id);

        var f = async (long id) => (await db.Folders.GetAsync(id))!;
        Assert.Equal((1, 4), ((await f(top)).DirectTrackCount, (await f(top)).RecursiveTrackCount));
        Assert.Equal((2, 3), ((await f(disco)).DirectTrackCount, (await f(disco)).RecursiveTrackCount));
        Assert.Equal((1, 1), ((await f(nu)).DirectTrackCount, (await f(nu)).RecursiveTrackCount));

        // A track not seen in a later scan is flagged Missing (never deleted) and leaves the counts.
        var scanStart = DateTime.UtcNow.AddMinutes(1);
        var seen = (await db.Tracks.GetIndexedFilesAsync(root.Id)).Where(x => !x.FullPath.EndsWith("d.mp3")).Select(x => x.Id).ToList();
        await db.Tracks.TouchAsync(seen, scanStart.AddSeconds(1));
        Assert.Equal(1, await db.Tracks.MarkMissingAsync(root.Id, scanStart));
        await db.Folders.RecomputeCountsAsync(root.Id);

        Assert.Equal(3, await db.Tracks.CountAsync());
        Assert.Equal(0, (await f(nu)).RecursiveTrackCount);
        Assert.Equal(3, (await f(top)).RecursiveTrackCount);
        Assert.Equal(4, (await db.Tracks.GetIndexedFilesAsync(root.Id)).Count); // still in DB
    }

    [Fact]
    public async Task WriteQueue_RollsBackFailedJob_AndKeepsRunning()
    {
        await using var db = new TestDb();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Writes.EnqueueAsync((c, t) =>
        {
            c.Execute("INSERT INTO LibraryRoots(Path) VALUES ('X')", transaction: t);
            c.Execute("INSERT INTO nonexistent VALUES (1)", transaction: t);
        }));
        Assert.Empty(await db.Roots.GetAllAsync());
        await db.Roots.AddAsync("Y");
        Assert.Single(await db.Roots.GetAllAsync());
    }
}
