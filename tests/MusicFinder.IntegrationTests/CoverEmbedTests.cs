using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;

namespace MusicFinder.IntegrationTests;

/// <summary>Adding or replacing a front cover, on real generated files: only on request, verified, never costing other pictures or tags.</summary>
public class CoverEmbedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-cover-" + Guid.NewGuid().ToString("N"));
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();
    private static readonly TagWriteOptions NoBackup = new();

    public CoverEmbedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    internal static CoverImage NewCover() => CoverImageReader.TryRead(ArtworkTests.OtherPng)!;
    internal static CoverImage OldCover() => CoverImageReader.TryRead(ArtworkTests.TinyPng)!;

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Make(string kind, bool withFront = false, bool withBack = false)
    {
        var path = Path.Combine(_dir, $"song.{kind}");
        if (kind == "mp3") Fixtures.MakeMp3(path); else Fixtures.MakeFlac(path);
        using var f = TagLib.File.Create(path);
        f.Tag.Album = "Original Album";
        f.Tag.Comment = "keep this comment";
        var pictures = new List<TagLib.IPicture>();
        if (withFront) pictures.Add(new TagLib.Picture(new TagLib.ByteVector(ArtworkTests.TinyPng)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" });
        if (withBack) pictures.Add(new TagLib.Picture(new TagLib.ByteVector([9, 9, 9, 9, 9, 9, 9, 9])) { Type = TagLib.PictureType.BackCover, MimeType = "image/png", Description = "back" });
        f.Tag.Pictures = [.. pictures];
        f.Save();
        return path;
    }

    private static TagChangeSet CoverOnly(CoverImage cover, bool replace = false) =>
        new(new Dictionary<TagField, string?>(), cover, replace);

    public static TheoryData<string> Formats => new() { "mp3", "flac" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ACover_IsEmbeddedInAFileWithout_ExactlyAsGiven_AndOtherTagsSurvive(string kind)
    {
        var path = Make(kind);
        var cover = NewCover();

        var result = await _writer.WriteAsync(path, CoverOnly(cover), NoBackup, default);

        Assert.True(result.Success, result.Error);
        var read = await _reader.ReadAsync(path, default);
        Assert.Equal(cover.Hash, read.ArtworkHash);
        using var f = TagLib.File.Create(path);
        var picture = Assert.Single(f.Tag.Pictures);
        Assert.Equal(TagLib.PictureType.FrontCover, picture.Type);
        Assert.Equal(cover.Data, picture.Data.Data);                                   // byte for byte
        Assert.Equal(("Original Album", "keep this comment"), (f.Tag.Album, f.Tag.Comment));
        Assert.Equal(cover.Hash, result.After!.ArtworkHash);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ReplacingTheFrontCover_KeepsEveryOtherPicture(string kind)
    {
        var path = Make(kind, withFront: true, withBack: true);
        var cover = NewCover();

        var result = await _writer.WriteAsync(path, CoverOnly(cover, replace: true), NoBackup, default);

        Assert.True(result.Success, result.Error);
        using var f = TagLib.File.Create(path);
        Assert.Equal(2, f.Tag.Pictures.Length);
        Assert.Equal(cover.Data, f.Tag.Pictures.Single(p => p.Type == TagLib.PictureType.FrontCover).Data.Data);
        var back = f.Tag.Pictures.Single(p => p.Type == TagLib.PictureType.BackCover);
        Assert.Equal(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, back.Data.Data);
        Assert.Equal("back", back.Description);
    }

    [Fact]
    public async Task ACoverAndTextChanges_AreWrittenTogether()
    {
        var path = Make("mp3");
        var changes = new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Title] = "Ξανά στη ζωή μου", [TagField.Year] = "1985" }, NewCover());

        var result = await _writer.WriteAsync(path, changes, NoBackup, default);

        Assert.True(result.Success, result.Error);
        var read = await _reader.ReadAsync(path, default);
        Assert.Equal(("Ξανά στη ζωή μου", 1985, NewCover().Hash), (read.Title, read.Year, read.ArtworkHash));
    }

    [Fact]
    public async Task WritingTextOnly_StillKeepsTheCover()
    {
        var path = Make("mp3", withFront: true);
        var before = (await _reader.ReadAsync(path, default)).ArtworkHash;

        var result = await _writer.WriteAsync(path, new TagChangeSet(new Dictionary<TagField, string?> { [TagField.Title] = "T" }), NoBackup, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(before, (await _reader.ReadAsync(path, default)).ArtworkHash);
    }

    [Theory]
    [InlineData("not an image at all, just some text padding padding")]
    [InlineData("")]
    public async Task ABadCover_IsRefused_AndTheFileIsUntouched(string junk)
    {
        var path = Make("mp3");
        var before = Hash(path);
        var bad = new CoverImage(System.Text.Encoding.ASCII.GetBytes(junk), "image/jpeg", null, null);

        var result = await _writer.WriteAsync(path, CoverOnly(bad), NoBackup, default);

        Assert.False(result.Success);
        Assert.Contains("not a usable JPEG or PNG", result.Error);
        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task ACoverOnReadOnlyFile_FailsCleanly()
    {
        var path = Make("mp3");
        var before = Hash(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await _writer.WriteAsync(path, CoverOnly(NewCover()), NoBackup, default);

            Assert.False(result.Success);
            Assert.Equal(before, Hash(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    // ===== through the editing service: preview, rules for an existing cover, the library follows =====

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public TagEditingService Service { get; }
        public long TrackId { get; private set; }
        public string FilePath { get; private set; } = "";

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Music");
            Directory.CreateDirectory(Music);
            Service = new TagEditingService(Db.Tracks, new TagLibMetadataWriter(), Db.GenreJob);
        }

        public async Task IndexAsync(bool withCover)
        {
            await Db.SeedTaxonomyAsync();
            FilePath = Fixtures.MakeMp3(Path.Combine(Music, "a.mp3"));
            using (var f = TagLib.File.Create(FilePath))
            {
                f.Tag.Title = "A";
                if (withCover)
                    f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(ArtworkTests.TinyPng)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
                f.Save();
            }
            var root = await Db.Roots.AddAsync(Music);
            await new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance).ScanRootAsync(root.Id, null, default);
            TrackId = (await Db.Tracks.GetIndexedFilesAsync(root.Id)).Single().Id;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Preview_ListsTheCover_AndWritesNothing()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: false);
        var before = Hash(e.FilePath);

        var plan = await e.Service.PreviewAsync([e.TrackId], CoverOnly(NewCover()));

        var row = Assert.Single(plan.Changes);
        Assert.Equal((TagField.Cover, (string?)null), (row.Field, row.Before));
        Assert.Contains("PNG", row.After);
        Assert.Equal(before, Hash(e.FilePath));
    }

    [Fact]
    public async Task Apply_EmbedsTheCover_AndTheLibraryKeyFollows_AndTracksChangedFires()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: false);
        var raised = new List<long>();
        e.Service.TracksChanged += (_, ids) => raised.AddRange(ids);
        Assert.Null((await e.Db.Tracks.GetAsync(e.TrackId))!.ArtworkCacheKey);

        var summary = await e.Service.ApplyAsync([e.TrackId], CoverOnly(NewCover()), NoBackup, null, default);

        Assert.Equal((1, 0, 0), (summary.Written, summary.Unchanged, summary.Failed));
        Assert.Equal(NewCover().Hash, (await e.Db.Tracks.GetAsync(e.TrackId))!.ArtworkCacheKey);   // the grid will show the new cover
        Assert.Equal([e.TrackId], raised);
    }

    [Fact]
    public async Task AFileThatAlreadyHasACover_IsLeftAlone_UnlessReplacingWasAsked()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: true);
        var before = Hash(e.FilePath);

        var plan = await e.Service.PreviewAsync([e.TrackId], CoverOnly(NewCover()));
        var summary = await e.Service.ApplyAsync([e.TrackId], CoverOnly(NewCover()), NoBackup, null, default);

        Assert.Empty(plan.Changes);
        Assert.Equal((0, 1), (summary.Written, summary.Unchanged));
        Assert.Equal(before, Hash(e.FilePath));                                                    // not even touched
        Assert.Equal(OldCover().Hash, (await e.Db.Tracks.GetAsync(e.TrackId))!.ArtworkCacheKey);
    }

    [Fact]
    public async Task Replacing_ShowsWhatIsReplaced_AndDoesIt()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: true);

        var plan = await e.Service.PreviewAsync([e.TrackId], CoverOnly(NewCover(), replace: true));
        var summary = await e.Service.ApplyAsync([e.TrackId], CoverOnly(NewCover(), replace: true), NoBackup, null, default);

        var row = Assert.Single(plan.Changes);
        Assert.Equal("embedded cover", row.Before);
        Assert.Equal(1, summary.Written);
        Assert.Equal(NewCover().Hash, (await e.Db.Tracks.GetAsync(e.TrackId))!.ArtworkCacheKey);
    }

    [Fact]
    public async Task TheSameCoverAgain_NeedsNoWrite_EvenWhenReplacingIsAllowed()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: true);
        var before = Hash(e.FilePath);

        var summary = await e.Service.ApplyAsync([e.TrackId], CoverOnly(OldCover(), replace: true), NoBackup, null, default);

        Assert.Equal((0, 1), (summary.Written, summary.Unchanged));
        Assert.Equal(before, Hash(e.FilePath));
    }

    [Fact]
    public async Task ABadCover_IsAPreviewError_NotAWrite()
    {
        await using var e = new Env();
        await e.IndexAsync(withCover: false);

        var plan = await e.Service.PreviewAsync([e.TrackId], CoverOnly(new CoverImage([1, 2, 3], "image/png", null, null)));

        Assert.Contains(plan.Errors, m => m.Contains("not a usable JPEG or PNG"));
        Assert.Empty(plan.Changes);
    }
}
