using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

public class ScannerTests
{
    /// <summary>Counts reads so tests can prove unchanged files are skipped.</summary>
    private sealed class CountingReader(IMetadataReader inner) : IMetadataReader
    {
        public int Reads;
        public Task<TrackMetadata> ReadAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return inner.ReadAsync(path, ct);
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public CountingReader Reader { get; } = new(new TagLibMetadataReader());
        public LibraryScanner Scanner { get; }
        public SqliteSearchService Search { get; }

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Music Ελλάδα"); // non-Latin folder name on purpose
            Directory.CreateDirectory(Music);
            Scanner = new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, Reader, NullLogger<LibraryScanner>.Instance);
            Search = new SqliteSearchService(Db.Factory);
        }

        public async Task<long> AddRootAsync() => (await Db.Roots.AddAsync(Music)).Id;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>Writes a tiny valid 8-bit mono WAV (0.1s of silence), optionally tagged via TagLib.</summary>
    private static string MakeWav(string path, string? title = null, string? artist = null, string? genre = null, uint? year = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int sampleRate = 8000, samples = 800;
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("RIFF"u8); w.Write(36 + samples); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sampleRate); w.Write(sampleRate); w.Write((short)1); w.Write((short)8);
            w.Write("data"u8); w.Write(samples);
            w.Write(Enumerable.Repeat((byte)128, samples).ToArray());
        }
        if (title is not null || artist is not null)
        {
            using var f = TagLib.File.Create(path);
            f.Tag.Title = title;
            if (artist is not null) f.Tag.Performers = [artist];
            if (genre is not null) f.Tag.Genres = [genre];
            if (year is not null) f.Tag.Year = year.Value;
            f.Save();
        }
        return path;
    }

    private static async Task<List<string>> TitlesAsync(Env e, string text) =>
        (await e.Search.SearchAsync(new SearchQuery { Text = text }, default)).Items.Select(i => i.Title).OrderBy(t => t).ToList();

    [Fact]
    public async Task Scan_IndexesTagsFoldersAndCounts()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "Disco", "Chic - Le Freak.wav"), "Le Freak", "Chic", "Disco", 1978);
        MakeWav(Path.Combine(e.Music, "Disco", "Nu", "Daft - Digital Love.wav"), "Digital Love", "Daft Punk", "Nu Disco");
        MakeWav(Path.Combine(e.Music, "Χάρις Αλεξίου - Σ' αγαπώ.wav"), "Σ' αγαπώ", "Χάρις Αλεξίου");
        var rootId = await e.AddRootAsync();

        var reports = new List<ScanProgress>();
        await e.Scanner.ScanRootAsync(rootId, new Progress<ScanProgress>(reports.Add), default);

        var root = (await e.Db.Roots.GetAsync(rootId))!;
        Assert.Equal(ScanStatus.Indexed, root.Status);
        Assert.NotNull(root.LastScanUtc);
        Assert.Equal(3, await e.Db.Tracks.CountAsync());

        Assert.Equal(["Le Freak"], await TitlesAsync(e, "chic"));
        Assert.Equal(["Σ' αγαπώ"], await TitlesAsync(e, "αλεξιου"));
        var freak = (await e.Search.SearchAsync(new SearchQuery { Text = "freak" }, default)).Items.Single();
        Assert.Equal(1978, freak.Year);
        Assert.Contains("Disco", freak.Genres);
        Assert.True(freak.DurationMs > 0);

        var top = (await e.Db.Folders.GetChildrenAsync(rootId, null)).Single();
        Assert.Equal((1, 3), (top.DirectTrackCount, top.RecursiveTrackCount));
        var disco = (await e.Db.Folders.GetChildrenAsync(rootId, top.Id)).Single();
        Assert.Equal("Disco", disco.Name);
        Assert.Equal((1, 2), (disco.DirectTrackCount, disco.RecursiveTrackCount));
    }

    [Fact]
    public async Task Rescan_SkipsUnchanged_RereadsModified_FlagsDeleted()
    {
        await using var e = new Env();
        var a = MakeWav(Path.Combine(e.Music, "a.wav"), "Alpha", "A");
        var b = MakeWav(Path.Combine(e.Music, "b.wav"), "Bravo", "B");
        MakeWav(Path.Combine(e.Music, "c.wav"), "Charlie", "C");
        var rootId = await e.AddRootAsync();

        await e.Scanner.ScanRootAsync(rootId, null, default);
        Assert.Equal(3, e.Reader.Reads);

        // Nothing changed: nothing re-read.
        await e.Scanner.ScanRootAsync(rootId, null, default);
        Assert.Equal(3, e.Reader.Reads);
        Assert.Equal(3, await e.Db.Tracks.CountAsync());

        // Retag one file, delete another.
        using (var f = TagLib.File.Create(a)) { f.Tag.Title = "Alpha Renamed"; f.Save(); }
        File.Delete(b);
        await e.Scanner.ScanRootAsync(rootId, null, default);

        Assert.Equal(4, e.Reader.Reads); // only the modified file
        Assert.Equal(2, await e.Db.Tracks.CountAsync());
        Assert.Equal(["Alpha Renamed"], await TitlesAsync(e, "alpha"));
        Assert.Empty(await TitlesAsync(e, "bravo")); // missing tracks are hidden from search...
        Assert.Equal(3, (await e.Db.Tracks.GetIndexedFilesAsync(rootId)).Count); // ...but kept in the DB

        var top = (await e.Db.Folders.GetChildrenAsync(rootId, null)).Single();
        Assert.Equal(2, top.DirectTrackCount);
    }

    [Fact]
    public async Task UnreadableFile_IsIndexedByFileName_AndScanContinues()
    {
        await using var e = new Env();
        File.WriteAllText(Path.Combine(e.Music, "Garbage Song.mp3"), "this is not audio");
        File.WriteAllBytes(Path.Combine(e.Music, "empty.flac"), []);
        File.WriteAllText(Path.Combine(e.Music, "notes.txt"), "ignored");
        MakeWav(Path.Combine(e.Music, "good.wav"), "Good", "Artist");
        var rootId = await e.AddRootAsync();

        ScanProgress? last = null;
        await e.Scanner.ScanRootAsync(rootId, new Progress<ScanProgress>(p => last = p), default);

        Assert.Equal(3, await e.Db.Tracks.CountAsync()); // .txt ignored
        Assert.Equal(["Garbage Song"], await TitlesAsync(e, "garbage"));
        Assert.Equal(ScanStatus.Indexed, (await e.Db.Roots.GetAsync(rootId))!.Status);
    }

    [Fact]
    public async Task OfflineRoot_IsNotTreatedAsDeletion()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "a.wav"), "Alpha", "A");
        var rootId = await e.AddRootAsync();
        await e.Scanner.ScanRootAsync(rootId, null, default);

        Directory.Move(e.Music, e.Music + "-unplugged"); // simulate a disconnected drive
        await e.Scanner.ScanRootAsync(rootId, null, default);

        Assert.Equal(ScanStatus.Offline, (await e.Db.Roots.GetAsync(rootId))!.Status);
        Assert.Equal(1, await e.Db.Tracks.CountAsync()); // still visible, not Missing

        Directory.Move(e.Music + "-unplugged", e.Music); // reconnect
        await e.Scanner.ScanRootAsync(rootId, null, default);
        Assert.Equal(ScanStatus.Indexed, (await e.Db.Roots.GetAsync(rootId))!.Status);
        Assert.Equal(1, await e.Db.Tracks.CountAsync());
    }

    [Fact]
    public async Task Cancellation_LeavesRootCancelledAndDbConsistent()
    {
        await using var e = new Env();
        for (var i = 0; i < 30; i++) MakeWav(Path.Combine(e.Music, $"d{i % 5}", $"t{i:00}.wav"), $"Title {i}", "Artist");
        var rootId = await e.AddRootAsync();

        using var cts = new CancellationTokenSource();
        var reads = 0;
        var cancelling = new LibraryScanner(e.Db.Roots, e.Db.Folders, e.Db.Tracks,
            new CallbackReader(e.Reader, () => { if (Interlocked.Increment(ref reads) == 5) cts.Cancel(); }),
            NullLogger<LibraryScanner>.Instance) { ReaderConcurrency = 1 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.ScanRootAsync(rootId, null, cts.Token));

        Assert.Equal(ScanStatus.Cancelled, (await e.Db.Roots.GetAsync(rootId))!.Status);
        var partial = await e.Db.Tracks.CountAsync();
        Assert.InRange(partial, 0, 29);

        // A later full scan completes and picks up everything.
        await e.Scanner.ScanRootAsync(rootId, null, default);
        Assert.Equal(30, await e.Db.Tracks.CountAsync());
        Assert.Equal(ScanStatus.Indexed, (await e.Db.Roots.GetAsync(rootId))!.Status);
    }

    [Fact]
    public async Task FoldersWithSystemOrHiddenAttributes_AreStillIndexed()
    {
        await using var e = new Env();
        var sys = Path.Combine(e.Music, "Customized Folder");
        MakeWav(Path.Combine(sys, "inside.wav"), "Inside", "A");
        File.SetAttributes(sys, File.GetAttributes(sys) | FileAttributes.System);
        var hidden = MakeWav(Path.Combine(e.Music, "hidden.wav"), "Hidden", "A");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var rootId = await e.AddRootAsync();

        await e.Scanner.ScanRootAsync(rootId, null, default);

        Assert.Equal(["Hidden", "Inside"], await TitlesAsync(e, ""));
    }

    [Fact]
    public async Task IncludeSubfoldersOff_StaysInRootFolder()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "top.wav"), "Top", "A");
        MakeWav(Path.Combine(e.Music, "sub", "deep.wav"), "Deep", "A");
        var rootId = await e.AddRootAsync();
        await e.Db.Writes.EnqueueAsync((c, t) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "UPDATE LibraryRoots SET IncludeSubfolders = 0";
            cmd.ExecuteNonQuery();
        });

        await e.Scanner.ScanRootAsync(rootId, null, default);
        Assert.Equal(["Top"], await TitlesAsync(e, ""));
    }

    private sealed class CallbackReader(IMetadataReader inner, Action onRead) : IMetadataReader
    {
        public Task<TrackMetadata> ReadAsync(string path, CancellationToken ct)
        {
            onRead();
            return inner.ReadAsync(path, ct);
        }
    }
}
