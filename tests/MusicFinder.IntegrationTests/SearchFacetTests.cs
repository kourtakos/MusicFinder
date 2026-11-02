using Dapper;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>Taxonomy-aware genre facet, virtual genre buckets, DJ tags and the new result columns.</summary>
public class SearchFacetTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public SqliteSearchService Search { get; }
        public long Root { get; private set; }
        public long Disco { get; private set; }
        public long Funk { get; private set; }
        public long Greek { get; private set; }
        public long NuDisco { get; private set; }
        public long WarmUp { get; private set; }
        public long PeakTime { get; private set; }
        public Dictionary<string, long> TrackIds { get; } = [];

        public Env() => Search = new SqliteSearchService(Db.Factory);

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var db = e.Db;
            await db.SeedTaxonomyAsync();
            var root = await db.Roots.AddAsync(@"C:\Music");
            e.Root = root.Id;
            var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

            static Track T(long f, string name, string? genre, int? year, double? bpm) =>
                new()
                {
                    FolderId = f, FullPath = $@"C:\Music\{name}.mp3", FileName = $"{name}.mp3", Extension = ".mp3", FileSize = 1,
                    ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Title = name, Artist = "Artist", GenreRaw = genre,
                    Year = year, Bpm = bpm, NormalizedText = MusicFinder.Application.Services.TextNormalizer.BuildSearchText(name, genre)
                };

            await db.Tracks.UpsertBatchAsync(
            [
                T(folder, "Le Freak", "Disco;Funk", 1978, 120),
                T(folder, "Digital Love", "Nu-Disco", 2001, 123),
                T(folder, "Sti Gonia", "Λαϊκά", 1985, 100),
                T(folder, "Weird Wave", "Vaporwave", 2015, 90),
                T(folder, "No Genre", null, null, null),
            ]);
            foreach (var f in await db.Tracks.GetIndexedFilesAsync(root.Id))
                e.TrackIds[Path.GetFileNameWithoutExtension(f.FullPath)] = f.Id;

            await db.GenreJob.RunAsync();

            var genres = (await db.Genres.GetAllAsync()).ToDictionary(g => g.Name, g => g.Id);
            e.Disco = genres["Disco"]; e.Funk = genres["Funk"]; e.Greek = genres["Greek"]; e.NuDisco = genres["Nu Disco"];
            var tags = (await db.Tags.GetAllAsync()).ToDictionary(t => t.Name, t => t.Id);
            e.WarmUp = tags["Warm Up"]; e.PeakTime = tags["Peak Time"];
            return e;
        }

        public Task TagAsync(string track, long tagId) => Db.Writes.EnqueueAsync((c, t) =>
            c.Execute("INSERT INTO TrackCustomTags(TrackId, TagId) VALUES (@id, @tagId)", new { id = TrackIds[track], tagId }, t));

        public async Task<List<string>> TitlesAsync(SearchQuery q) =>
            (await Search.SearchAsync(q, default)).Items.Select(i => i.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task SelectingAParentGenre_IncludesItsChildren()
    {
        await using var e = await Env.CreateAsync();
        Assert.Equal(["Digital Love", "Le Freak"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { GenreIds = [e.Disco] } }));
        Assert.Equal(["Digital Love"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { GenreIds = [e.NuDisco] } }));
        Assert.Equal(["Sti Gonia"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { GenreIds = [e.Greek] } })); // via child Laiko
    }

    [Fact]
    public async Task GenreFacet_IsOrWithinAndAndAcrossFacets()
    {
        await using var e = await Env.CreateAsync();
        var either = new SearchFilters { GenreIds = [e.Funk, e.Greek] };
        Assert.Equal(["Le Freak", "Sti Gonia"], await e.TitlesAsync(new SearchQuery { Filters = either }));

        var andDecade = either with { Decades = [1980] };
        Assert.Equal(["Sti Gonia"], await e.TitlesAsync(new SearchQuery { Filters = andDecade }));

        var andText = new SearchQuery { Text = "freak", Filters = either };
        Assert.Equal(["Le Freak"], await e.TitlesAsync(andText));
    }

    [Fact]
    public async Task UnknownGenre_AndUncategorized_AreVirtualBuckets()
    {
        await using var e = await Env.CreateAsync();
        Assert.Equal(["No Genre"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { UnknownGenre = true } }));
        Assert.Equal(["Weird Wave"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { Uncategorized = true } }));
        Assert.Equal(["No Genre", "Weird Wave"],
            await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { UnknownGenre = true, Uncategorized = true } }));
        Assert.Equal(["Le Freak", "No Genre", "Weird Wave"],
            await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { GenreIds = [e.Funk], UnknownGenre = true, Uncategorized = true } }));
    }

    [Fact]
    public async Task UserAssignedGenre_MakesATrackCategorized()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
            "INSERT INTO TrackGenres(TrackId, GenreId, Source) VALUES (@id, @g, 'User')", new { id = e.TrackIds["Weird Wave"], g = e.Funk }, t));

        Assert.Empty(await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { Uncategorized = true } }));
        Assert.Equal(["Le Freak", "Weird Wave"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { GenreIds = [e.Funk] } }));
    }

    [Fact]
    public async Task Tags_AreOrWithinTheFacet_AndCombineWithGenres()
    {
        await using var e = await Env.CreateAsync();
        await e.TagAsync("Le Freak", e.WarmUp);
        await e.TagAsync("Sti Gonia", e.PeakTime);
        await e.TagAsync("Digital Love", e.PeakTime);

        Assert.Equal(["Le Freak"], await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { TagIds = [e.WarmUp] } }));
        Assert.Equal(["Digital Love", "Le Freak", "Sti Gonia"],
            await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { TagIds = [e.WarmUp, e.PeakTime] } }));
        Assert.Equal(["Digital Love"],
            await e.TitlesAsync(new SearchQuery { Filters = new SearchFilters { TagIds = [e.PeakTime], GenreIds = [e.Disco] } }));
    }

    [Fact]
    public async Task ResultRows_CarryMappedGenres_Tags_AndArtworkKey()
    {
        await using var e = await Env.CreateAsync();
        await e.TagAsync("Le Freak", e.PeakTime);
        await e.TagAsync("Le Freak", e.WarmUp);
        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
            "UPDATE Tracks SET ArtworkCacheKey = 'abc123' WHERE Id = @id", new { id = e.TrackIds["Le Freak"] }, t));

        var rows = (await e.Search.SearchAsync(new SearchQuery(), default)).Items.ToDictionary(r => r.Title);

        var freak = rows["Le Freak"];
        Assert.Equal(["Disco", "Funk"], freak.Genres);                   // mapped, in taxonomy order
        Assert.True(freak.HasMappedGenres);
        Assert.Equal(["Disco", "Funk"], freak.RawGenres);
        Assert.Equal(["Warm Up", "Peak Time"], freak.DjTags);            // tag sort order, not insertion order
        Assert.Equal("abc123", freak.ArtworkCacheKey);

        Assert.Equal(["Nu Disco"], rows["Digital Love"].Genres);         // "Nu-Disco" shown as the taxonomy name
        Assert.Equal(["Nu-Disco"], rows["Digital Love"].RawGenres);      // the embedded text is still available

        var weird = rows["Weird Wave"];
        Assert.False(weird.HasMappedGenres);
        Assert.Equal(["Vaporwave"], weird.Genres);                       // unmapped genres stay visible as raw text
        Assert.Empty(weird.DjTags);
        Assert.Null(weird.ArtworkCacheKey);
        Assert.Empty(rows["No Genre"].Genres);
    }

    [Fact]
    public async Task GetRows_ReturnsFreshRowsForTheRequestedTracksOnly()
    {
        await using var e = await Env.CreateAsync();
        await e.TagAsync("Le Freak", e.WarmUp);

        var ids = new[] { e.TrackIds["Le Freak"], e.TrackIds["No Genre"] };
        var rows = await e.Search.GetRowsAsync(ids, null, default);

        Assert.Equal(["Le Freak", "No Genre"], rows.Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(["Warm Up"], rows.Single(r => r.Title == "Le Freak").DjTags);
        Assert.Empty(await e.Search.GetRowsAsync([], null, default));
        Assert.Empty(await e.Search.GetRowsAsync([987654], null, default));
    }

    [Theory]
    [InlineData(SortDirection.Asc)]
    [InlineData(SortDirection.Desc)]
    public async Task KeysetPaging_StaysCompleteWithFolderAndGenreCtesCombined(SortDirection dir)
    {
        await using var e = await Env.CreateAsync();
        var folder = (await e.Db.Folders.GetChildrenAsync(e.Root, null)).Single().Id;
        var disco = (await e.Db.Genres.GetAllAsync()).Single(g => g.Name == "Disco").Id;

        // 40 more Disco-family tracks so paging has several pages.
        await e.Db.Tracks.UpsertBatchAsync(Enumerable.Range(0, 40).Select(i =>
        {
            var t = TestDb.MakeTrack(folder, $@"C:\Music\bulk{i:00}.mp3", $"Bulk {i % 7}", "A", i % 2 == 0 ? "Disco" : "Nu Disco");
            t.Bpm = 100 + i % 5;
            return t;
        }).ToList());
        await e.Db.GenreJob.RunAsync();

        var query = new SearchQuery
        {
            FolderId = folder, IncludeSubfolders = true, Sort = SortField.Bpm, Direction = dir, PageSize = 7,
            Filters = new SearchFilters { GenreIds = [disco] }
        };
        var all = (await e.Search.SearchAsync(query with { PageSize = 1000 }, default)).Items.Select(i => i.Id).ToList();
        Assert.Equal(42, all.Count); // 40 bulk + Le Freak + Digital Love

        var seen = new List<long>();
        SearchCursor? cursor = null;
        do
        {
            var page = await e.Search.SearchAsync(query with { After = cursor }, default);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(all, seen);
        Assert.Equal(42, await e.Search.CountAsync(query, default));
    }
}
