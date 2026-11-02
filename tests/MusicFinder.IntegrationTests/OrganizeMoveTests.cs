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

/// <summary>Moving files into folders below their library folder: the rename machinery plus folder rows. Generated files in temp folders only.</summary>
public class OrganizeMoveTests
{
    private static readonly RenameOptions ByArtist = new("{Artist}/{Artist} - {Title}", MoveIntoFolders: true);

    /// <summary>The real file system with an optional crash hook after the move.</summary>
    private sealed class CrashingMover : IFileMover
    {
        private readonly SystemFileMover _real = new();
        public Action? AfterMove { get; set; }
        public bool Exists(string path) => _real.Exists(path);
        public bool ExistsExact(string path) => _real.ExistsExact(path);
        public void EnsureFolder(string directory) => _real.EnsureFolder(directory);
        public void Move(string from, string to) { _real.Move(from, to); AfterMove?.Invoke(); }
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public CrashingMover Mover { get; } = new();
        public FileOperationJournalRepository Journal { get; }
        public FileOperationService Service { get; }

        public Env(params (string File, string Artist, string Title)[] files)
        {
            Music = Path.Combine(Db.Dir, "Μουσική");
            Directory.CreateDirectory(Music);
            foreach (var (name, artist, title) in files)
            {
                var path = Path.Combine(Music, name);
                using (var fs = File.Create(path))
                {
                    var frame = new byte[417];
                    frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
                    for (var i = 0; i < 30; i++) fs.Write(frame);
                }
                using var tl = TagLib.File.Create(path);
                tl.Tag.Performers = [artist];
                tl.Tag.Title = title;
                tl.Save();
            }
            Journal = new FileOperationJournalRepository(Db.Factory, Db.Writes, Db.Folders);
            Service = NewService();
        }

        public FileOperationService NewService() => new(Mover, Journal, new LibraryWriteLock(), NullLogger<FileOperationService>.Instance);

        public async Task ScanAsync()
        {
            var root = (await Db.Roots.GetAllAsync()).FirstOrDefault() ?? await Db.Roots.AddAsync(Music);
            await new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
                .ScanRootAsync(root.Id, null, default);
        }

        public async Task<IReadOnlyList<PlanItem>> PlanAsync(params string[] names)
        {
            using var conn = Db.Factory.Open();
            var ids = names.Select(n => conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @n", new { n })).ToList();
            return FileOperationPlanner.PlanRename(await Journal.GetAsync(ids, default), ByArtist, new SystemFileMover());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task AMove_CreatesTheFolders_KeepsTheRow_UpdatesTheFolderTreeAndCounts()
    {
        await using var e = new Env(("a.mp3", "Madonna", "Frozen"), ("b.mp3", "Χάρις Αλεξίου", "Σ' αγαπώ"));
        await e.ScanAsync();
        using var conn = e.Db.Factory.Open();
        var idA = conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = 'a.mp3'");

        var result = await e.Service.RenameAsync(await e.PlanAsync("a.mp3", "b.mp3"), "t", null, default, "Organize");

        Assert.Equal((2, 0), (result.Renamed, result.Failures.Count));
        Assert.True(File.Exists(Path.Combine(e.Music, "Madonna", "Madonna - Frozen.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "Χάρις Αλεξίου", "Χάρις Αλεξίου - Σ' αγαπώ.mp3")));
        Assert.False(File.Exists(Path.Combine(e.Music, "a.mp3")));

        Assert.Equal(Path.Combine(e.Music, "Madonna", "Madonna - Frozen.mp3"), conn.ExecuteScalar<string>("SELECT FullPath FROM Tracks WHERE Id = @idA", new { idA }));
        var folder = conn.QuerySingle<(string Name, long Direct)>(
            "SELECT f.Name, f.DirectTrackCount FROM Tracks t JOIN Folders f ON f.Id = t.FolderId WHERE t.Id = @idA", new { idA });
        Assert.Equal(("Madonna", 1L), folder);
        var music = e.Music;
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT DirectTrackCount FROM Folders WHERE FullPath = @music", new { music }));
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT RecursiveTrackCount FROM Folders WHERE FullPath = @music", new { music }));
        Assert.Equal("Organize", conn.ExecuteScalar<string>("SELECT Kind FROM FileOperationBatches"));
    }

    [Fact]
    public async Task ARescanAfterAMove_FindsNothingNewAndNothingMissing()
    {
        await using var e = new Env(("a.mp3", "Madonna", "Frozen"));
        await e.ScanAsync();
        await e.Service.RenameAsync(await e.PlanAsync("a.mp3"), "t", null, default, "Organize");

        await e.ScanAsync();

        using var conn = e.Db.Factory.Open();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks WHERE Missing = 1"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Folders WHERE Name = 'Madonna'"));
    }

    [Fact]
    public async Task Undo_MovesTheFilesBack_AndLeavesTheEmptyFolderAlone()
    {
        await using var e = new Env(("a.mp3", "Madonna", "Frozen"));
        await e.ScanAsync();
        var batch = await e.Service.RenameAsync(await e.PlanAsync("a.mp3"), "t", null, default, "Organize");

        var undo = await e.Service.UndoAsync(batch.BatchId!, null, default);

        Assert.Equal(1, undo.Restored);
        Assert.True(File.Exists(Path.Combine(e.Music, "a.mp3")));
        Assert.True(Directory.Exists(Path.Combine(e.Music, "Madonna")));                  // nothing is ever deleted, folders included
        using var conn = e.Db.Factory.Open();
        var music = e.Music;
        Assert.Equal(Path.Combine(e.Music, "a.mp3"), conn.ExecuteScalar<string>("SELECT FullPath FROM Tracks"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT DirectTrackCount FROM Folders WHERE FullPath = @music", new { music }));
    }

    [Fact]
    public async Task ACrashRightAfterTheMove_IsCompletedByRecovery_IncludingTheFolderRow()
    {
        await using var e = new Env(("a.mp3", "Madonna", "Frozen"));
        await e.ScanAsync();
        var plan = await e.PlanAsync("a.mp3");
        e.Mover.AfterMove = () => throw new InvalidProgramException();
        await Assert.ThrowsAsync<InvalidProgramException>(() => e.Service.RenameAsync(plan, "t", null, default, "Organize"));
        e.Mover.AfterMove = null;

        var recovery = await e.NewService().RecoverAsync(default);

        Assert.Equal(1, recovery.Completed);
        using var conn = e.Db.Factory.Open();
        Assert.Equal("Madonna", conn.ExecuteScalar<string>("SELECT f.Name FROM Tracks t JOIN Folders f ON f.Id = t.FolderId"));
        Assert.Equal(Path.Combine(e.Music, "Madonna", "Madonna - Frozen.mp3"), conn.ExecuteScalar<string>("SELECT FullPath FROM Tracks"));
    }

    [Fact]
    public async Task AFileThatAppearedAtTheDestination_IsNeverOverwritten()
    {
        await using var e = new Env(("a.mp3", "Madonna", "Frozen"));
        await e.ScanAsync();
        var plan = await e.PlanAsync("a.mp3");
        Directory.CreateDirectory(Path.Combine(e.Music, "Madonna"));
        File.WriteAllText(Path.Combine(e.Music, "Madonna", "Madonna - Frozen.mp3"), "someone else's");

        var result = await e.Service.RenameAsync(plan, "t", null, default, "Organize");

        Assert.Equal(0, result.Renamed);
        Assert.Single(result.Failures);
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(e.Music, "Madonna", "Madonna - Frozen.mp3")));
        Assert.True(File.Exists(Path.Combine(e.Music, "a.mp3")));
    }
}
