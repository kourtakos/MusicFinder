using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;

namespace MusicFinder.IntegrationTests;

/// <summary>What the scanner records about each file's quality: container facts, unreadable files, skipped formats.</summary>
public class QualityScanTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public LibraryScanner Scanner { get; }

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Μουσική");
            Directory.CreateDirectory(Music);
            Scanner = new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance);
        }

        public async Task<long> ScanAsync()
        {
            var root = await Db.Roots.AddAsync(Music);
            await Scanner.ScanRootAsync(root.Id, null, default);
            return root.Id;
        }

        public async Task<Track> TrackAsync(string fileName)
        {
            using var conn = Db.Factory.Open();
            var id = conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FileName = @fileName", new { fileName });
            return (await Db.Tracks.GetAsync(id))!;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task TheContainerFacts_AreStored()
    {
        await using var e = new Env();
        Fixtures.MakeMp3(Path.Combine(e.Music, "a.mp3"));
        Fixtures.MakeFlac(Path.Combine(e.Music, "b.flac"));
        await e.ScanAsync();

        var mp3 = await e.TrackAsync("a.mp3");
        Assert.Contains("MPEG", mp3.Codec);
        Assert.Equal(2, mp3.Channels);
        Assert.Null(mp3.ReadError);

        var flac = await e.TrackAsync("b.flac");
        Assert.Contains("FLAC", flac.Codec, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, flac.BitDepth);
        Assert.Equal(Track.CurrentMetaVersion, flac.MetaVersion);
    }

    [Fact]
    public async Task AFileWhoseTagsCannotBeRead_IsStillIndexed_WithTheReasonAndTheUnreadableFlag()
    {
        await using var e = new Env();
        await File.WriteAllBytesAsync(Path.Combine(e.Music, "broken.mp3"), new byte[300]);
        Fixtures.MakeMp3(Path.Combine(e.Music, "fine.mp3"));
        await e.ScanAsync();

        var broken = await e.TrackAsync("broken.mp3");
        Assert.False(string.IsNullOrEmpty(broken.ReadError));
        Assert.Equal((int)IssueFlags.Unreadable, broken.IssueFlags);

        var fine = await e.TrackAsync("fine.mp3");
        Assert.Null(fine.ReadError);
        Assert.False(((IssueFlags)fine.IssueFlags).HasFlag(IssueFlags.Unreadable));
    }

    [Fact]
    public async Task AudioLikeFilesTheScannerSkips_AreCountedPerExtension_AndOthersAreNot()
    {
        await using var e = new Env();
        Fixtures.MakeMp3(Path.Combine(e.Music, "a.mp3"));
        File.WriteAllText(Path.Combine(e.Music, "x.wma"), "x");
        File.WriteAllText(Path.Combine(e.Music, "y.WMA"), "x");
        Directory.CreateDirectory(Path.Combine(e.Music, "sub"));
        File.WriteAllText(Path.Combine(e.Music, "sub", "z.opus"), "x");
        File.WriteAllText(Path.Combine(e.Music, "notes.txt"), "x");
        await e.ScanAsync();

        var counts = await e.Db.Roots.GetUnsupportedAsync();

        Assert.Equal([(".wma", 2), (".opus", 1)], counts);
    }

    [Fact]
    public async Task TheUnsupportedCounts_AreReplacedByEachScan()
    {
        await using var e = new Env();
        var wma = Path.Combine(e.Music, "x.wma");
        File.WriteAllText(wma, "x");
        var rootId = await e.ScanAsync();
        Assert.Single(await e.Db.Roots.GetUnsupportedAsync());

        File.Delete(wma);
        await e.Scanner.ScanRootAsync(rootId, null, default);

        Assert.Empty(await e.Db.Roots.GetUnsupportedAsync());
    }

    [Fact]
    public async Task ARowIndexedByTheOlderReader_IsReadOnceMoreAndGainsTheNewFacts()
    {
        await using var e = new Env();
        Fixtures.MakeMp3(Path.Combine(e.Music, "a.mp3"));
        var rootId = await e.ScanAsync();
        using (var conn = e.Db.Factory.Open())
            conn.Execute("UPDATE Tracks SET MetaVersion = 1, Codec = NULL, Channels = NULL");

        await e.Scanner.ScanRootAsync(rootId, null, default);

        var t = await e.TrackAsync("a.mp3");
        Assert.Equal(Track.CurrentMetaVersion, t.MetaVersion);
        Assert.Equal(2, t.Channels);
    }

    [Fact]
    public async Task ATagWrite_RecomputesTheHints_AndClearsAReadError()
    {
        await using var e = new Env();
        await File.WriteAllBytesAsync(Path.Combine(e.Music, "a.mp3"), new byte[300]);
        await e.ScanAsync();
        var track = await e.TrackAsync("a.mp3");
        Assert.NotEqual(0, track.IssueFlags);

        await e.Db.Tracks.ApplyMetadataAsync(track.Id,
            new Domain.Models.TrackMetadata { Title = "t", DurationMs = 240_000, BitrateKbps = 320, SampleRateHz = 44100, Codec = "MPEG Version 1 Audio, Layer 3" },
            track.FileSize, DateTime.UtcNow, "t");

        var after = await e.Db.Tracks.GetAsync(track.Id);
        Assert.Null(after!.ReadError);
        Assert.False(((IssueFlags)after.IssueFlags).HasFlag(IssueFlags.Unreadable));
    }
}
