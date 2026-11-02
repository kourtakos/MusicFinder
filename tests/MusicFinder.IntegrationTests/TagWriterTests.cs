using System.Security.Cryptography;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Metadata;

namespace MusicFinder.IntegrationTests;

public class TagWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-write-" + Guid.NewGuid().ToString("N"));
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();
    private static readonly TagWriteOptions NoBackup = new();

    public TagWriteOptionsHolder Holder { get; } = new();

    public sealed class TagWriteOptionsHolder { }

    public TagWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static TagChangeSet Set(params (TagField Field, string? Value)[] edits) =>
        new(edits.ToDictionary(e => e.Field, e => e.Value));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] Leftovers(string dir) =>
        Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories);

    /// <summary>Creates a fixture of the given kind with some pre-existing tags that must survive an edit.</summary>
    private string Make(string kind)
    {
        var path = Path.Combine(_dir, $"song.{kind}");
        switch (kind)
        {
            case "wav": ArtworkTests.MakeWav(path, (TagLib.PictureType.FrontCover, ArtworkTests.TinyPng)); break;
            case "mp3": Fixtures.MakeMp3(path); break;
            case "flac": Fixtures.MakeFlac(path); break;
            default: throw new ArgumentException(kind);
        }
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Album = "Original Album";
            f.Tag.Comment = "keep this comment";
            f.Tag.BeatsPerMinute = 101;
            if (kind == "mp3")
                f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(ArtworkTests.TinyPng)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
            f.Save();
        }
        return path;
    }

    public static TheoryData<string> Formats => new() { "wav", "mp3", "flac" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task GreekAndAllFields_RoundTrip(string kind)
    {
        var path = Make(kind);

        var result = await _writer.WriteAsync(path, Set(
            (TagField.Title, "Ξανά στη ζωή μου"),
            (TagField.Artist, "Χάρις Αλεξίου"),
            (TagField.AlbumArtist, "Παπάζογλου & Φίλοι"),
            (TagField.Genre, "Λαϊκά; Pop"),
            (TagField.Year, "1985"),
            (TagField.TrackNumber, "3/12"),
            (TagField.Bpm, "128,4"),
            (TagField.Key, "8A")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var read = await _reader.ReadAsync(path, default); // an independent read of what is now on disk
        Assert.Equal("Ξανά στη ζωή μου", read.Title);
        Assert.Equal("Χάρις Αλεξίου", read.Artist);
        Assert.Equal("Παπάζογλου & Φίλοι", read.AlbumArtist);
        Assert.Equal("Λαϊκά; Pop", read.GenreRaw);
        Assert.Equal(1985, read.Year);
        Assert.Equal(3, read.TrackNumber);
        Assert.Equal(128, read.Bpm);
        Assert.Equal("8A", read.MusicalKey);
        Assert.Equal(read.Title, result.After!.Title);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task UntouchedFields_AndOtherFrames_Survive(string kind)
    {
        var path = Make(kind);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "Only the title")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var read = await _reader.ReadAsync(path, default);
        Assert.Equal("Only the title", read.Title);
        Assert.Equal("Original Album", read.Album);   // not selected, not touched
        Assert.Equal(101, read.Bpm);
        using var f = TagLib.File.Create(path);
        Assert.Equal("keep this comment", f.Tag.Comment); // a field the editor does not manage is preserved
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    public async Task TheEmbeddedCover_IsByteIdenticalAfterAnEdit(string kind)
    {
        var path = Make(kind);
        var before = await new MusicFinder.Infrastructure.Artwork.ArtworkExtractor().GetEmbeddedAsync(path, default);
        Assert.Equal(ArtworkTests.TinyPng, before!.Bytes);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "New title"), (TagField.Artist, "New artist")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var after = await new MusicFinder.Infrastructure.Artwork.ArtworkExtractor().GetEmbeddedAsync(path, default);
        Assert.Equal(ArtworkTests.TinyPng, after!.Bytes);
        Assert.Equal(before.Hash, after.Hash);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task BlankValue_ClearsOnlyThatField(string kind)
    {
        var path = Make(kind);
        await _writer.WriteAsync(path, Set((TagField.Title, "Has title"), (TagField.Year, "1999"), (TagField.Genre, "Disco")), NoBackup, default);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, null), (TagField.Year, "  ")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var read = await _reader.ReadAsync(path, default);
        Assert.Null(read.Title);
        Assert.Null(read.Year);
        Assert.Equal("Disco", read.GenreRaw);          // untouched
        Assert.Equal("Original Album", read.Album);
    }

    [Theory]
    [InlineData(TagField.Year, "1800")]
    [InlineData(TagField.Year, "abcd")]
    [InlineData(TagField.Year, "2101")]
    [InlineData(TagField.Bpm, "500")]
    [InlineData(TagField.Bpm, "12")]
    [InlineData(TagField.Bpm, "fast")]
    [InlineData(TagField.TrackNumber, "-1")]
    [InlineData(TagField.TrackNumber, "1000")]
    [InlineData(TagField.TrackNumber, "x/3")]
    [InlineData(TagField.Title, "bad\u0000title")]
    [InlineData(TagField.Key, "this-key-is-way-too-long")]
    public async Task InvalidValues_AreRejected_BeforeAnyFileIsTouched(TagField field, string value)
    {
        var path = Make("wav");
        var original = Hash(path);
        var changes = Set((field, value));

        Assert.NotEmpty(_writer.Validate(changes));
        var result = await _writer.WriteAsync(path, changes, NoBackup, default);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(original, Hash(path));
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public void Validate_ReportsEveryProblem_AndAcceptsEdgeValues()
    {
        var bad = Set((TagField.Year, "1800"), (TagField.Bpm, "1"), (TagField.TrackNumber, "-5"));
        Assert.Equal(3, _writer.Validate(bad).Count);

        Assert.Empty(_writer.Validate(Set((TagField.Year, "1900"), (TagField.Bpm, "40"), (TagField.TrackNumber, "0"), (TagField.Title, ""))));
        Assert.Empty(_writer.Validate(Set((TagField.Year, "2100"), (TagField.Bpm, "300"))));
        Assert.Empty(_writer.Validate(Set((TagField.Bpm, " 128.5 "))));
        Assert.Empty(_writer.Validate(Set((TagField.Bpm, "128,5"))));
    }

    [Fact]
    public async Task EmptyChangeSet_IsRefused()
    {
        var path = Make("wav");
        var result = await _writer.WriteAsync(path, Set(), NoBackup, default);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReadOnlyFile_IsNotWritten_AndNothingIsLeftBehind()
    {
        var path = Make("mp3");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var original = Hash(path);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "x")), NoBackup, default);

        Assert.False(result.Success);
        Assert.Contains("read-only", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, Hash(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)); // we never clear it silently
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task LockedFile_FailsCleanly_WithoutCorruptingIt()
    {
        var path = Make("mp3");
        var original = Hash(path);

        TagWriteResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // a DJ app holding the file open
            result = await _writer.WriteAsync(path, Set((TagField.Title, "x")), NoBackup, default);

        Assert.False(result.Success);
        Assert.Contains("in use", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, Hash(path));
        Assert.Empty(Leftovers(_dir));

        // Once released, the same write succeeds.
        var retry = await _writer.WriteAsync(path, Set((TagField.Title, "x")), NoBackup, default);
        Assert.True(retry.Success, retry.Error);
    }

    [Theory]
    [InlineData("garbage.mp3", "this is not audio")]
    [InlineData("empty.flac", "")]
    public async Task UnreadableFiles_AreLeftUntouched(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        var original = Hash(path);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "x")), NoBackup, default);

        Assert.False(result.Success);
        Assert.Equal(original, Hash(path));
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task MissingFile_ReportsAnErrorInsteadOfThrowing()
    {
        var result = await _writer.WriteAsync(Path.Combine(_dir, "gone.mp3"), Set((TagField.Title, "x")), NoBackup, default);
        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
    }

    [Fact]
    public async Task Backup_KeepsTheOriginalBytes_OutsideTheMusicFolder()
    {
        var path = Make("mp3");
        var original = Hash(path);
        var backups = Path.Combine(_dir, "..", "mf-backups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await _writer.WriteAsync(path, Set((TagField.Title, "Changed")), new TagWriteOptions(true, backups), default);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.BackupPath);
            Assert.StartsWith(Path.GetFullPath(backups), Path.GetFullPath(result.BackupPath!));
            Assert.Equal(original, Hash(result.BackupPath!));           // the pre-edit file, byte for byte
            Assert.NotEqual(original, Hash(path));                       // the live file changed
            Assert.Equal("Changed", (await _reader.ReadAsync(path, default)).Title);
        }
        finally
        {
            try { Directory.Delete(backups, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task BackupRequestedWithoutAFolder_IsRefusedBeforeWriting()
    {
        var path = Make("mp3");
        var original = Hash(path);
        var result = await _writer.WriteAsync(path, Set((TagField.Title, "x")), new TagWriteOptions(true, null), default);
        Assert.False(result.Success);
        Assert.Equal(original, Hash(path));
    }

    [Fact]
    public async Task Success_ReturnsTheNewSizeAndTime_AndLeavesNoTemporaryFiles()
    {
        var path = Make("mp3");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "A fairly long new title that grows the tag")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var info = new FileInfo(path);
        Assert.Equal(info.Length, result.NewSize);
        Assert.Equal(info.LastWriteTimeUtc, result.NewModifiedUtc);
        Assert.True(result.NewModifiedUtc > stamp);          // a tag write is a real modification the scanner must notice
        Assert.Equal([path], Directory.GetFiles(_dir));      // no hidden temp or stray files
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task WritingTheSameValuesTwice_Works()
    {
        var path = Make("mp3");
        var edit = Set((TagField.Title, "Same"), (TagField.Genre, "House"));
        Assert.True((await _writer.WriteAsync(path, edit, NoBackup, default)).Success);
        Assert.True((await _writer.WriteAsync(path, edit, NoBackup, default)).Success);
        Assert.Equal("Same", (await _reader.ReadAsync(path, default)).Title);
    }

    [Fact]
    public async Task NewMp3Tags_AreWrittenAsId3v23_AndGenreAsText()
    {
        var path = Fixtures.MakeMp3(Path.Combine(_dir, "fresh.mp3"));

        var result = await _writer.WriteAsync(path, Set((TagField.Title, "Ξανά"), (TagField.Genre, "House")), NoBackup, default);

        Assert.True(result.Success, result.Error);
        using var f = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2);
        Assert.Equal(3, id3.Version);                          // v2.3 for maximum compatibility with DJ software
        Assert.Equal("House", id3.GetTextAsString("TCON"));    // text, not the numeric "(35)" code
        Assert.Equal("Ξανά", id3.Title);
    }

    [Fact]
    public async Task Cancellation_BeforeTheWrite_LeavesTheFileUntouched()
    {
        var path = Make("mp3");
        var original = Hash(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _writer.WriteAsync(path, Set((TagField.Title, "x")), NoBackup, cts.Token));

        Assert.Equal(original, Hash(path));
        Assert.Empty(Leftovers(_dir));
    }

    [Fact]
    public async Task GenreText_IsSplitOnSemicolons_IntoSeparateValues()
    {
        var path = Make("mp3");
        await _writer.WriteAsync(path, Set((TagField.Genre, " Disco ;; Funk ; ")), NoBackup, default);

        using var f = TagLib.File.Create(path);
        Assert.Equal(["Disco", "Funk"], f.Tag.Genres);
    }
}
