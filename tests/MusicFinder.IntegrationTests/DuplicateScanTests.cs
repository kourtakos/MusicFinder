using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Duplicates;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Duplicates;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;

namespace MusicFinder.IntegrationTests;

/// <summary>Exact duplicates on real generated files in temp folders: found by content, never by name, and nothing is ever touched.</summary>
public class DuplicateScanTests
{
    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings Current { get; set; } = new() { HashThrottleMbPerSec = 0 };
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    /// <summary>Counts how often each file is hashed, and can cancel after N files.</summary>
    private sealed class CountingHasher(IFileHashService inner) : IFileHashService
    {
        public List<string> Hashed { get; } = [];
        public Func<string, Task>? OnHash { get; set; }
        public async Task<string> HashAsync(string path, long maxBytesPerSecond, IProgress<long>? progress, CancellationToken ct)
        {
            Hashed.Add(path);
            if (OnHash is not null) await OnHash(path);
            return await inner.HashAsync(path, maxBytesPerSecond, progress, ct);
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public CountingHasher Hasher { get; } = new(new StreamingFileHasher());
        public DuplicateScanService Service { get; }
        public long RootId { get; private set; }

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Μουσική");
            Directory.CreateDirectory(Music);
            Service = new DuplicateScanService(Db.Hashes, Hasher, new FakeSettings());
        }

        /// <summary>Same audio bytes written under several names (a real, tag-free MP3 fixture).</summary>
        public string Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            return to;
        }

        public string Mp3(string name, int frames = 30)
        {
            var path = Path.Combine(Music, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
            for (var i = 0; i < frames; i++) fs.Write(frame);
            return path;
        }

        public async Task ScanLibraryAsync()
        {
            var root = await Db.Roots.AddAsync(Music);
            RootId = root.Id;
            await Rescan();
        }

        public Task Rescan() =>
            new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance).ScanRootAsync(RootId, null, default);

        public Task<long> IdOf(string fileName)
        {
            using var conn = Db.Factory.Open();
            return Task.FromResult(conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @fileName", new { fileName }));
        }

        public string Snapshot() => string.Join("|", Directory.EnumerateFiles(Music, "*", SearchOption.AllDirectories).OrderBy(f => f)
            .Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task TheSameBytesUnderDifferentNames_AreOneGroup_AndOtherFilesAreNot()
    {
        await using var e = new Env();
        var a = e.Mp3("Song.mp3");
        e.Copy(a, Path.Combine(e.Music, "Backup", "Song (1).mp3"));
        e.Copy(a, Path.Combine(e.Music, "Χάρις - Σ' αγαπώ.mp3"));
        e.Mp3("Other.mp3", frames: 31);               // different size: never hashed
        await e.ScanLibraryAsync();

        var summary = await e.Service.ScanAsync(null, default);
        var groups = await e.Service.GetGroupsAsync(default);

        Assert.Equal((3, 3, 1, 3), (summary.Candidates, summary.Hashed, summary.Groups, summary.FilesInGroups));
        var group = Assert.Single(groups);
        Assert.Equal(3, group.Members.Count);
        Assert.Equal(["Song (1).mp3", "Song.mp3", "Χάρις - Σ' αγαπώ.mp3"], group.Members.Select(m => Path.GetFileName(m.FullPath)).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain(e.Hasher.Hashed, p => p.EndsWith("Other.mp3"));
        Assert.Equal(await e.IdOf("Song.mp3"), group.SuggestedKeepTrackId);          // not named like a copy, and nearest the top
    }

    [Fact]
    public async Task SameSizeButDifferentBytes_IsNotADuplicate()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        var b = e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        var bytes = File.ReadAllBytes(b);
        bytes[^1] ^= 0xFF;                                  // one byte differs, the size does not
        File.WriteAllBytes(b, bytes);
        await e.ScanLibraryAsync();

        var summary = await e.Service.ScanAsync(null, default);

        Assert.Equal(2, summary.Hashed);
        Assert.Equal(0, summary.Groups);
        Assert.Empty(await e.Service.GetGroupsAsync(default));
    }

    [Fact]
    public async Task ASecondScan_HashesOnlyWhatIsNew()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        await e.ScanLibraryAsync();
        await e.Service.ScanAsync(null, default);
        Assert.Equal(2, e.Hasher.Hashed.Count);

        var again = await e.Service.ScanAsync(null, default);
        Assert.Equal((0, 2), (again.Hashed, again.AlreadyHashed));
        Assert.Equal(2, e.Hasher.Hashed.Count);

        e.Copy(a, Path.Combine(e.Music, "c.mp3"));
        await e.Rescan();
        var third = await e.Service.ScanAsync(null, default);

        Assert.Equal((1, 2), (third.Hashed, third.AlreadyHashed));
        Assert.Equal(3, e.Hasher.Hashed.Count);
        Assert.Equal(3, Assert.Single(await e.Service.GetGroupsAsync(default)).Members.Count);
    }

    [Fact]
    public async Task AStoppedScan_KeepsWhatItDid_AndResumesWithTheRest()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        for (var i = 0; i < 4; i++) e.Copy(a, Path.Combine(e.Music, $"copy{i}.mp3"));
        await e.ScanLibraryAsync();
        using var cts = new CancellationTokenSource();
        e.Hasher.OnHash = _ => { if (e.Hasher.Hashed.Count == 2) cts.Cancel(); return Task.CompletedTask; };

        var stopped = await e.Service.ScanAsync(null, cts.Token);

        Assert.True(stopped.Cancelled);
        Assert.InRange(stopped.Hashed, 1, 4);
        var done = stopped.Hashed;

        e.Hasher.OnHash = null;
        var resumed = await e.Service.ScanAsync(null, default);

        Assert.False(resumed.Cancelled);
        Assert.Equal(done, resumed.AlreadyHashed);
        Assert.Equal(5, resumed.AlreadyHashed + resumed.Hashed);
        Assert.Equal(5, Assert.Single(await e.Service.GetGroupsAsync(default)).Members.Count);
    }

    [Fact]
    public async Task AFileThatIsOpenElsewhereForSharedReading_StillHashes_AndOneThatIsGoneIsReportedNotThrown()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        var b = e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        var c = e.Copy(a, Path.Combine(e.Music, "c.mp3"));
        await e.ScanLibraryAsync();
        File.Delete(c);                                     // vanished after indexing

        using var open = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var summary = await e.Service.ScanAsync(null, default);

        Assert.Equal((2, 1), (summary.Hashed, summary.Unreadable));
        Assert.Equal(2, Assert.Single(await e.Service.GetGroupsAsync(default)).Members.Count);
    }

    [Fact]
    public async Task ChangingAFile_DropsItsRememberedHash_SoItLeavesTheGroup()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        var b = e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        await e.ScanLibraryAsync();
        await e.Service.ScanAsync(null, default);
        Assert.Single(await e.Service.GetGroupsAsync(default));

        using (var f = TagLib.File.Create(b)) { f.Tag.Title = "Now different"; f.Save(); }   // bytes (and mtime) change
        await e.Rescan();

        Assert.Empty(await e.Service.GetGroupsAsync(default));
        using var conn = e.Db.Factory.Open();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM FileHashes"));        // only the untouched file keeps its hash
    }

    [Fact]
    public async Task RenamingAFileInTheLibrary_KeepsItsHash()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        await e.ScanLibraryAsync();
        await e.Service.ScanAsync(null, default);
        var id = await e.IdOf("b.mp3");

        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute("UPDATE Tracks SET FileName = 'renamed.mp3', FullPath = FullPath || '.x' WHERE Id = @id", new { id }, t));

        Assert.Single(await e.Service.GetGroupsAsync(default));
    }

    [Fact]
    public async Task ADismissedGroup_StaysDismissed_OnLaterScans_AndCanBeRestored()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        await e.ScanLibraryAsync();
        await e.Service.ScanAsync(null, default);
        var group = Assert.Single(await e.Service.GetGroupsAsync(default));

        await e.Service.DismissAsync(group.Hash, default);
        await e.Service.ScanAsync(null, default);

        Assert.Empty(await e.Service.GetGroupsAsync(default));
        Assert.Equal(1, await e.Db.Hashes.RestoreDismissedAsync(default));
        Assert.Single(await e.Service.GetGroupsAsync(default));
    }

    [Fact]
    public async Task FilesThatAreMissingFromDisk_NeverCountAsDuplicates()
    {
        await using var e = new Env();
        var a = e.Mp3("a.mp3");
        var b = e.Copy(a, Path.Combine(e.Music, "b.mp3"));
        await e.ScanLibraryAsync();
        await e.Service.ScanAsync(null, default);
        File.Delete(b);
        await e.Rescan();                                   // b is flagged Missing

        Assert.Empty(await e.Service.GetGroupsAsync(default));
    }

    [Fact]
    public async Task APlayedCopy_IsTheSuggestedOne_AndNothingOnDiskIsEverChanged()
    {
        await using var e = new Env();
        var a = e.Mp3("Song.mp3");
        e.Copy(a, Path.Combine(e.Music, "Song (1).mp3"));
        await e.ScanLibraryAsync();
        var copyId = await e.IdOf("Song (1).mp3");
        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
            "INSERT INTO TrackPlayStats(TrackId, ConfirmedCount, PossibleCount) VALUES (@copyId, 2, 0)", new { copyId }, t));
        var before = e.Snapshot();

        await e.Service.ScanAsync(null, default);
        var group = Assert.Single(await e.Service.GetGroupsAsync(default));
        await e.Service.DismissAsync(group.Hash, default);

        Assert.Equal(copyId, group.SuggestedKeepTrackId);
        Assert.Contains(group.KeepReasons, r => r.Contains("most plays"));
        Assert.Equal(before, e.Snapshot());                 // every file, byte for byte, exactly as it was
    }

    [Fact]
    public async Task TheHasher_GivesTheKnownSha256_AndHonoursCancellationAndTheSpeedLimit()
    {
        await using var e = new Env();
        var path = Path.Combine(e.Music, "x.bin");
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(path, data);
        var hasher = new StreamingFileHasher();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), await hasher.HashAsync(path, 0, null, default));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hasher.HashAsync(path, 0, null, cts.Token));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await hasher.HashAsync(path, 8L * 1024 * 1024, null, default);             // 3 MB at 8 MB/s is at least ~0.35 s
        Assert.True(watch.ElapsedMilliseconds >= 250, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AnEmptyOrUniqueLibrary_HasNoCandidates()
    {
        await using var e = new Env();
        e.Mp3("a.mp3", 10);
        e.Mp3("b.mp3", 11);
        await e.ScanLibraryAsync();

        var summary = await e.Service.ScanAsync(null, default);

        Assert.Equal((0, 0, 0), (summary.Candidates, summary.Hashed, summary.Groups));
        Assert.Empty(e.Hasher.Hashed);
    }
}
