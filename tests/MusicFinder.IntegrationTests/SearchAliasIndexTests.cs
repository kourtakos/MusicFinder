using Dapper;
using MusicFinder.Application.Search;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;
using MusicFinder.Infrastructure.Persistence.Repositories;

namespace MusicFinder.IntegrationTests;

/// <summary>Migration 0005 and the job that fills the smart-search alias index: triggers, staleness, upgrade and guards.</summary>
public class SearchAliasIndexTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public long FolderId { get; private set; }
        public Dictionary<string, long> Ids { get; } = [];

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            e.FolderId = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(e.FolderId, @"C:\Music\Λαϊκά\Χάρις Αλεξίου - Σ' αγαπώ.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Λαϊκά"),
                TestDb.MakeTrack(e.FolderId, @"C:\Music\Pop\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco"),
            ]);
            foreach (var f in await e.Db.Tracks.GetIndexedFilesAsync(root.Id))
                e.Ids[Path.GetFileNameWithoutExtension(f.FullPath)] = f.Id;
            return e;
        }

        public long Greek => Ids["Χάρις Αλεξίου - Σ' αγαπώ"];
        public long Chic => Ids["Chic - Le Freak"];

        public List<long> Alias(string match)
        {
            using var conn = Db.Factory.Open();
            return conn.Query<long>("SELECT rowid FROM TrackAliasSearch WHERE TrackAliasSearch MATCH @match ORDER BY rowid", new { match }).AsList();
        }

        public int Version(long id)
        {
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<int>("SELECT SearchAliasVersion FROM Tracks WHERE Id = @id", new { id });
        }

        public int AliasRows()
        {
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM TrackSearchAliases");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // ===== the text builder =====

    [Fact]
    public void TheBuilder_NormalizesEachField_AndKeysItPhonetically()
    {
        var row = new SearchAliasStaleRow(7, @"C:\Music\Λαϊκά\Χάρις Αλεξίου - Σ' αγαπώ.mp3", "Χάρις Αλεξίου - Σ' αγαπώ.mp3",
            "Σ' αγαπώ", "Χάρις Αλεξίου", null, "Best of", "Λαϊκά");

        var a = SearchTextBuilder.Build(row, []);

        Assert.Equal(("σ αγαπω", "χαρισ αλεξιου", "best of", "λαικα"), (a.TitleN, a.ArtistN, a.AlbumN, a.GenreN));
        // Λαϊκά keys to "leka", and so does the Latin spelling "laika", which is what lets the two find each other.
        Assert.Equal(("s agapo", "xaris alexiu", "best of", "leka"), (a.TitleK, a.ArtistK, a.AlbumK, a.GenreK));
        Assert.Contains("λαικα", a.FileN);                       // the folder name is searchable
        Assert.Contains("χαρισ αλεξιου σ αγαπω", a.FileN);      // so is the file name without its extension
        Assert.DoesNotContain("mp3", a.FileN);
        Assert.False(a.FileN.Split(' ').Contains("c"));          // the drive letter is not
        Assert.Equal("", a.Extra);
    }

    [Fact]
    public void TheBuilder_HandlesMissingTags()
    {
        var a = SearchTextBuilder.Build(new SearchAliasStaleRow(1, @"\\nas\music\song.flac", "song.flac", null, null, null, null, null), []);
        Assert.Equal(("", "", "", ""), (a.TitleN, a.ArtistN, a.AlbumN, a.GenreN));
        Assert.Equal("song nas music", a.FileN);
    }

    [Fact]
    public void AnOverride_AppliesOnlyToTracksThatContainItsSourceAsWholeWords()
    {
        var overrides = new[] { new SearchAliasOverride(1, "Daft Punk", "νταφτ πανκ", "daft punk", "νταφτ πανκ") };
        var daft = new SearchAliasStaleRow(1, @"C:\a.mp3", "a.mp3", "One More Time", "Daft Punk", null, null, null);
        var other = new SearchAliasStaleRow(2, @"C:\b.mp3", "b.mp3", "Song", "Daft Punkers", null, null, null);

        Assert.Contains("νταφτ πανκ", SearchTextBuilder.Build(daft, overrides).Extra);
        Assert.Contains(GreekPhoneticKey.KeyText("νταφτ πανκ"), SearchTextBuilder.Build(daft, overrides).Extra);
        Assert.Equal("", SearchTextBuilder.Build(other, overrides).Extra);
    }

    // ===== schema and triggers =====

    [Fact]
    public async Task Migration_AddsTheAliasTables_AndEveryExistingTrackIsStale()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(0, e.Version(e.Greek));
        Assert.Equal(0, e.AliasRows());
        Assert.Equal(2, await e.Db.Aliases.CountStaleAsync(SearchTextBuilder.CurrentVersion));
    }

    [Fact]
    public async Task TheJob_FillsTheIndex_SoGreekAndGreeklishFindTheTrack()
    {
        await using var e = await Env.CreateAsync();

        var indexed = await e.Db.AliasJob.RunAsync();

        Assert.Equal(2, indexed);
        Assert.Equal(SearchTextBuilder.CurrentVersion, e.Version(e.Greek));
        Assert.Equal([e.Greek], e.Alias("ArtistN:\"χαρισ\""));          // normalized Greek
        Assert.Equal([e.Greek], e.Alias("ArtistK:\"xaris\""));          // the phonetic key a Latin query reduces to
        Assert.Equal([e.Greek], e.Alias("ArtistK:\"alexiu\""));
        Assert.Equal([e.Greek], e.Alias("FileN:\"λαικα\""));            // folder name
        Assert.Equal([e.Chic], e.Alias("TitleN:\"freak\" OR TitleN:\"le\""));
        Assert.Empty(e.Alias("ArtistK:\"zzz\""));
        Assert.Equal(0, await e.Db.Aliases.CountStaleAsync(SearchTextBuilder.CurrentVersion));
    }

    [Fact]
    public async Task APrefixQuery_UsesTheIndexedWords()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        Assert.Equal([e.Greek], e.Alias("ArtistK:\"alex\"*"));
        Assert.Equal([e.Greek], e.Alias("ArtistN:\"αλε\"*"));
        Assert.Equal([e.Greek, e.Chic], e.Alias("ArtistK:\"x\"*"));        // "chic" is keyed xik, so a bare x prefix reaches it too
    }

    [Fact]
    public async Task RunningTheJobAgain_DoesNothing_AndKeepsTheIndexConsistent()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        Assert.Equal(0, await e.Db.AliasJob.RunAsync());
        Assert.Equal(2, e.AliasRows());
        Assert.Single(e.Alias("ArtistK:\"xaris\""));
    }

    [Fact]
    public async Task EditingATitle_MakesTheTrackStale_AndTheJobReplacesTheOldWords()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        using (var conn = e.Db.Factory.Open())
            conn.Execute("UPDATE Tracks SET Title = 'Another Name' WHERE Id = @id", new { id = e.Chic });
        Assert.Equal(0, e.Version(e.Chic));                          // the trigger noticed, whoever wrote the row

        await e.Db.AliasJob.RunAsync();

        Assert.Empty(e.Alias("TitleN:\"freak\""));                   // the old word is gone from the index
        Assert.Equal([e.Chic], e.Alias("TitleN:\"another\""));
        Assert.Equal(SearchTextBuilder.CurrentVersion, e.Version(e.Chic));
    }

    [Fact]
    public async Task ChangingAnUnrelatedColumn_DoesNotMakeTheTrackStale()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        using (var conn = e.Db.Factory.Open())
            conn.Execute("UPDATE Tracks SET Bpm = 120 WHERE Id = @id", new { id = e.Chic });

        Assert.Equal(SearchTextBuilder.CurrentVersion, e.Version(e.Chic));
    }

    [Fact]
    public async Task ARescanUpsert_MakesAChangedTrackStale()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        await e.Db.Tracks.UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, @"C:\Music\Pop\Chic - Le Freak.mp3", "Le Freak (Remix)", "Chic", "Disco")]);

        Assert.Equal(0, e.Version(e.Chic));
        await e.Db.AliasJob.RunAsync();
        Assert.Equal([e.Chic], e.Alias("TitleN:\"remix\""));
    }

    [Fact]
    public async Task DeletingATrack_RemovesItsAliasRowAndIndexEntries()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasJob.RunAsync();

        using (var conn = e.Db.Factory.Open())
        {
            conn.Execute("PRAGMA foreign_keys = ON");
            conn.Execute("DELETE FROM Tracks WHERE Id = @id", new { id = e.Greek });
        }

        Assert.Equal(1, e.AliasRows());
        Assert.Empty(e.Alias("ArtistK:\"xaris\""));
        // The FTS index must still be healthy after the delete (a stale 'delete' would corrupt it).
        using var check = e.Db.Factory.Open();
        check.Execute("INSERT INTO TrackAliasSearch(TrackAliasSearch, rank) VALUES ('integrity-check', 1)");
    }

    [Fact]
    public async Task ARowWhoseTextChangedWhileItWasBeingPrepared_IsSkipped_AndStaysStale()
    {
        await using var e = await Env.CreateAsync();
        var stale = (await e.Db.Aliases.GetStaleAsync(SearchTextBuilder.CurrentVersion, 10)).Single(s => s.Id == e.Chic);
        var alias = SearchTextBuilder.Build(stale, []);

        using (var conn = e.Db.Factory.Open())
            conn.Execute("UPDATE Tracks SET Artist = 'Somebody Else' WHERE Id = @id", new { id = e.Chic }); // the edit lands after the read

        var applied = await e.Db.Aliases.UpsertAsync([(stale, alias)], SearchTextBuilder.CurrentVersion);

        Assert.Equal(0, applied);
        Assert.Equal(0, e.Version(e.Chic));
        Assert.Equal(0, e.AliasRows());
    }

    [Fact]
    public async Task ALargeLibrary_IsIndexedInBatches()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.Tracks.UpsertBatchAsync(Enumerable.Range(0, 1300)
            .Select(i => TestDb.MakeTrack(e.FolderId, $@"C:\Music\Bulk\Track {i}.mp3", $"Τίτλος {i}", $"Artist {i % 40}", "Pop")).ToList());

        var indexed = await e.Db.AliasJob.RunAsync();

        Assert.Equal(1302, indexed);                                 // three batches of at most 500
        Assert.Equal(1302, e.AliasRows());
        Assert.Equal(0, await e.Db.Aliases.CountStaleAsync(SearchTextBuilder.CurrentVersion));
    }

    [Fact]
    public async Task OnlyOneRunExecutesAtATime_AndBothFinish()
    {
        await using var e = await Env.CreateAsync();

        var runs = await Task.WhenAll(e.Db.AliasJob.RunAsync(), e.Db.AliasJob.RunAsync(), e.Db.AliasJob.RunAsync());

        Assert.Equal(2, runs.Sum());                                 // nothing indexed twice, nothing missed
        Assert.Equal(2, e.AliasRows());
    }

    [Fact]
    public async Task TheJob_ReportsProgress_AndSaysWhenItIndexedSomething()
    {
        await using var e = await Env.CreateAsync();
        var progress = new List<(int Done, int Remaining)>();
        var indexedEvents = 0;
        e.Db.AliasJob.Progress += (_, p) => progress.Add(p);
        e.Db.AliasJob.Indexed += (_, _) => indexedEvents++;

        await e.Db.AliasJob.RunAsync();
        await e.Db.AliasJob.RunAsync();

        Assert.Equal([(2, 0)], progress);
        Assert.Equal(1, indexedEvents);                              // the empty second run raises nothing
    }

    // ===== user aliases =====

    [Fact]
    public async Task AnOverride_IsIndexedForMatchingTracksOnly()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasOverrides.AddAsync("Chic", "σικ", "chic", "σικ");

        await e.Db.AliasJob.RunAsync();

        Assert.Equal([e.Chic], e.Alias("Extra:\"σικ\""));
        Assert.Equal([e.Chic], e.Alias("Extra:\"sik\""));            // and its key form, so a Greeklish query finds it too
    }

    [Fact]
    public async Task OverridesAreUniquePerPair_AndRemovable()
    {
        await using var e = await Env.CreateAsync();
        var first = await e.Db.AliasOverrides.AddAsync("Chic", "σικ", "chic", "σικ");
        var duplicate = await e.Db.AliasOverrides.AddAsync("CHIC", "σικ", "chic", "σικ");

        Assert.NotNull(first);
        Assert.Null(duplicate);
        Assert.Single(await e.Db.AliasOverrides.GetAllAsync());

        var removed = await e.Db.AliasOverrides.RemoveAsync(first!.Id);
        Assert.Equal("Chic", removed!.Source);
        Assert.Empty(await e.Db.AliasOverrides.GetAllAsync());
        Assert.Null(await e.Db.AliasOverrides.RemoveAsync(first.Id));
    }

    [Fact]
    public async Task FindingTracksAffectedByAnAlias_UsesTheNormalizedWords()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.AliasOverrides.AddAsync("Chic", "σικ", "chic", "σικ");
        await e.Db.AliasJob.RunAsync();

        Assert.Equal([e.Chic], await e.Db.Aliases.FindContainingAsync("chic"));
        Assert.Equal([e.Chic], await e.Db.Aliases.FindWithExtraAsync("σικ"));
        Assert.Empty(await e.Db.Aliases.FindContainingAsync("nothing here"));
        Assert.Empty(await e.Db.Aliases.FindContainingAsync(""));

        await e.Db.Aliases.MarkStaleAsync([e.Chic]);
        Assert.Equal(0, e.Version(e.Chic));
        Assert.Equal(SearchTextBuilder.CurrentVersion, e.Version(e.Greek));
    }

    // ===== upgrade from the previous schema =====

    [Fact]
    public async Task UpgradingAnOlderDatabase_MarksEveryTrackStale_AndTheJobPopulatesThem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-upg5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        SqliteConnectionFactory? factory = null;
        try
        {
            factory = new SqliteConnectionFactory(Path.Combine(dir, "old.db"));
            new MigrationRunner(factory).Migrate(maxVersion: 4);       // the schema before M7
            using (var conn = factory.Open())
            {
                conn.Execute("INSERT INTO LibraryRoots(Path) VALUES ('C:\\M')");
                conn.Execute("INSERT INTO Folders(RootId, FullPath, Name) VALUES (1, 'C:\\M', 'M')");
                conn.Execute(@"INSERT INTO Tracks(FolderId, FullPath, FileName, Extension, FileSize, ModifiedUtc, IndexedUtc, Title, Artist, NormalizedText)
                               VALUES (1, 'C:\M\a.mp3', 'a.mp3', '.mp3', 1, '2025-01-01 00:00:00', '2025-01-01 00:00:00', 'Καλημέρα', 'Μαζωνάκης', 'καλημερα μαζωνακησ')");
            }

            Assert.Equal(1, new MigrationRunner(factory).Migrate(maxVersion: 5));   // applies 0005 only (0006 is an index and is tested on its own)

            var writes = new SqliteWriteQueue(factory);
            var aliases = new TrackSearchAliasRepository(factory, writes);
            var overrides = new SearchAliasOverrideRepository(factory, writes);
            Assert.Equal(1, await aliases.CountStaleAsync(SearchTextBuilder.CurrentVersion));

            Assert.Equal(1, await new SearchAliasIndexJob(aliases, overrides).RunAsync());

            using var check = factory.Open();
            Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM TrackAliasSearch WHERE TrackAliasSearch MATCH 'ArtistK:\"mazonakis\" AND TitleK:\"kalimera\"'"));
            await writes.DisposeAsync();
        }
        finally
        {
            factory?.ReleasePool();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
