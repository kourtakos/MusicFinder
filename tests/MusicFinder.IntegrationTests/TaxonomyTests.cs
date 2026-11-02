using Dapper;
using MusicFinder.Application.Services;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.IntegrationTests;

public class TaxonomyTests
{
    [Fact]
    public async Task Seeding_CreatesHierarchyAliasesAndDjTags()
    {
        await using var db = new TestDb();
        await db.SeedTaxonomyAsync();

        var genres = await db.Genres.GetAllAsync();
        var byName = genres.ToDictionary(g => g.Name);
        Assert.Equal(94, genres.Count); // 16 top-level categories + 78 sub-genres in the starter taxonomy
        Assert.All(genres, g => Assert.Equal(TextNormalizer.Normalize(g.Name), g.NormalizedName));
        Assert.All(genres, g => Assert.True(g.IsBuiltIn));

        Assert.Null(byName["Disco"].ParentId);
        Assert.Equal(byName["Disco"].Id, byName["Nu Disco"].ParentId);
        Assert.Equal(byName["Greek"].Id, byName["Laiko"].ParentId);

        var aliases = (await db.Genres.GetAliasesAsync()).ToDictionary(a => a.AliasNormalized, a => a.GenreId);
        Assert.Equal(byName["Nu Disco"].Id, aliases["nudisco"]);          // alias to a sub-genre
        Assert.Equal(byName["Laiko"].Id, aliases[TextNormalizer.Normalize("λαϊκά")]); // Greek alias, accent-folded key
        Assert.Equal(byName["Greek"].Id, aliases[TextNormalizer.Normalize("ελληνικά")]); // bare alias -> parent

        var tags = (await db.Tags.GetAllAsync()).Select(t => t.Name).ToList();
        Assert.Equal(["Warm Up", "Peak Time", "Closing", "Wedding", "Greek Party"], tags);
    }

    [Fact]
    public async Task Seeding_IsIdempotent_AndDoesNotResurrectDeletedBuiltIns()
    {
        await using var db = new TestDb();
        await db.SeedTaxonomyAsync();
        var first = (await db.Genres.GetAllAsync()).Count;
        var firstAliases = (await db.Genres.GetAliasesAsync()).Count;

        await db.SeedTaxonomyAsync();
        Assert.Equal(first, (await db.Genres.GetAllAsync()).Count);
        Assert.Equal(firstAliases, (await db.Genres.GetAliasesAsync()).Count);

        // The user owns the taxonomy afterwards: a deleted built-in stays deleted across restarts.
        await db.Writes.EnqueueAsync((c, t) =>
        {
            c.Execute("DELETE FROM GenreAliases WHERE GenreId IN (SELECT Id FROM Genres WHERE Name = 'Skyladiko')", transaction: t);
            c.Execute("DELETE FROM Genres WHERE Name = 'Skyladiko'", transaction: t);
        });
        await db.SeedTaxonomyAsync();
        Assert.DoesNotContain(await db.Genres.GetAllAsync(), g => g.Name == "Skyladiko");
    }

    [Fact]
    public async Task Migration0002_UpgradesAnExistingM1Database_WithoutLosingData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-upgrade-" + Guid.NewGuid().ToString("N"));
        SqliteConnectionFactory? factory = null;
        try
        {
            factory = new SqliteConnectionFactory(Path.Combine(dir, "old.db"));
            Assert.Equal(1, new MigrationRunner(factory).Migrate(maxVersion: 1)); // an M1 database

            await using (var writes = new SqliteWriteQueue(factory))
            {
                await writes.EnqueueAsync((c, t) =>
                {
                    c.Execute("INSERT INTO LibraryRoots(Path) VALUES ('C:\\Music')", transaction: t);
                    c.Execute("INSERT INTO Folders(RootId, FullPath, Name) VALUES (1, 'C:\\Music', 'Music')", transaction: t);
                    c.Execute(
                        """
                        INSERT INTO Tracks(FolderId, FullPath, FileName, Extension, FileSize, ModifiedUtc, IndexedUtc, Title, GenreRaw, NormalizedText)
                        VALUES (1, 'C:\Music\a.mp3', 'a.mp3', '.mp3', 10, '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z', 'Le Freak', 'Disco', 'a mp3 le freak disco')
                        """, transaction: t);
                });
            }

            Assert.Equal(MigrationRunner.LatestVersion - 1, new MigrationRunner(factory).Migrate()); // everything after 0001

            using var conn = factory.Open();
            Assert.Equal(MigrationRunner.LatestVersion, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SchemaVersions"));
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT MetaVersion FROM Tracks")); // old rows are re-read once by the scanner
            Assert.Equal("Le Freak", conn.ExecuteScalar<string>("SELECT Title FROM Tracks"));
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT GenreMapVersion FROM Tracks")); // stale until mapped
            Assert.Equal("1", conn.ExecuteScalar<string>("SELECT Value FROM AppState WHERE Key = 'TaxonomyVersion'"));
            Assert.Single(conn.Query<long>("SELECT rowid FROM TrackSearch WHERE TrackSearch MATCH 'freak*'"));
        }
        finally
        {
            factory?.ReleasePool();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
