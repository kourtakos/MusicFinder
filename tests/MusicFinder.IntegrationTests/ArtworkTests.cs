using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Infrastructure.Artwork;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

public class ArtworkTests
{
    /// <summary>A valid 1x1 PNG, inline so no binary fixture is committed.</summary>
    internal static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    internal static readonly byte[] OtherPng = [.. TinyPng, 0]; // different bytes, same decodable prefix

    internal static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    /// <summary>Writes a tiny WAV and optionally embeds pictures (type, bytes) through TagLib.</summary>
    internal static string MakeWav(string path, params (TagLib.PictureType Type, byte[] Bytes)[] pictures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int samples = 800;
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("RIFF"u8); w.Write(36 + samples); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8);
            w.Write("data"u8); w.Write(samples);
            w.Write(Enumerable.Repeat((byte)128, samples).ToArray());
        }
        using var f = TagLib.File.Create(path);
        f.Tag.Title = Path.GetFileNameWithoutExtension(path);
        if (pictures.Length > 0)
            f.Tag.Pictures = pictures.Select(p => (TagLib.IPicture)new TagLib.Picture(new TagLib.ByteVector(p.Bytes))
                { Type = p.Type, MimeType = "image/png" }).ToArray();
        f.Save();
        return path;
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public ArtworkExtractor Extractor { get; } = new();

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Music");
            Directory.CreateDirectory(Music);
        }

        public LibraryScanner NewScanner(IMetadataReader? reader = null) =>
            new(Db.Roots, Db.Folders, Db.Tracks, reader ?? new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance);

        public async Task<long> ScanAsync(IMetadataReader? reader = null)
        {
            var root = await Db.Roots.AddAsync(Music);
            await NewScanner(reader).ScanRootAsync(root.Id, null, default);
            return root.Id;
        }

        public Task<string?> KeyOf(string file)
        {
            using var conn = Db.Factory.Open();
            return Task.FromResult(conn.ExecuteScalar<string?>("SELECT ArtworkCacheKey FROM Tracks WHERE FileName = @file", new { file }));
        }

        /// <summary>The key as the grid receives it, through the real search service.</summary>
        public async Task<string?> KeyViaSearchOf(string uniqueName)
        {
            var search = new SqliteSearchService(Db.Factory);
            return (await search.SearchAsync(new Domain.Models.SearchQuery { Text = $"\"{uniqueName}\"" }, default)).Items.Single().ArtworkCacheKey;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Scan_StoresTheContentHashOfTheEmbeddedCover_AndSearchReturnsIt()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "withcover.wav"), (TagLib.PictureType.FrontCover, TinyPng));
        MakeWav(Path.Combine(e.Music, "nocover.wav"));
        await e.ScanAsync();

        Assert.Equal(Sha1(TinyPng), await e.KeyOf("withcover.wav"));
        Assert.Null(await e.KeyOf("nocover.wav"));
        Assert.Equal(Sha1(TinyPng), await e.KeyViaSearchOf("withcover"));
        Assert.Null(await e.KeyViaSearchOf("nocover"));
    }

    [Fact]
    public async Task SameImageInTwoFiles_SharesOneKey_DifferentImagesDoNot()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "a.wav"), (TagLib.PictureType.FrontCover, TinyPng));
        MakeWav(Path.Combine(e.Music, "b.wav"), (TagLib.PictureType.FrontCover, TinyPng));
        MakeWav(Path.Combine(e.Music, "c.wav"), (TagLib.PictureType.FrontCover, OtherPng));
        await e.ScanAsync();

        Assert.Equal(await e.KeyOf("a.wav"), await e.KeyOf("b.wav")); // an album's tracks share one cached thumbnail
        Assert.NotEqual(await e.KeyOf("a.wav"), await e.KeyOf("c.wav"));
    }

    [Fact]
    public async Task FrontCoverIsPreferredOverOtherPictures_AndFirstPictureIsTheFallback()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "mixed.wav"), (TagLib.PictureType.BackCover, OtherPng), (TagLib.PictureType.FrontCover, TinyPng));
        MakeWav(Path.Combine(e.Music, "only-back.wav"), (TagLib.PictureType.BackCover, OtherPng));

        var mixed = await e.Extractor.GetEmbeddedAsync(Path.Combine(e.Music, "mixed.wav"), default);
        var onlyBack = await e.Extractor.GetEmbeddedAsync(Path.Combine(e.Music, "only-back.wav"), default);

        Assert.Equal(TinyPng, mixed!.Bytes);
        Assert.Equal("image/png", mixed.MimeType);
        Assert.Equal(Sha1(TinyPng), mixed.Hash);
        Assert.Equal(OtherPng, onlyBack!.Bytes);
    }

    [Fact]
    public async Task Extractor_ReturnsNull_ForMissingGarbageAndCoverlessFiles()
    {
        await using var e = new Env();
        var garbage = Path.Combine(e.Music, "garbage.mp3");
        File.WriteAllText(garbage, "not audio");
        var plain = MakeWav(Path.Combine(e.Music, "plain.wav"));

        Assert.Null(await e.Extractor.GetEmbeddedAsync(garbage, default));
        Assert.Null(await e.Extractor.GetEmbeddedAsync(plain, default));
        Assert.Null(await e.Extractor.GetEmbeddedAsync(Path.Combine(e.Music, "missing.mp3"), default));
    }

    [Fact]
    public async Task FolderArt_IsFoundCaseInsensitively_PrefersCoverOverFolder_AndIgnoresOtherFiles()
    {
        await using var e = new Env();
        var song = Path.Combine(e.Music, "album", "song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        File.WriteAllText(song, "x");

        Assert.Null(await e.Extractor.GetFolderArtAsync(song, default));

        File.WriteAllText(Path.Combine(e.Music, "album", "notes.jpg"), "not a cover name");
        File.WriteAllBytes(Path.Combine(e.Music, "album", "Folder.PNG"), OtherPng);
        var folderOnly = await e.Extractor.GetFolderArtAsync(song, default);
        Assert.Equal(OtherPng, folderOnly!.Bytes);
        Assert.Equal("image/png", folderOnly.MimeType);

        File.WriteAllBytes(Path.Combine(e.Music, "album", "COVER.JPG"), TinyPng);
        var both = await e.Extractor.GetFolderArtAsync(song, default);
        Assert.Equal(TinyPng, both!.Bytes);            // cover wins over folder
        Assert.Equal("image/jpeg", both.MimeType);
        Assert.Equal(Sha1(TinyPng), both.Hash);

        Assert.Null(await e.Extractor.GetFolderArtAsync(Path.Combine(e.Music, "nowhere", "x.mp3"), default));
    }

    [Fact]
    public async Task EmptyFolderArtFile_IsIgnored()
    {
        await using var e = new Env();
        var song = Path.Combine(e.Music, "song.mp3");
        File.WriteAllText(song, "x");
        File.WriteAllBytes(Path.Combine(e.Music, "cover.jpg"), []);
        Assert.Null(await e.Extractor.GetFolderArtAsync(song, default));
    }

    [Fact]
    public async Task Rescan_RereadsFilesFromAnOlderReader_EvenWhenSizeAndTimeAreUnchanged()
    {
        await using var e = new Env();
        MakeWav(Path.Combine(e.Music, "song.wav"), (TagLib.PictureType.FrontCover, TinyPng));
        var counting = new CountingReader(new TagLibMetadataReader());
        var rootId = await e.ScanAsync(counting);
        Assert.Equal(1, counting.Reads);

        // Same file, up to date: skipped.
        await e.NewScanner(counting).ScanRootAsync(rootId, null, default);
        Assert.Equal(1, counting.Reads);

        // Simulate a library indexed before the reader extracted artwork: MetaVersion 0 and no key.
        await e.Db.Writes.EnqueueAsync((c, t) =>
            c.Execute("UPDATE Tracks SET MetaVersion = 0, ArtworkCacheKey = NULL", transaction: t));
        Assert.Null(await e.KeyOf("song.wav"));

        await e.NewScanner(counting).ScanRootAsync(rootId, null, default);
        Assert.Equal(2, counting.Reads);                            // re-read once...
        Assert.Equal(Sha1(TinyPng), await e.KeyOf("song.wav"));     // ...and the cover key appears

        await e.NewScanner(counting).ScanRootAsync(rootId, null, default);
        Assert.Equal(2, counting.Reads);                            // and not again
        using var conn = e.Db.Factory.Open();
        Assert.Equal(Track.CurrentMetaVersion, conn.ExecuteScalar<int>("SELECT MetaVersion FROM Tracks"));
    }

    private sealed class CountingReader(IMetadataReader inner) : IMetadataReader
    {
        public int Reads;
        public Task<Domain.Models.TrackMetadata> ReadAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return inner.ReadAsync(path, ct);
        }
    }
}
