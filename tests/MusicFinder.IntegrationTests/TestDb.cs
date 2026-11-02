using Dapper;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Entities;
using MusicFinder.Infrastructure.Genres;
using MusicFinder.Infrastructure.Persistence;
using MusicFinder.Infrastructure.Persistence.Repositories;

namespace MusicFinder.IntegrationTests;

/// <summary>Temp-folder SQLite database with migrations applied and repositories wired up.</summary>
public sealed class TestDb : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mf-tests-" + Guid.NewGuid().ToString("N"));
    public SqliteConnectionFactory Factory { get; }
    public SqliteWriteQueue Writes { get; }
    public LibraryRootRepository Roots { get; }
    public FolderRepository Folders { get; }
    public TrackRepository Tracks { get; }
    public GenreRepository Genres { get; }
    public CustomTagRepository Tags { get; }
    public GenreTaxonomySeeder Seeder { get; }
    public DjSessionRepository Sessions { get; }
    public TrackEventRepository Events { get; }
    public PlayHistoryRepository Plays { get; }
    public TrackGenreRepository TrackGenres { get; }
    public MusicFinder.Application.Genres.GenreService GenreService { get; }
    public MusicFinder.Application.Genres.DjTagService TagService { get; }
    public MusicFinder.Application.Genres.GenreMappingJob GenreJob { get; }
    public TrackSearchAliasRepository Aliases { get; }
    public SearchAliasOverrideRepository AliasOverrides { get; }
    public MusicFinder.Application.Search.SearchAliasIndexJob AliasJob { get; }
    public IdentificationCacheRepository Identification { get; }

    public TestDb()
    {
        Directory.CreateDirectory(Dir);
        Factory = new SqliteConnectionFactory(Path.Combine(Dir, "test.db"));
        new MigrationRunner(Factory).Migrate();
        Writes = new SqliteWriteQueue(Factory);
        Roots = new LibraryRootRepository(Factory, Writes);
        Folders = new FolderRepository(Factory, Writes);
        Tracks = new TrackRepository(Factory, Writes);
        Genres = new GenreRepository(Factory, Writes);
        TrackGenres = new TrackGenreRepository(Factory, Writes);
        GenreJob = new MusicFinder.Application.Genres.GenreMappingJob(Genres, TrackGenres);
        Tags = new CustomTagRepository(Factory, Writes);
        GenreService = new MusicFinder.Application.Genres.GenreService(Genres, TrackGenres, GenreJob);
        TagService = new MusicFinder.Application.Genres.DjTagService(Tags);
        Seeder = new GenreTaxonomySeeder(Writes);
        Sessions = new DjSessionRepository(Factory, Writes);
        Events = new TrackEventRepository(Factory, Writes);
        Plays = new PlayHistoryRepository(Factory, Writes);
        Aliases = new TrackSearchAliasRepository(Factory, Writes);
        AliasOverrides = new SearchAliasOverrideRepository(Factory, Writes);
        AliasJob = new MusicFinder.Application.Search.SearchAliasIndexJob(Aliases, AliasOverrides);
        Identification = new IdentificationCacheRepository(Factory, Writes);
    }

    /// <summary>Seeds the starter genre taxonomy and DJ tags (the app does this at startup).</summary>
    public Task SeedTaxonomyAsync() => Seeder.EnsureSeededAsync();

    public static Track MakeTrack(long folderId, string path, string? title = null, string? artist = null, string? genre = null)
    {
        var name = Path.GetFileName(path);
        return new Track
        {
            FolderId = folderId,
            FullPath = path,
            FileName = name,
            Extension = Path.GetExtension(path).ToLowerInvariant(),
            FileSize = 1000,
            ModifiedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IndexedUtc = DateTime.UtcNow,
            Title = title,
            Artist = artist,
            GenreRaw = genre,
            NormalizedText = TextNormalizer.BuildSearchText(name, title, artist, genre)
        };
    }

    public List<long> Match(string ftsExpression)
    {
        using var conn = Factory.Open();
        return conn.Query<long>("SELECT rowid FROM TrackSearch WHERE TrackSearch MATCH @m ORDER BY rowid", new { m = ftsExpression }).AsList();
    }

    public async ValueTask DisposeAsync()
    {
        await Writes.DisposeAsync();
        Factory.ReleasePool(); // only this database: tests run in parallel and share the process-wide pool list
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
