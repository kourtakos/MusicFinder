using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Health;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Health;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

public class HealthServiceTests
{
    private static MetadataHealthService HealthOf(TestDb db) => new(db.Factory, db.Roots);

    private static async Task<(TestDb Db, long Folder)> CreateAsync()
    {
        var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        return (db, folder);
    }

    private static Track Make(long folder, string name, Action<Track>? edit = null)
    {
        var t = TestDb.MakeTrack(folder, $@"C:\Music\{name}.mp3", name, "Artist", "Disco");
        t.Album = "Album"; t.Year = 2000; t.Bpm = 120; t.MusicalKey = "8A"; t.ArtworkCacheKey = "abc";
        edit?.Invoke(t);
        return t;
    }

    [Fact]
    public async Task AnEmptyLibrary_HasNoFindings()
    {
        await using var db = new TestDb();

        var s = await HealthOf(db).GetSummaryAsync(default);

        Assert.Equal(0, s.TotalTracks);
        Assert.All(Enum.GetValues<HealthIssueKind>(), k => Assert.Equal(0, s.CountOf(k)));
    }

    [Fact]
    public async Task EmptyAndWhitespaceTags_BothCountAsMissing_AndRealValuesDoNot()
    {
        var (db, folder) = await CreateAsync();
        await using var _ = db;
        await db.Tracks.UpsertBatchAsync(
        [
            Make(folder, "complete"),
            Make(folder, "nulls", t => { t.Artist = null; t.Album = null; t.GenreRaw = null; t.Year = null; t.Bpm = null; t.MusicalKey = null; t.ArtworkCacheKey = null; }),
            Make(folder, "blanks", t => { t.Artist = ""; t.Album = "   "; t.GenreRaw = "  "; t.Bpm = 0; t.MusicalKey = " "; t.ArtworkCacheKey = ""; })
        ]);

        var s = await HealthOf(db).GetSummaryAsync(default);

        Assert.Equal(3, s.TotalTracks);
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingArtist));
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingAlbum));
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingGenre));
        Assert.Equal(1, s.CountOf(HealthIssueKind.MissingYear));
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingBpm));
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingKey));
        Assert.Equal(2, s.CountOf(HealthIssueKind.MissingArtwork));
        Assert.Equal(0, s.CountOf(HealthIssueKind.MissingTitle));
    }

    [Fact]
    public async Task FilesThatAreGone_AreCountedApart_AndLeaveTheOtherCountsAlone()
    {
        var (db, folder) = await CreateAsync();
        await using var _ = db;
        await db.Tracks.UpsertBatchAsync(
        [
            Make(folder, "here"),
            Make(folder, "gone", t => { t.Missing = true; t.Artist = null; t.IssueFlags = (int)IssueFlags.LowBitrate; })
        ]);

        var s = await HealthOf(db).GetSummaryAsync(default);

        Assert.Equal(1, s.TotalTracks);
        Assert.Equal(1, s.CountOf(HealthIssueKind.MissingFiles));
        Assert.Equal(0, s.CountOf(HealthIssueKind.MissingArtist));
        Assert.Equal(0, s.CountOf(HealthIssueKind.LowBitrate));
    }

    [Fact]
    public async Task RowsOfAnOlderReader_AreCountedAsNotCheckedYet()
    {
        var (db, folder) = await CreateAsync();
        await using var _ = db;
        await db.Tracks.UpsertBatchAsync([Make(folder, "old", t => t.MetaVersion = 1), Make(folder, "new")]);

        Assert.Equal(1, (await HealthOf(db).GetSummaryAsync(default)).CountOf(HealthIssueKind.NotChecked));
    }

    [Fact]
    public async Task QueryIssues_ReturnsThoseTracks_InIdOrder_AndPages()
    {
        var (db, folder) = await CreateAsync();
        await using var _ = db;
        await db.Tracks.UpsertBatchAsync(Enumerable.Range(0, 5).Select(i => Make(folder, "t" + i, t => t.IssueFlags = i % 2 == 0 ? (int)IssueFlags.Mojibake : 0)).ToList());
        var h = HealthOf(db);

        var all = await h.QueryIssuesAsync(HealthIssueKind.Mojibake, 0, 100, default);
        var page = await h.QueryIssuesAsync(HealthIssueKind.Mojibake, 1, 1, default);

        Assert.Equal(3, all.Count);
        Assert.Equal(all.OrderBy(x => x), all);
        Assert.Equal([all[1]], page);
    }

    [Fact]
    public async Task EverySearchFilter_AgreesWithItsHealthCount()
    {
        var (db, folder) = await CreateAsync();
        await using var _ = db;
        await db.Tracks.UpsertBatchAsync(
        [
            Make(folder, "a"),
            Make(folder, "b", t => { t.Title = ""; t.Artist = null; t.Album = " "; t.GenreRaw = null; t.Year = null; t.Bpm = null; t.MusicalKey = null; t.ArtworkCacheKey = null; }),
            Make(folder, "c", t => t.IssueFlags = (int)(IssueFlags.Mojibake | IssueFlags.LowBitrate | IssueFlags.OddSampleRate)),
            Make(folder, "d", t => t.IssueFlags = (int)(IssueFlags.Unreadable | IssueFlags.CodecMismatch | IssueFlags.SizeMismatch | IssueFlags.ZeroDuration)),
            Make(folder, "e", t => t.Missing = true)
        ]);
        var summary = await HealthOf(db).GetSummaryAsync(default);
        var search = new SqliteSearchService(db.Factory);

        foreach (var kind in Enum.GetValues<HealthIssueKind>())
        {
            var query = HealthIssues.SearchFor(kind);
            if (query is null) continue;
            var found = await search.CountAsync(new SearchQuery { Text = query }, default);
            Assert.True(summary.CountOf(kind) == found, $"{kind}: card says {summary.CountOf(kind)}, '{query}' finds {found}");
        }
    }

    [Fact]
    public async Task EveryFinding_HasATitle_AndOnlyTheFileListsHaveNoSearch()
    {
        foreach (var kind in Enum.GetValues<HealthIssueKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(HealthIssues.Title(kind)));
            Assert.Equal(kind is HealthIssueKind.MissingFiles or HealthIssueKind.NotChecked, HealthIssues.SearchFor(kind) is null);
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheCounts_FollowATagEditWithoutARescan()
    {
        await using var db = new TestDb();
        var music = Path.Combine(db.Dir, "Music");
        Directory.CreateDirectory(music);
        var path = Fixtures.MakeMp3(Path.Combine(music, "a.mp3"));
        await db.SeedTaxonomyAsync();
        var root = await db.Roots.AddAsync(music);
        await new LibraryScanner(db.Roots, db.Folders, db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
            .ScanRootAsync(root.Id, null, default);
        var id = (await db.Tracks.GetIndexedFilesAsync(root.Id)).Single().Id;
        var health = HealthOf(db);
        Assert.Equal(1, (await health.GetSummaryAsync(default)).CountOf(HealthIssueKind.MissingArtist));

        var service = new TagEditingService(db.Tracks, new TagLibMetadataWriter(), db.GenreJob);
        var summary = await service.ApplyAsync([id],
            new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Artist] = "Χάρις Αλεξίου", [TagField.Title] = "Σ' αγαπώ" }),
            new TagWriteOptions(), null, default);

        Assert.Equal(1, summary.Written);
        var after = await health.GetSummaryAsync(default);
        Assert.Equal(0, after.CountOf(HealthIssueKind.MissingArtist));
        Assert.Equal(0, after.CountOf(HealthIssueKind.MissingTitle));
        Assert.Equal(0, after.CountOf(HealthIssueKind.Mojibake)); // real Greek written and read back is not garbled
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AFileThatIsGarbledOnDisk_IsFoundAsMojibake_AfterAScan()
    {
        await using var db = new TestDb();
        var music = Path.Combine(db.Dir, "Music");
        Directory.CreateDirectory(music);
        var path = Fixtures.MakeMp3(Path.Combine(music, "a.mp3"));
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("Σ' αγαπώ"));
            f.Tag.Performers = ["Χάρις Αλεξίου"];
            f.Save();
        }
        var root = await db.Roots.AddAsync(music);
        await new LibraryScanner(db.Roots, db.Folders, db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
            .ScanRootAsync(root.Id, null, default);

        Assert.Equal(1, (await HealthOf(db).GetSummaryAsync(default)).CountOf(HealthIssueKind.Mojibake));
    }

    [Fact]
    public async Task UnsupportedFiles_AreReportedByExtension()
    {
        await using var db = new TestDb();
        var music = Path.Combine(db.Dir, "Music");
        Directory.CreateDirectory(music);
        File.WriteAllText(Path.Combine(music, "x.wma"), "x");
        var root = await db.Roots.AddAsync(music);
        await new LibraryScanner(db.Roots, db.Folders, db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
            .ScanRootAsync(root.Id, null, default);

        var s = await HealthOf(db).GetSummaryAsync(default);

        Assert.Equal([(".wma", 1)], s.Unsupported);
        Assert.Equal(1, s.UnsupportedTotal);
    }
}
