using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Identification;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>An identified candidate applied through the one safe write path, on real generated files.</summary>
public class IdentificationApplyTests
{
    private const string Rec = "5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33";

    private static readonly IdentificationCandidate Candidate = new(
        Rec, "Ξανά στη ζωή μου", "Χάρις Αλεξίου", 225_000, IdentificationMethod.AcoustIdFingerprint, 0.97, null,
        [], [], ["laiko"], []);

    private static readonly ReleaseDetails Release = new(
        "rel-1", "Το Τραγούδι", "1985-03-02", "GR", "Χάρις Αλεξίου", [],
        [new ReleaseTrack(Rec, 1, 5, 9, "Ξανά στη ζωή μου", "Χάρις Αλεξίου")]);

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public string Music { get; }
        public TagEditingService Service { get; }
        public SqliteSearchService Search { get; }
        public long TrackId { get; private set; }
        public string Path_ { get; private set; } = "";

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Music");
            Directory.CreateDirectory(Music);
            Service = new TagEditingService(Db.Tracks, new TagLibMetadataWriter(), Db.GenreJob);
            Search = new SqliteSearchService(Db.Factory);
        }

        public async Task IndexAsync(string title, string? artist, string? album)
        {
            await Db.SeedTaxonomyAsync();
            Path_ = Fixtures.MakeMp3(System.IO.Path.Combine(Music, "unknown track.mp3"));
            using (var f = TagLib.File.Create(Path_))
            {
                f.Tag.Title = title;
                f.Tag.Performers = artist is null ? [] : [artist];
                f.Tag.Album = album;
                f.Tag.Genres = ["Disco"];
                f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(ArtworkTests.TinyPng)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
                f.Save();
            }
            var root = await Db.Roots.AddAsync(Music);
            await new LibraryScanner(Db.Roots, Db.Folders, Db.Tracks, new TagLibMetadataReader(), NullLogger<LibraryScanner>.Instance)
                .ScanRootAsync(root.Id, null, default);
            TrackId = (await Db.Tracks.GetIndexedFilesAsync(root.Id)).Single().Id;
        }

        public async Task<List<string>> TitlesFor(string text) =>
            (await Search.SearchAsync(new SearchQuery { Text = text }, default)).Items.Select(i => i.Title).OrderBy(t => t).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task TickedProposals_AreWritten_ToTheFileTheDatabaseAndTheSearchIndex_AndTheCoverSurvives()
    {
        await using var e = new Env();
        await e.IndexAsync(title: "unknown track", artist: null, album: null);
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var proposals = IdentificationProposalBuilder.Build(track, Candidate, Release);
        Assert.Contains(proposals, p => p.Field == TagField.Title && !p.Selected);          // the file's own title differs: not ticked
        var ticked = proposals.Select(p => p.Field == TagField.Title ? p with { Selected = true } : p).ToList();   // the user ticks it
        var changes = IdentificationProposalBuilder.ToChangeSet(ticked);

        var plan = await e.Service.PreviewAsync([e.TrackId], changes);
        Assert.Empty(plan.Errors);
        Assert.Contains(plan.Changes, c => c.Field == TagField.Title && c.Before == "unknown track" && c.After == "Ξανά στη ζωή μου");
        var hashBefore = Hash(e.Path_);
        await e.Service.PreviewAsync([e.TrackId], changes);
        Assert.Equal(hashBefore, Hash(e.Path_));

        var summary = await e.Service.ApplyAsync([e.TrackId], changes, new TagWriteOptions(), null, default);

        Assert.Equal((1, 0), (summary.Written, summary.Failed));
        using (var f = TagLib.File.Create(e.Path_))
        {
            Assert.Equal("Ξανά στη ζωή μου", f.Tag.Title);
            Assert.Equal(["Χάρις Αλεξίου"], f.Tag.Performers);
            Assert.Equal("Το Τραγούδι", f.Tag.Album);
            Assert.Equal(1985u, f.Tag.Year);
            Assert.Equal(5u, f.Tag.Track);
            Assert.Equal(Rec, f.Tag.MusicBrainzTrackId);
            Assert.Equal(["Disco"], f.Tag.Genres);                                            // the community-tag suggestion was never ticked
            Assert.Equal(0u, f.Tag.BeatsPerMinute);                                           // nothing invented
            Assert.Single(f.Tag.Pictures);
        }
        var row = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        Assert.Equal(("Ξανά στη ζωή μου", "Το Τραγούδι", Rec), (row.Title, row.Album, row.MusicBrainzRecordingId));
        Assert.Equal(track.ArtworkCacheKey, row.ArtworkCacheKey);
        Assert.Equal(["Ξανά στη ζωή μου"], await e.TitlesFor("ξανα"));                        // the search index follows (the old title is no longer what it is found by)
    }

    [Fact]
    public async Task UntickedProposals_ChangeNothing()
    {
        await using var e = new Env();
        await e.IndexAsync(title: "My own title", artist: "My Artist", album: "My Album");
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var proposals = IdentificationProposalBuilder.Build(track, Candidate, Release);
        // Only fields that are empty in the file (and the recording id) start ticked: whatever would replace a value stays as it is.
        Assert.Equal([TagField.AlbumArtist, TagField.Year, TagField.TrackNumber, TagField.MusicBrainzRecordingId],
            proposals.Where(p => p.Selected).Select(p => p.Field).OrderBy(x => (int)x).ToArray());

        var summary = await e.Service.ApplyAsync([e.TrackId], IdentificationProposalBuilder.ToChangeSet(proposals), new TagWriteOptions(), null, default);

        Assert.Equal(1, summary.Written);
        using var f = TagLib.File.Create(e.Path_);
        Assert.Equal(("My own title", "My Album"), (f.Tag.Title, f.Tag.Album));
        Assert.Equal(["My Artist"], f.Tag.Performers);
        Assert.Equal(Rec, f.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_LeavesTheDatabaseAndTheFileAlone()
    {
        await using var e = new Env();
        await e.IndexAsync(title: "unknown track", artist: null, album: null);
        var before = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var hash = Hash(e.Path_);
        File.SetAttributes(e.Path_, FileAttributes.ReadOnly);
        try
        {
            var changes = IdentificationProposalBuilder.ToChangeSet(IdentificationProposalBuilder.Build(before, Candidate, Release));

            var summary = await e.Service.ApplyAsync([e.TrackId], changes, new TagWriteOptions(), null, default);

            Assert.Equal((0, 1), (summary.Written, summary.Failed));
            var after = (await e.Db.Tracks.GetAsync(e.TrackId))!;
            Assert.Equal((before.Title, before.Album, before.MusicBrainzRecordingId), (after.Title, after.Album, after.MusicBrainzRecordingId));
            Assert.Equal(hash, Hash(e.Path_));
            Assert.Equal(["unknown track"], await e.TitlesFor("unknown"));
        }
        finally { File.SetAttributes(e.Path_, FileAttributes.Normal); }
    }

    [Fact]
    public async Task AnIdFromTheService_ThatIsNotAUuid_IsRejectedBeforeAnythingIsWritten()
    {
        await using var e = new Env();
        await e.IndexAsync(title: "unknown track", artist: null, album: null);
        var hash = Hash(e.Path_);
        var bad = Candidate with { RecordingId = "not-a-uuid" };
        var changes = IdentificationProposalBuilder.ToChangeSet(IdentificationProposalBuilder.Build((await e.Db.Tracks.GetAsync(e.TrackId))!, bad, null));

        var plan = await e.Service.PreviewAsync([e.TrackId], changes);

        Assert.Contains(plan.Errors, m => m.Contains("MusicBrainz recording id"));
        Assert.Equal(hash, Hash(e.Path_));
    }
}
