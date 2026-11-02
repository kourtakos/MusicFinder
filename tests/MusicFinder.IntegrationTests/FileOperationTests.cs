using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Organize;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.FileOperations;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Persistence.Repositories;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// Batch rename on real generated files in temp folders. Covers the journal order, collisions, crash recovery, undo and the
/// guarantee that the bytes of every file survive a rename untouched.
/// </summary>
public class FileOperationTests
{
    /// <summary>The real mover, with hooks to look at the journal just before a move and to "crash" around it.</summary>
    private sealed class HookedMover(SystemFileMover real) : IFileMover
    {
        public Action<string, string>? BeforeMove { get; set; }
        public Action<string, string>? AfterMove { get; set; }
        public bool Exists(string path) => real.Exists(path);
        public bool ExistsExact(string path) => real.ExistsExact(path);
        public void EnsureFolder(string directory) => real.EnsureFolder(directory);
        public void Move(string from, string to)
        {
            BeforeMove?.Invoke(from, to);
            real.Move(from, to);
            AfterMove?.Invoke(from, to);
        }
    }

    private sealed class Crash : Exception;

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public HookedMover Mover { get; } = new(new SystemFileMover());
        public FileOperationJournalRepository Journal { get; }
        public LibraryWriteLock Lock { get; } = new();
        public FileOperationService Service { get; }
        public long RootId { get; private set; }

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Μουσική");
            Directory.CreateDirectory(Music);
            Journal = new FileOperationJournalRepository(Db.Factory, Db.Writes, Db.Folders);
            Service = NewService();
        }

        public FileOperationService NewService() => new(Mover, Journal, Lock, NullLogger<FileOperationService>.Instance);

        public string Track(string relative, string artist, string title, int frames = 30)
        {
            var path = Path.Combine(Music, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path))
            {
                var frame = new byte[417];
                frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
                for (var i = 0; i < frames; i++) fs.Write(frame);
            }
            using var tl = TagLib.File.Create(path);
            tl.Tag.Performers = [artist];
            tl.Tag.Title = title;
            tl.Save();
            return path;
        }

        public async Task ScanAsync()
        {
            if (RootId == 0) RootId = (await Db.Roots.AddAsync(Music)).Id;
            await new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
                .ScanRootAsync(RootId, null, default);
        }

        public long IdOf(string fileName)
        {
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @fileName", new { fileName });
        }

        public string? PathOf(long id)
        {
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<string?>("SELECT FullPath FROM Tracks WHERE Id = @id", new { id });
        }

        public async Task<IReadOnlyList<PlanItem>> PlanAsync(string template, bool sidecars = true, params string[] fileNames)
        {
            var ids = fileNames.Select(IdOf).ToList();
            var sources = await Journal.GetAsync(ids, default);
            return FileOperationPlanner.PlanRename(sources, new RenameOptions(template, false, sidecars), new SystemFileMover());
        }

        public List<string> JournalStates()
        {
            using var conn = Db.Factory.Open();
            return conn.Query<string>("SELECT State FROM FileOperations ORDER BY Id").ToList();
        }

        public Dictionary<string, string> ContentHashes() => Directory.EnumerateFiles(Music, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task ARename_ChangesTheName_KeepsTheTrackRow_AndNeverTouchesTheBytes()
    {
        await using var e = new Env();
        var a = e.Track("old1.mp3", "Χάρις Αλεξίου", "Σ' αγαπώ", 30);
        e.Track("old2.mp3", "Madonna", "Frozen", 31);
        File.WriteAllText(Path.ChangeExtension(a, ".lrc"), "[00:01.00]hello");
        File.WriteAllText(Path.Combine(e.Music, "cover.jpg"), "not really a jpg");
        await e.ScanAsync();
        var idA = e.IdOf("old1.mp3");
        var bytesBefore = e.ContentHashes().Values.OrderBy(h => h).ToList();

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old1.mp3", "old2.mp3");
        var result = await e.Service.RenameAsync(plan, "{Artist} - {Title}", null, default);

        Assert.Equal((2, 1, 0), (result.Renamed, result.SidecarsRenamed, result.Failures.Count));
        Assert.True(File.Exists(Path.Combine(e.Music, "Χάρις Αλεξίου - Σ' αγαπώ.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "Χάρις Αλεξίου - Σ' αγαπώ.lrc")));
        Assert.True(File.Exists(Path.Combine(e.Music, "Madonna - Frozen.mp3")));
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(e.Music, "cover.jpg")));                      // cover images are never moved
        Assert.Equal(bytesBefore, e.ContentHashes().Values.OrderBy(h => h).ToList());       // same bytes under new names

        Assert.Equal(Path.Combine(e.Music, "Χάρις Αλεξίου - Σ' αγαπώ.mp3"), e.PathOf(idA));  // the SAME row followed the file
        Assert.All(e.JournalStates(), s => Assert.Equal("Done", s));
        Assert.Empty(e.Db.Match("FullPath:old1"));                                         // the search index follows the new name
        Assert.Single(e.Db.Match("FileName:Frozen"));
    }

    [Fact]
    public async Task APlayHistoryRow_StillBelongsToTheTrackAfterTheRename()
    {
        await using var e = new Env();
        e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var id = e.IdOf("old.mp3");
        using (var conn = e.Db.Factory.Open())
            conn.Execute("INSERT INTO PlayHistory(TrackId, PlayedUtc, Status, Source) VALUES (@id, '2025-01-01T00:00:00.0000000Z', 'Confirmed', 'test')", new { id });

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");
        await e.Service.RenameAsync(plan, "t", null, default);
        await e.ScanAsync();                                                              // a later scan must not see a new/missing pair

        using var check = e.Db.Factory.Open();
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks WHERE Missing = 1"));
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM PlayHistory WHERE TrackId = @id", new { id }));
    }

    [Fact]
    public async Task TheJournalRow_IsInProgress_BeforeTheFileIsTouched()
    {
        await using var e = new Env();
        e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var seen = new List<string>();
        e.Mover.BeforeMove = (_, _) => seen.AddRange(e.JournalStates());

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");
        await e.Service.RenameAsync(plan, "t", null, default);

        Assert.Equal(["InProgress"], seen);
    }

    [Fact]
    public async Task AFileThatAppearedSincePreview_IsNeverOverwritten_AndTheRestOfTheBatchContinues()
    {
        await using var e = new Env();
        e.Track("one.mp3", "A", "One");
        e.Track("two.mp3", "B", "Two");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "one.mp3", "two.mp3");
        var intruder = Path.Combine(e.Music, "A - One.mp3");
        File.WriteAllText(intruder, "somebody else's file");                              // appears after the preview

        var result = await e.Service.RenameAsync(plan, "t", null, default);

        Assert.Equal(1, result.Renamed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("appeared", failure.Reason);
        Assert.Equal("somebody else's file", File.ReadAllText(intruder));
        Assert.True(File.Exists(Path.Combine(e.Music, "one.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "B - Two.mp3")));
        Assert.Equal(["Failed", "Done"], e.JournalStates());
    }

    [Fact]
    public async Task ALockedFile_FailsOnItsOwn_WithAReason()
    {
        await using var e = new Env();
        var locked = e.Track("one.mp3", "A", "One");
        e.Track("two.mp3", "B", "Two");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "one.mp3", "two.mp3");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await e.Service.RenameAsync(plan, "t", null, default);

            Assert.Equal(1, result.Renamed);
            Assert.Single(result.Failures);
        }
        Assert.True(File.Exists(locked));
        Assert.Equal(locked, e.PathOf(e.IdOf("one.mp3")));                                // the database still points at the file
    }

    [Fact]
    public async Task ACrashAfterTheMove_IsFinishedByRecovery_AndTheRowFollowsTheFile()
    {
        await using var e = new Env();
        e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var id = e.IdOf("old.mp3");
        e.Mover.AfterMove = (_, _) => throw new Crash();                                  // the process dies right after File.Move

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");
        await Assert.ThrowsAsync<Crash>(() => e.Service.RenameAsync(plan, "t", null, default));
        Assert.Equal(["InProgress"], e.JournalStates());
        Assert.EndsWith("old.mp3", e.PathOf(id));                                         // database not updated yet

        e.Mover.AfterMove = null;
        var recovery = await e.NewService().RecoverAsync(default);

        Assert.Equal((1, 0, 0), (recovery.Completed, recovery.RolledBack, recovery.NeedAttention.Count));
        Assert.Equal(Path.Combine(e.Music, "Madonna - Frozen.mp3"), e.PathOf(id));
        Assert.Equal(["Done"], e.JournalStates());
    }

    [Fact]
    public async Task ACrashBeforeTheMove_IsRolledBackByRecovery_NothingChanges()
    {
        await using var e = new Env();
        var old = e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var id = e.IdOf("old.mp3");
        e.Mover.BeforeMove = (_, _) => throw new Crash();

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");
        await Assert.ThrowsAsync<Crash>(() => e.Service.RenameAsync(plan, "t", null, default));

        e.Mover.BeforeMove = null;
        var recovery = await e.NewService().RecoverAsync(default);

        Assert.Equal((0, 1), (recovery.Completed, recovery.RolledBack));
        Assert.True(File.Exists(old));
        Assert.Equal(old, e.PathOf(id));
        Assert.Equal(["Failed"], e.JournalStates());
        Assert.Equal((0, 0, 0), ((await e.NewService().RecoverAsync(default)).Completed, 0, 0)); // nothing left to settle
    }

    [Fact]
    public async Task WhenBothFilesExist_RecoveryDoesNotGuess()
    {
        await using var e = new Env();
        var old = e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        e.Mover.BeforeMove = (_, to) => { File.WriteAllText(to, "someone made this"); throw new Crash(); };

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");
        await Assert.ThrowsAsync<Crash>(() => e.Service.RenameAsync(plan, "t", null, default));
        e.Mover.BeforeMove = null;

        var recovery = await e.NewService().RecoverAsync(default);

        Assert.Single(recovery.NeedAttention);
        Assert.True(File.Exists(old));
        Assert.Equal("someone made this", File.ReadAllText(Path.Combine(e.Music, "Madonna - Frozen.mp3")));
    }

    [Fact]
    public async Task Undo_RestoresEveryOldName_AndTheRowsFollowBack()
    {
        await using var e = new Env();
        var a = e.Track("one.mp3", "A", "One");
        var b = e.Track("two.mp3", "B", "Two");
        File.WriteAllText(Path.ChangeExtension(a, ".cue"), "FILE");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "one.mp3", "two.mp3");
        var batch = await e.Service.RenameAsync(plan, "t", null, default);
        var before = e.ContentHashes().Values.OrderBy(h => h).ToList();

        var undo = await e.Service.UndoAsync(batch.BatchId!, null, default);

        Assert.Equal((3, 0), (undo.Restored, undo.Refused.Count));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(Path.ChangeExtension(a, ".cue")));
        Assert.Equal(a, e.PathOf(e.IdOf("one.mp3")));
        Assert.Equal(before, e.ContentHashes().Values.OrderBy(h => h).ToList());
        var info = Assert.Single(await e.Service.GetBatchesAsync(5, default));
        Assert.False(info.CanUndo);
        Assert.Equal(2, info.Undone);
        Assert.Empty(await e.Service.GetPathMapAsync(batch.BatchId!, default));
    }

    [Fact]
    public async Task Undo_RefusesWhatNoLongerFits_AndDoesTheRest()
    {
        await using var e = new Env();
        e.Track("one.mp3", "A", "One");
        e.Track("two.mp3", "B", "Two");
        e.Track("three.mp3", "C", "Three");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "one.mp3", "two.mp3", "three.mp3");
        var batch = await e.Service.RenameAsync(plan, "t", null, default);

        File.WriteAllText(Path.Combine(e.Music, "one.mp3"), "a new file took the old name");   // old path occupied
        File.Delete(Path.Combine(e.Music, "B - Two.mp3"));                                       // renamed file gone

        var undo = await e.Service.UndoAsync(batch.BatchId!, null, default);

        Assert.Equal(1, undo.Restored);
        Assert.Equal(2, undo.Refused.Count);
        Assert.Equal("a new file took the old name", File.ReadAllText(Path.Combine(e.Music, "one.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "three.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "A - One.mp3")));                          // the refused one stays as it is
        Assert.True((await e.Service.GetBatchesAsync(5, default))[0].CanUndo);                    // the refused ones can be retried later
    }

    [Fact]
    public async Task Undo_OfAnOlderBatch_IsRefusedWhileALaterRenameOfTheSameFileStands()
    {
        await using var e = new Env();
        e.Track("one.mp3", "A", "One");
        await e.ScanAsync();
        var first = await e.Service.RenameAsync(await e.PlanAsync("{Artist} - {Title}", true, "one.mp3"), "t1", null, default);
        var second = await e.Service.RenameAsync(await e.PlanAsync("{Title}", true, "A - One.mp3"), "t2", null, default);
        Assert.Equal(1, second.Renamed);

        var undoFirst = await e.Service.UndoAsync(first.BatchId!, null, default);
        Assert.Equal(0, undoFirst.Restored);
        Assert.Contains("later rename", Assert.Single(undoFirst.Refused).Reason);

        Assert.Equal(1, (await e.Service.UndoAsync(second.BatchId!, null, default)).Restored);
        Assert.Equal(1, (await e.Service.UndoAsync(first.BatchId!, null, default)).Restored);
        Assert.True(File.Exists(Path.Combine(e.Music, "one.mp3")));
    }

    [Fact]
    public async Task ACaseOnlyRename_WorksOnTheRealFileSystem_AndCanBeUndone()
    {
        await using var e = new Env();
        e.Track("madonna - frozen.mp3", "Madonna", "Frozen");
        await e.ScanAsync();

        var plan = await e.PlanAsync("{Artist} - {Title}", true, "madonna - frozen.mp3");
        Assert.Equal(PlanStatus.CaseOnly, plan[0].Status);
        var batch = await e.Service.RenameAsync(plan, "t", null, default);

        Assert.Equal(1, batch.Renamed);
        Assert.True(new SystemFileMover().ExistsExact(Path.Combine(e.Music, "Madonna - Frozen.mp3")));
        Assert.False(new SystemFileMover().ExistsExact(Path.Combine(e.Music, "madonna - frozen.mp3")));
        Assert.Equal(Path.Combine(e.Music, "Madonna - Frozen.mp3"), e.PathOf(e.IdOf("Madonna - Frozen.mp3")));

        Assert.Equal(1, (await e.Service.UndoAsync(batch.BatchId!, null, default)).Restored);
        Assert.True(new SystemFileMover().ExistsExact(Path.Combine(e.Music, "madonna - frozen.mp3")));
    }

    [Fact]
    public async Task AScanInProgress_BlocksTheBatch_WithoutTouchingAnything()
    {
        await using var e = new Env();
        var old = e.Track("old.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "old.mp3");

        using (e.Lock.TryEnter())
        {
            var result = await e.Service.RenameAsync(plan, "t", null, default);
            Assert.NotNull(result.Refused);
            Assert.Equal(0, result.Renamed);
        }
        Assert.True(File.Exists(old));
        Assert.Empty(e.JournalStates());

        Assert.Equal(1, (await e.Service.RenameAsync(plan, "t", null, default)).Renamed);       // free again
    }

    [Fact]
    public async Task Cancelling_StopsBetweenFiles_AndMarksTheRestSkipped()
    {
        await using var e = new Env();
        e.Track("one.mp3", "A", "One");
        e.Track("two.mp3", "B", "Two");
        await e.ScanAsync();
        var plan = await e.PlanAsync("{Artist} - {Title}", true, "one.mp3", "two.mp3");
        using var cts = new CancellationTokenSource();
        e.Mover.AfterMove = (_, _) => cts.Cancel();

        var result = await e.Service.RenameAsync(plan, "t", null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Renamed);
        Assert.Equal(["Done", "Skipped"], e.JournalStates());
        Assert.True(File.Exists(Path.Combine(e.Music, "two.mp3")));
    }

    [Fact]
    public async Task ThePathMap_ListsOldAndNew_AsCsvWithABom()
    {
        await using var e = new Env();
        e.Track("old, x.mp3", "Madonna", "Frozen");
        await e.ScanAsync();
        var batch = await e.Service.RenameAsync(await e.PlanAsync("{Artist} - {Title}", true, "old, x.mp3"), "t", null, default);

        var map = await e.Service.GetPathMapAsync(batch.BatchId!, default);
        var csv = Path.Combine(e.Db.Dir, "map.csv");
        PathMapCsv.Write(csv, [.. map, new PathMapEntry("C:/a \"q\".mp3", "C:/b.mp3")]);

        var bytes = File.ReadAllBytes(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("OldPath,NewPath\r\n", text);
        Assert.Contains("\"C:/a \"\"q\"\".mp3\"", text);
        Assert.Contains("old, x.mp3", text);
        Assert.Contains("Madonna - Frozen.mp3", text);
    }

    [Fact]
    public async Task TheGenreToken_IsTheTaxonomyTopGenre_FallingBackToTheRawTag()
    {
        await using var e = new Env();
        await e.Db.SeedTaxonomyAsync();
        e.Track("a.mp3", "A", "One");
        e.Track("b.mp3", "B", "Two");
        await e.ScanAsync();
        var idA = e.IdOf("a.mp3");
        var idB = e.IdOf("b.mp3");
        using (var conn = e.Db.Factory.Open())
        {
            conn.Execute("UPDATE Tracks SET GenreRaw = 'Whatever Raw' WHERE Id = @idB", new { idB });
            var child = conn.ExecuteScalar<long>("SELECT Id FROM Genres WHERE ParentId IS NOT NULL LIMIT 1");
            conn.Execute("INSERT INTO TrackGenres(TrackId, GenreId, Source) VALUES (@idA, @child, 'User')", new { idA, child });
        }

        var sources = (await e.Journal.GetAsync([idA, idB], default)).ToDictionary(s => s.TrackId);

        Assert.Equal("Whatever Raw", sources[idB].Genre);
        Assert.Null(await ParentOf(e, sources[idA].Genre));                                   // a root genre of the taxonomy
        Assert.NotEqual("Whatever Raw", sources[idA].Genre);
    }

    private static async Task<long?> ParentOf(Env e, string? genreName)
    {
        using var conn = e.Db.Factory.Open();
        return await Task.FromResult(conn.ExecuteScalar<long?>("SELECT ParentId FROM Genres WHERE Name = @genreName", new { genreName }));
    }
}
