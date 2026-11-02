using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

public class SearchServiceTests
{
    private sealed record Fixture(TestDb Db, SqliteSearchService Search, long Root, long Top, long Disco, long NuDisco);

    private static async Task<Fixture> CreateAsync()
    {
        var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var top = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        var disco = await db.Folders.UpsertAsync(root.Id, top, @"C:\Music\Disco", "Disco");
        var nu = await db.Folders.UpsertAsync(root.Id, disco, @"C:\Music\Disco\Nu Disco", "Nu Disco");

        static Track T(long f, string path, string? title, string? artist, string? genre, double? bpm, int? year, string? key = null)
        {
            var t = TestDb.MakeTrack(f, path, title, artist, genre);
            t.Bpm = bpm; t.Year = year; t.MusicalKey = key;
            return t;
        }

        await db.Tracks.UpsertBatchAsync(
        [
            T(disco, @"C:\Music\Disco\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco;Funk", 120, 1978, "8A"),
            T(disco, @"C:\Music\Disco\Chic - Good Times.mp3", "Good Times", "Chic", "Disco", 119, 1979, "8A"),
            T(nu, @"C:\Music\Disco\Nu Disco\Daft - Digital Love.flac", "Digital Love", "Daft Punk", "Nu Disco", null, 2001),
            T(top, @"C:\Music\Χάρις Αλεξίου - Σ' αγαπώ.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Greek", 100, 1985),
            T(top, @"C:\Music\untitled.mp3", null, null, null, null, null),
        ]);
        return new Fixture(db, new SqliteSearchService(db.Factory), root.Id, top, disco, nu);
    }

    private static async Task<List<string>> Titles(Fixture f, SearchQuery q) =>
        (await f.Search.SearchAsync(q, default)).Items.Select(i => i.Title).ToList();

    [Fact]
    public async Task TokenSearch_AndPrefix()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { Text = "chic" }));
        Assert.Equal(["Le Freak"], await Titles(f, new SearchQuery { Text = "chic fre" }));
        Assert.Equal(["Digital Love"], await Titles(f, new SearchQuery { Text = "nu disco" }));
        Assert.Empty(await Titles(f, new SearchQuery { Text = "nonexistent" }));
    }

    [Fact]
    public async Task EmptyQuery_ListsAll_AndHostileInputDoesNotThrow()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(5, (await f.Search.SearchAsync(new SearchQuery(), default)).Items.Count);
        foreach (var hostile in new[] { "\"", "AND", "NEAR(", "a:b:c", "*", "'; DROP TABLE Tracks;--", "(((", "title:\"" })
            await f.Search.SearchAsync(new SearchQuery { Text = hostile }, default);
        Assert.Equal(5, await f.Search.CountAsync(new SearchQuery(), default));
    }

    [Fact]
    public async Task FieldFilters()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(["Digital Love"], await Titles(f, new SearchQuery { Text = "ext:flac" }));
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { Text = "key:8a" }));
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { Text = "bpm:115-125" }));
        Assert.Equal(["Digital Love"], await Titles(f, new SearchQuery { Text = "decade:00s" }));
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { Text = "year:1978-1979" }));
        Assert.Equal(["Digital Love", "untitled"], await Titles(f, new SearchQuery { Text = "missing:bpm" }));
        Assert.Equal(["Le Freak"], await Titles(f, new SearchQuery { Text = "artist:chic genre:funk" }));
        Assert.Empty(await Titles(f, new SearchQuery { Text = "title:chic" })); // scoped: chic is an artist
    }

    [Fact]
    public async Task InvalidSyntax_ReturnsWarningsNotErrors()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        var page = await f.Search.SearchAsync(new SearchQuery { Text = "bpm:abc chic" }, default);
        Assert.Single(page.Warnings);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GreekSearch_IsAccentAndCaseInsensitive()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(["Σ' αγαπώ"], await Titles(f, new SearchQuery { Text = "αλεξιου" }));
        Assert.Equal(["Σ' αγαπώ"], await Titles(f, new SearchQuery { Text = "ΧΑΡΙΣ" }));
        Assert.Equal(["Σ' αγαπώ"], await Titles(f, new SearchQuery { Text = "Αλεξίου" }));
        Assert.Equal(["Σ' αγαπώ"], await Titles(f, new SearchQuery { Text = "\"χαρις αλεξιου\"" }));
    }

    [Fact]
    public async Task FolderScope_DirectRecursiveAndGlobal()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(["Digital Love", "Good Times", "Le Freak"],
            await Titles(f, new SearchQuery { FolderId = f.Disco, IncludeSubfolders = true }));
        Assert.Equal(["Good Times", "Le Freak"],
            await Titles(f, new SearchQuery { FolderId = f.Disco, IncludeSubfolders = false }));
        Assert.Equal(["Digital Love"], await Titles(f, new SearchQuery { FolderId = f.NuDisco }));
        Assert.Equal(5, (await f.Search.SearchAsync(new SearchQuery { FolderId = f.NuDisco, GlobalScope = true }, default)).Items.Count);
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { FolderId = f.Disco, Text = "chic" }));
        Assert.Empty(await Titles(f, new SearchQuery { FolderId = f.NuDisco, Text = "chic" }));
    }

    [Fact]
    public async Task Facets_OrWithinAndAcross()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        // OR within the decade facet...
        var decades = new SearchFilters { Decades = [1970, 1980] };
        Assert.Equal(["Good Times", "Le Freak", "Σ' αγαπώ"], await Titles(f, new SearchQuery { Filters = decades }));
        // ...AND across facets (decade AND BPM range) ...
        var both = decades with { BpmMin = 110, BpmMax = 130 };
        Assert.Equal(["Good Times", "Le Freak"], await Titles(f, new SearchQuery { Filters = both }));
        // ...AND with the text query.
        Assert.Equal(["Le Freak"], await Titles(f, new SearchQuery { Text = "freak", Filters = both }));
        Assert.Equal(["Good Times", "Le Freak"],
            await Titles(f, new SearchQuery { Filters = new SearchFilters { BpmMin = 110, BpmMax = 130 } }));
        // Genre facets are covered by SearchFacetTests (taxonomy-aware).
    }

    [Fact]
    public async Task MissingTracks_AreExcludedUnlessRequested()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        await f.Db.Tracks.MarkMissingAsync(f.Root, DateTime.UtcNow.AddHours(1));
        Assert.Empty(await Titles(f, new SearchQuery()));
        Assert.Equal(5, (await f.Search.SearchAsync(new SearchQuery { Filters = new SearchFilters { IncludeMissing = true } }, default)).Items.Count);
    }

    [Theory]
    [InlineData(SortField.Title, SortDirection.Asc)]
    [InlineData(SortField.Title, SortDirection.Desc)]
    [InlineData(SortField.Artist, SortDirection.Asc)]
    [InlineData(SortField.Artist, SortDirection.Desc)]
    [InlineData(SortField.Bpm, SortDirection.Asc)]
    [InlineData(SortField.Bpm, SortDirection.Desc)]
    [InlineData(SortField.Duration, SortDirection.Asc)]
    [InlineData(SortField.Key, SortDirection.Desc)]
    [InlineData(SortField.FileDate, SortDirection.Asc)]
    public async Task KeysetPages_AreDisjointAndComplete_UnderTiesAndNulls(SortField sort, SortDirection dir)
    {
        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");

        // Many ties (BPM/artist repeat) and many NULLs.
        var tracks = Enumerable.Range(0, 97).Select(i =>
        {
            var t = TestDb.MakeTrack(folder, $@"C:\Music\track{i:000}.mp3", i % 7 == 0 ? null : $"Title {i % 10}", i % 3 == 0 ? null : $"Artist {i % 4}");
            t.Bpm = i % 5 == 0 ? null : 100 + i % 6;
            t.DurationMs = i % 4 == 0 ? null : 180000 + (i % 3) * 1000;
            t.MusicalKey = i % 2 == 0 ? null : $"{1 + i % 3}A";
            return t;
        }).ToList();
        await db.Tracks.UpsertBatchAsync(tracks);

        var search = new SqliteSearchService(db.Factory);
        var seen = new List<long>();
        SearchCursor? cursor = null;
        var pages = 0;
        do
        {
            var page = await search.SearchAsync(new SearchQuery { Sort = sort, Direction = dir, PageSize = 10, After = cursor }, default);
            Assert.True(page.Items.Count <= 10);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
            Assert.Equal(page.HasMore, cursor is not null);
            pages++;
        } while (cursor is not null && pages < 50);

        Assert.Equal(10, pages);
        Assert.Equal(97, seen.Count);
        Assert.Equal(97, seen.Distinct().Count());

        // Paging order must equal the order of one big page.
        var all = await search.SearchAsync(new SearchQuery { Sort = sort, Direction = dir, PageSize = 1000 }, default);
        Assert.Equal(all.Items.Select(i => i.Id), seen);
    }

    [Fact]
    public async Task Count_MatchesSearch()
    {
        var f = await CreateAsync();
        await using var _ = f.Db;
        Assert.Equal(2, await f.Search.CountAsync(new SearchQuery { Text = "chic" }, default));
        Assert.Equal(3, await f.Search.CountAsync(new SearchQuery { FolderId = f.Disco }, default));
    }
}
