using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;
using MusicFinder.Infrastructure.Backup;
using MusicFinder.Infrastructure.FileOperations;
using MusicFinder.Infrastructure.Persistence;
using MusicFinder.Infrastructure.Persistence.Repositories;
using MusicFinder.Infrastructure.Settings;

namespace MusicFinder.IntegrationTests;

/// <summary>Restore end to end on temp folders: staging never touches the live library, the swap survives a crash at any phase, and paths remap with history intact.</summary>
public class RestoreTests
{
    private sealed class Env : IAsyncDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mf-restore-" + Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Factory { get; }
        public SqliteWriteQueue Writes { get; private set; }
        public JsonSettingsStore Settings { get; }
        public LibraryBackupService Backup { get; }
        public LibraryWriteLock Lock { get; } = new();
        public FileOperationJournalRepository Journal { get; }
        public RestoreService Restore { get; }
        public long FolderId { get; private set; }

        public Env(int? maxMigration = null)
        {
            Directory.CreateDirectory(Dir);
            Paths = new AppPaths(Dir, Path.Combine(Dir, "musicfinder.db"), Path.Combine(Dir, "settings.json"), Path.Combine(Dir, "logs"), Path.Combine(Dir, "Cache"));
            Factory = new SqliteConnectionFactory(Paths.DatabasePath);
            new MigrationRunner(Factory).Migrate(maxMigration);
            Writes = new SqliteWriteQueue(Factory);
            Settings = new JsonSettingsStore(Paths.SettingsPath);
            Settings.Save(new AppSettings { AcoustIdApiKey = "LOCAL-KEY", MusicBrainzContact = "me@example.org", LeftPanelWidth = 300 });
            Backup = new LibraryBackupService(Factory, Settings, null, "1.0.0");
            Journal = new FileOperationJournalRepository(Factory, Writes);
            Restore = new RestoreService(Factory, Backup, Settings, Lock, Journal, Paths);
        }

        public async Task SeedAsync(string rootPath, int tracks, string rootName = "DJ Music")
        {
            var roots = new LibraryRootRepository(Factory, Writes);
            var folders = new FolderRepository(Factory, Writes);
            var trackRepo = new TrackRepository(Factory, Writes);
            var root = await roots.AddAsync(rootPath);
            FolderId = await folders.UpsertAsync(root.Id, null, rootPath, rootName);
            await trackRepo.UpsertBatchAsync(Enumerable.Range(0, tracks).Select(i => TestDb.MakeTrack(FolderId, rootPath + $@"\t{i}.mp3", $"Song {i}", "Artist")).ToList());
        }

        public long TrackId(string fileName)
        {
            using var c = Factory.Open();
            return c.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @fileName", new { fileName });
        }

        /// <summary>What the app does before the next start: nothing may hold the database any more.</summary>
        public async Task CloseLiveAsync()
        {
            await Writes.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        public SqliteConnection OpenLive()
        {
            var c = new SqliteConnection($"Data Source={Paths.DatabasePath};Pooling=False");
            c.Open();
            return c;
        }

        public string Archive(string name = "backup") => Path.Combine(Dir, "archives", name + LibraryBackupService.FileExtension);

        public async ValueTask DisposeAsync()
        {
            try { await Writes.DisposeAsync(); } catch (ObjectDisposedException) { }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private static List<string> TriggerSql(SqliteConnection c) =>
        c.Query<string>("SELECT sql FROM sqlite_master WHERE type='trigger' ORDER BY name").ToList();

    [Fact]
    public async Task ARestore_ReplacesTheLibrary_OnlyAtTheNextStart_AndKeepsThePreviousOne()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\DJ Music", 30);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        await e.Writes.EnqueueAsync((c, tx) => c.Execute("INSERT INTO PlayHistory(TrackId, PlayedUtc, Status, Source) VALUES (1, '2025-01-01T00:00:00.0000000Z', 'Confirmed', 'later')", transaction: tx));
        await new TrackRepository(e.Factory, e.Writes).UpsertBatchAsync(Enumerable.Range(100, 5).Select(i => TestDb.MakeTrack(e.FolderId, $@"D:\DJ Music\extra{i}.mp3", "Extra", "X")).ToList());

        var staged = await e.Restore.StageAsync(new RestoreRequest(archive, []), default);

        Assert.True(e.Restore.HasPendingRestore);
        Assert.True(File.Exists(staged.SafetyBackupPath));
        await e.Backup.InspectAsync(staged.SafetyBackupPath, default);                        // the safety backup is itself verified
        using (var live = e.Factory.Open())
            Assert.Equal(35, live.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));          // the live library is untouched until restart

        await e.CloseLiveAsync();
        var outcome = RestoreFinalizer.TryApply(e.Paths);

        Assert.True(outcome!.Applied);
        Assert.False(File.Exists(e.Paths.RestoreMarkerPath));
        using var restored = e.OpenLive();
        Assert.Equal(30, restored.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.Equal(0, restored.ExecuteScalar<int>("SELECT COUNT(*) FROM PlayHistory"));      // the play was added after the backup
        Assert.Equal("ok", restored.ExecuteScalar<string>("PRAGMA integrity_check"));
        using var previous = new SqliteConnection($"Data Source={outcome.PreviousDatabase};Pooling=False");
        previous.Open();
        Assert.Equal(35, previous.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));          // nothing was deleted
        Assert.True(File.Exists(e.Paths.RestoreHistoryPath));
    }

    [Fact]
    public async Task APathMapping_MovesEveryPath_KeepsTheIdsAndHistory_AndRebuildsTheSearchIndex()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\DJ Music", 20);
        var id = e.TrackId("t5.mp3");
        await e.Writes.EnqueueAsync((c, tx) => c.Execute(
            "INSERT INTO PlayHistory(TrackId, PlayedUtc, Status, Source) VALUES (@id, '2025-01-01T00:00:00.0000000Z', 'Confirmed', 'x')", new { id }, tx));
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        string[] triggersBefore;
        using (var c = e.Factory.Open()) triggersBefore = TriggerSql(c).ToArray();

        var staged = await e.Restore.StageAsync(new RestoreRequest(archive, [new PathMapping(@"D:\DJ Music", @"\\nas\Tunes")]), default);
        await e.CloseLiveAsync();
        RestoreFinalizer.TryApply(e.Paths);

        Assert.Equal((20, 1), (staged.TracksRemapped, staged.RootsRemapped));
        using var c2 = e.OpenLive();
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks WHERE FullPath NOT LIKE '\\\\nas\\Tunes\\%' ESCAPE '|'"));
        Assert.Equal(@"\\nas\Tunes\t5.mp3", c2.ExecuteScalar<string>("SELECT FullPath FROM Tracks WHERE Id = @id", new { id }));
        Assert.Equal(@"\\nas\Tunes", c2.ExecuteScalar<string>("SELECT Path FROM LibraryRoots"));
        Assert.Equal("Tunes", c2.ExecuteScalar<string>("SELECT Name FROM Folders"));
        Assert.Equal(1, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM PlayHistory WHERE TrackId = @id", new { id }));      // history followed the same id
        Assert.Equal(20, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM TrackSearch WHERE TrackSearch MATCH 'FullPath:Tunes'"));
        Assert.Equal(0, c2.ExecuteScalar<int>("SELECT COUNT(*) FROM TrackSearch WHERE TrackSearch MATCH 'FullPath:\"DJ\"'"));
        c2.Execute("INSERT INTO TrackSearch(TrackSearch) VALUES ('integrity-check')");                                   // throws if the index is inconsistent
        Assert.Equal(triggersBefore, TriggerSql(c2));                                                                      // the triggers are back, identical
    }

    [Fact]
    public async Task AMappingThatMergesTwoFolders_IsRefused_AndNothingIsChanged()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\A", 3, "A");
        await e.SeedAsync(@"D:\B", 3, "B");
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;

        var ex = await Assert.ThrowsAsync<RestoreRefusedException>(() => e.Restore.StageAsync(
            new RestoreRequest(archive, [new PathMapping(@"D:\A", @"E:\X"), new PathMapping(@"D:\B", @"E:\X")]), default));

        Assert.Contains("same place", ex.Message);
        Assert.False(e.Restore.HasPendingRestore);
        Assert.False(Directory.Exists(e.Paths.BackupsDir));                                                                // refused before the safety backup
    }

    [Fact]
    public async Task AnArchiveFromANewerVersion_IsRefused_BeforeAnythingIsWritten()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 2);
        var good = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        var future = e.Archive("future");
        File.Copy(good, future);
        using (var zip = ZipFile.Open(future, ZipArchiveMode.Update))
        {
            BackupManifest m;
            using (var r = zip.GetEntry("manifest.json")!.Open()) m = JsonSerializer.Deserialize<BackupManifest>(r)!;
            m.SchemaVersion = 999;
            zip.GetEntry("manifest.json")!.Delete();
            using var w = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(w, m);
        }

        var preview = await e.Restore.PreviewAsync(future, default);
        var ex = await Assert.ThrowsAsync<RestoreRefusedException>(() => e.Restore.StageAsync(new RestoreRequest(future, []), default));

        Assert.Equal(SchemaVerdict.TooNew, preview.Schema);
        Assert.Contains("newer", ex.Message);
        Assert.False(Directory.Exists(e.Paths.BackupsDir));
        Assert.False(e.Restore.HasPendingRestore);
    }

    [Fact]
    public async Task ADamagedArchive_IsRefused_AndTheLiveLibraryIsUntouched()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 20);
        var good = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        var bytes = File.ReadAllBytes(good);
        bytes[300] ^= 0xFF;
        var bad = e.Archive("bad");
        File.WriteAllBytes(bad, bytes);

        await Assert.ThrowsAsync<BackupFormatException>(() => e.Restore.StageAsync(new RestoreRequest(bad, []), default));

        Assert.False(e.Restore.HasPendingRestore);
        using var c = e.Factory.Open();
        Assert.Equal(20, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
    }

    [Fact]
    public async Task ARestoreWaitsWhileAScanOrAnUnsettledRenameIsRunning()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 3);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;

        using (e.Lock.TryEnter())
        {
            var ex = await Assert.ThrowsAsync<RestoreRefusedException>(() => e.Restore.StageAsync(new RestoreRequest(archive, []), default));
            Assert.Contains("scan", ex.Message);
        }

        var (batch, ops) = await e.Journal.BeginBatchAsync("Rename", "t", [new NewJournalOp(1, OpRole.Track, @"D:\M\a.mp3", @"D:\M\b.mp3")], default);
        await e.Journal.SetStateAsync(ops[0].Id, OpState.InProgress, null, null, default);
        var ex2 = await Assert.ThrowsAsync<RestoreRefusedException>(() => e.Restore.StageAsync(new RestoreRequest(archive, []), default));
        Assert.Contains("interrupted", ex2.Message);
        Assert.False(e.Restore.HasPendingRestore);
        Assert.NotNull(batch);
    }

    [Theory]
    [InlineData(RestorePhase.LiveMovedAside)]
    [InlineData(RestorePhase.DbSwapped)]
    public async Task ACrashAfterAnyPhase_IsFinishedOnTheNextStart(string crashAfter)
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\DJ Music", 12);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        await new TrackRepository(e.Factory, e.Writes).UpsertBatchAsync([TestDb.MakeTrack(e.FolderId, @"D:\DJ Music\later.mp3", "Later", "X")]);
        await e.Restore.StageAsync(new RestoreRequest(archive, [new PathMapping(@"D:\DJ Music", @"E:\Music")]), default);
        await e.CloseLiveAsync();

        Assert.Throws<InvalidProgramException>(() => RestoreFinalizer.TryApply(e.Paths, phase => { if (phase == crashAfter) throw new InvalidProgramException(); }));
        Assert.True(File.Exists(e.Paths.RestoreMarkerPath));                                  // still pending

        var outcome = RestoreFinalizer.TryApply(e.Paths);

        Assert.True(outcome!.Applied);
        Assert.False(File.Exists(e.Paths.RestoreMarkerPath));
        using var c = e.OpenLive();
        Assert.Equal(12, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks WHERE FullPath LIKE 'D:%'"));
        using var previous = new SqliteConnection($"Data Source={outcome.PreviousDatabase};Pooling=False");
        previous.Open();
        Assert.Equal(13, previous.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
    }

    [Fact]
    public async Task ACrashBetweenRecordingTheNameAndMovingTheLiveFile_StillFindsThePreviousLibrary()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 5);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        await e.Restore.StageAsync(new RestoreRequest(archive, []), default);
        await e.CloseLiveAsync();

        // the state after "name recorded, live file already moved aside, phase still Staged"
        var marker = RestoreFinalizer.ReadMarker(e.Paths)!;
        marker.PreviousDb = e.Paths.DatabasePath + ".previous";
        File.Move(e.Paths.DatabasePath, marker.PreviousDb);
        RestoreFinalizer.WriteMarker(e.Paths, marker);

        var outcome = RestoreFinalizer.TryApply(e.Paths);

        Assert.True(outcome!.Applied);
        Assert.Equal(marker.PreviousDb, outcome.PreviousDatabase);
        using var c = e.OpenLive();
        Assert.Equal(5, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
    }

    [Fact]
    public async Task AStagedDatabaseThatChangedAfterStaging_IsDropped_AndTheLibraryIsKept()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 7);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        var staged = await e.Restore.StageAsync(new RestoreRequest(archive, []), default);
        await e.CloseLiveAsync();
        File.AppendAllText(Path.Combine(staged.StagingDirectory, "staging.db"), "tamper");

        var outcome = RestoreFinalizer.TryApply(e.Paths);

        Assert.False(outcome!.Applied);
        Assert.False(File.Exists(e.Paths.RestoreMarkerPath));
        using var c = e.OpenLive();
        Assert.Equal(7, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
    }

    [Fact]
    public async Task IfTheNewDatabaseCannotBePutInPlace_ThePreviousOneComesBack()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 9);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        var staged = await e.Restore.StageAsync(new RestoreRequest(archive, []), default);
        await e.CloseLiveAsync();

        var marker = RestoreFinalizer.ReadMarker(e.Paths)!;
        marker.PreviousDb = e.Paths.DatabasePath + ".previous";
        File.Move(e.Paths.DatabasePath, marker.PreviousDb);
        File.Delete(marker.StagingDb);                                                      // the prepared copy vanished
        marker.Phase = RestorePhase.LiveMovedAside;
        RestoreFinalizer.WriteMarker(e.Paths, marker);

        var outcome = RestoreFinalizer.TryApply(e.Paths);

        Assert.False(outcome!.Applied);
        Assert.Contains("previous library is back", outcome.Message);
        using var c = e.OpenLive();
        Assert.Equal(9, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tracks"));
        Assert.NotNull(staged);
    }

    [Fact]
    public async Task ABackupFromAnOlderSchema_IsUpgradedInStaging()
    {
        await using var e = new Env(maxMigration: MigrationRunner.LatestVersion - 3);
        using (var c = e.Factory.Open())
        {
            c.Execute("INSERT INTO LibraryRoots(Path, Enabled, IncludeSubfolders, Status) VALUES ('D:\\Old', 1, 1, 'Pending')");
            c.Execute("INSERT INTO Folders(RootId, ParentId, FullPath, Name) VALUES (1, NULL, 'D:\\Old', 'Old')");
            c.Execute("INSERT INTO Tracks(FolderId, FullPath, FileName, Extension, FileSize, ModifiedUtc, IndexedUtc) VALUES (1, 'D:\\Old\\a.mp3', 'a.mp3', '.mp3', 1, '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z')");
        }
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        new MigrationRunner(e.Factory).Migrate();                                              // the running app is always on the latest schema; the archive is the old one
        var preview = await e.Restore.PreviewAsync(archive, default);
        Assert.Equal(SchemaVerdict.WillBeUpgraded, preview.Schema);

        await e.Restore.StageAsync(new RestoreRequest(archive, [new PathMapping(@"D:\Old", @"E:\New")]), default);
        await e.CloseLiveAsync();
        RestoreFinalizer.TryApply(e.Paths);

        using var restored = e.OpenLive();
        Assert.Equal(MigrationRunner.LatestVersion, restored.ExecuteScalar<int>("SELECT MAX(Version) FROM SchemaVersions"));
        Assert.Equal(@"E:\New\a.mp3", restored.ExecuteScalar<string>("SELECT FullPath FROM Tracks"));
    }

    [Fact]
    public async Task TheSettingsOfTheArchiveReturn_ButThisPcsSecretsAreKept()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 1);
        e.Settings.Save(new AppSettings { AcoustIdApiKey = "LOCAL-KEY", MusicBrainzContact = "me@example.org", LeftPanelWidth = 222 });
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;      // no secrets in it
        e.Settings.Save(new AppSettings { AcoustIdApiKey = "LOCAL-KEY", MusicBrainzContact = "me@example.org", LeftPanelWidth = 400 });

        await e.Restore.StageAsync(new RestoreRequest(archive, []), default);
        await e.CloseLiveAsync();
        RestoreFinalizer.TryApply(e.Paths);

        var settings = e.Settings.Load();
        Assert.Equal(222, settings.LeftPanelWidth);                                           // from the archive
        Assert.Equal("LOCAL-KEY", settings.AcoustIdApiKey);                                   // kept from this PC
    }

    [Fact]
    public async Task APendingRestore_CanBeCancelled_BeforeItStarts()
    {
        await using var e = new Env();
        await e.SeedAsync(@"D:\M", 1);
        var archive = (await e.Backup.BackupAsync(new BackupRequest(e.Archive()), default)).Path;
        var staged = await e.Restore.StageAsync(new RestoreRequest(archive, []), default);

        e.Restore.CancelPendingRestore();

        Assert.False(e.Restore.HasPendingRestore);
        Assert.False(Directory.Exists(staged.StagingDirectory));
        Assert.Null(RestoreFinalizer.TryApply(e.Paths));
    }
}
