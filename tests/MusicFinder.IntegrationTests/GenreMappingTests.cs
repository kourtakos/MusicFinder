using Dapper;

namespace MusicFinder.IntegrationTests;

public class GenreMappingTests
{
    private sealed record Env(TestDb Db, long RootId, long FolderId);

    private static async Task<Env> CreateAsync()
    {
        var db = new TestDb();
        await db.SeedTaxonomyAsync();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        return new Env(db, root.Id, folder);
    }

    private static async Task<long> AddTrack(Env e, string file, string? genre)
    {
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, $@"C:\Music\{file}", file, "Artist", genre)]);
        return (await e.Db.Tracks.GetIndexedFilesAsync(e.RootId)).Single(f => f.FullPath.EndsWith(file)).Id;
    }

    private static async Task<List<string>> GenresOf(Env e, long trackId, string? source = null)
    {
        using var conn = e.Db.Factory.Open();
        return (await conn.QueryAsync<string>(
            """
            SELECT g.Name FROM TrackGenres tg JOIN Genres g ON g.Id = tg.GenreId
            WHERE tg.TrackId = @trackId AND (@source IS NULL OR tg.Source = @source) ORDER BY g.Name
            """, new { trackId, source })).AsList();
    }

    private static int MapVersion(Env e, long trackId)
    {
        using var conn = e.Db.Factory.Open();
        return conn.ExecuteScalar<int>("SELECT GenreMapVersion FROM Tracks WHERE Id = @trackId", new { trackId });
    }

    [Fact]
    public async Task Job_MapsRawGenresToTaxonomy_AndKeepsTheRawText()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var multi = await AddTrack(e, "multi.mp3", "Disco;Funk");
        var nu = await AddTrack(e, "nu.mp3", "Nu-Disco");
        var greek = await AddTrack(e, "greek.mp3", "Λαϊκά");
        var unknown = await AddTrack(e, "unknown.mp3", "Vaporwave");
        var blank = await AddTrack(e, "blank.mp3", null);

        Assert.Equal(5, await e.Db.GenreJob.RunAsync());

        Assert.Equal(["Disco", "Funk"], await GenresOf(e, multi));
        Assert.Equal(["Nu Disco"], await GenresOf(e, nu));
        Assert.Equal(["Laiko"], await GenresOf(e, greek));
        Assert.Empty(await GenresOf(e, unknown));
        Assert.Empty(await GenresOf(e, blank));

        // Provenance, and the embedded text is never discarded.
        Assert.Equal(["Disco", "Funk"], await GenresOf(e, multi, "Embedded"));
        Assert.Equal("Disco;Funk", (await e.Db.Tracks.GetAsync(multi))!.GenreRaw);
        Assert.Equal("Vaporwave", (await e.Db.Tracks.GetAsync(unknown))!.GenreRaw);

        Assert.Equal(1, MapVersion(e, multi));
        Assert.Equal(1, MapVersion(e, blank)); // unmapped/blank tracks are stamped too, so they are not retried forever
    }

    [Fact]
    public async Task Job_IsANoOp_WhenNothingIsStale()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        await AddTrack(e, "a.mp3", "Disco");
        Assert.Equal(1, await e.Db.GenreJob.RunAsync());
        Assert.Equal(0, await e.Db.GenreJob.RunAsync());
    }

    [Fact]
    public async Task RereadingAFile_MarksItStale_AndRemapsOnlyThatTrack()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var a = await AddTrack(e, "a.mp3", "Disco");
        var b = await AddTrack(e, "b.mp3", "Funk");
        await e.Db.GenreJob.RunAsync();

        // The scanner upserts a file when its size/mtime changed: new tag text -> mapping becomes stale.
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, @"C:\Music\a.mp3", "a.mp3", "Artist", "Jazz")]);
        Assert.Equal(0, MapVersion(e, a));
        Assert.Equal(1, MapVersion(e, b));

        Assert.Equal(1, await e.Db.GenreJob.RunAsync());
        Assert.Equal(["Jazz"], await GenresOf(e, a));
        Assert.Equal(["Funk"], await GenresOf(e, b));
    }

    [Fact]
    public async Task EditingTheTaxonomy_RemapsEverything_AndUserAssignmentsSurvive()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrack(e, "a.mp3", "Vaporwave");
        await e.Db.GenreJob.RunAsync();
        Assert.Empty(await GenresOf(e, track));

        // The user assigns a genre manually (provenance: User).
        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
            "INSERT INTO TrackGenres(TrackId, GenreId, Source) SELECT @track, Id, 'User' FROM Genres WHERE Name = 'Ambient'",
            new { track }, t));

        // Then adds an alias "vaporwave" -> Electronic and bumps the taxonomy version.
        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
            "INSERT INTO GenreAliases(AliasNormalized, GenreId) SELECT 'vaporwave', Id FROM Genres WHERE Name = 'Electronic'", transaction: t));
        Assert.Equal(2, await e.Db.Genres.BumpTaxonomyVersionAsync());

        Assert.Equal(1, await e.Db.GenreJob.RunAsync());
        Assert.Equal(["Electronic"], await GenresOf(e, track, "Embedded"));
        Assert.Equal(["Ambient"], await GenresOf(e, track, "User"));   // untouched by the remap
        Assert.Equal(["Ambient", "Electronic"], await GenresOf(e, track));
        Assert.Equal(2, MapVersion(e, track));
    }

    [Fact]
    public async Task ChangedRawGenreDuringMapping_LeavesTheTrackStale()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var track = await AddTrack(e, "a.mp3", "Disco");

        // Simulate a rescan that changes the tag between reading the stale row and writing the mapping.
        var stale = await e.Db.TrackGenres.GetStaleAsync(1, 10);
        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, @"C:\Music\a.mp3", "a.mp3", "Artist", "Jazz")]);
        var applied = await e.Db.TrackGenres.ReplaceEmbeddedAsync(
            stale.Select(s => new MusicFinder.Domain.Contracts.TrackGenreMapping(s.TrackId, s.RawGenre, [1])).ToList(), 1);

        Assert.Equal(0, applied);
        Assert.Equal(0, MapVersion(e, track));
        Assert.Empty(await GenresOf(e, track));
    }
}
