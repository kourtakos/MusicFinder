using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Analysis;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;

namespace MusicFinder.IntegrationTests;

/// <summary>An estimate turned into tags on a real generated file, through the one safe write path.</summary>
public class AnalysisApplyTests
{
    private static readonly TrackAnalysis Estimate = new(
        1, 1, new BpmEstimate(123.5, 0.8, Confidence.High, [], ""), new KeyEstimate("8A", 0.7, Confidence.High, "8B", "", 0), 90, "test", null, DateTime.UtcNow);

    /// <summary>What the user gets by ticking every proposal (keys are never ticked for them, so the tests tick them).</summary>
    private static TagChangeSet Ticked(AnalysisProposals p) => AnalysisProposalBuilder.ToChangeSet(p.All.Select(x => x with { Selected = true }));

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

        public async Task IndexAsync(uint bpm = 0, string? key = null, bool withCover = true)
        {
            await Db.SeedTaxonomyAsync();
            FilePath = Fixtures.MakeMp3(Path.Combine(Music, "a.mp3"));
            using (var f = TagLib.File.Create(FilePath))
            {
                f.Tag.Title = "Ξανά στη ζωή μου";
                f.Tag.Comment = "keep this comment";
                f.Tag.BeatsPerMinute = bpm;
                if (key is not null) f.Tag.InitialKey = key;
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
    public async Task TheTickedEstimates_AreWritten_AsABpmAndAKeyTag_AndTheLibraryFollows()
    {
        await using var e = new Env();
        await e.IndexAsync();
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var coverBefore = track.ArtworkCacheKey;
        var proposals = AnalysisProposalBuilder.Build(track, Estimate, KeyNotation.Camelot, allowOverwrite: false);

        var summary = await e.Service.ApplyAsync([e.TrackId], Ticked(proposals), new TagWriteOptions(), null, default);

        Assert.Equal(1, summary.Written);
        using (var f = TagLib.File.Create(e.FilePath))
        {
            Assert.Equal(124u, f.Tag.BeatsPerMinute);
            Assert.Equal("8A", f.Tag.InitialKey);
            Assert.Equal("keep this comment", f.Tag.Comment);
            Assert.Single(f.Tag.Pictures);
        }
        var row = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        Assert.Equal((124.0, "8A"), (row.Bpm, row.MusicalKey));
        Assert.Equal(coverBefore, row.ArtworkCacheKey);
    }

    [Theory]
    [InlineData(KeyNotation.OpenKey, "1m")]
    [InlineData(KeyNotation.NoteName, "Am")]
    public async Task TheKeyIsWrittenInTheChosenNotation(KeyNotation notation, string expected)
    {
        await using var e = new Env();
        await e.IndexAsync();
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var proposals = AnalysisProposalBuilder.Build(track, Estimate, notation, false);

        await e.Service.ApplyAsync([e.TrackId], Ticked(proposals), new TagWriteOptions(), null, default);

        using var f = TagLib.File.Create(e.FilePath);
        Assert.Equal(expected, f.Tag.InitialKey);
    }

    [Fact]
    public async Task ExistingValues_AreNotTouched_UnlessOverwritingIsAllowed()
    {
        await using var e = new Env();
        await e.IndexAsync(bpm: 100, key: "5B");
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;

        var blocked = AnalysisProposalBuilder.Build(track, Estimate, KeyNotation.Camelot, allowOverwrite: false);
        var summary = await e.Service.ApplyAsync([e.TrackId], AnalysisProposalBuilder.ToChangeSet(blocked.All), new TagWriteOptions(), null, default);
        Assert.Equal(0, summary.Written);                                          // nothing ticked: nothing to write
        using (var f = TagLib.File.Create(e.FilePath)) Assert.Equal((100u, "5B"), (f.Tag.BeatsPerMinute, f.Tag.InitialKey));

        var allowed = AnalysisProposalBuilder.Build(track, Estimate, KeyNotation.Camelot, allowOverwrite: true);
        var set = AnalysisProposalBuilder.ToChangeSet(allowed.All.Select(p => p with { Selected = true }));
        await e.Service.ApplyAsync([e.TrackId], set, new TagWriteOptions(), null, default);
        using var after = TagLib.File.Create(e.FilePath);
        Assert.Equal((124u, "8A"), (after.Tag.BeatsPerMinute, after.Tag.InitialKey));
    }

    [Fact]
    public async Task APreview_ShowsTheEstimatesAsTheyWillBeStored_AndWritesNothing()
    {
        await using var e = new Env();
        await e.IndexAsync();
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e.FilePath)));
        var proposals = AnalysisProposalBuilder.Build(track, Estimate, KeyNotation.Camelot, false);

        var plan = await e.Service.PreviewAsync([e.TrackId], Ticked(proposals));

        Assert.Equal([(TagField.Bpm, (string?)null, "124"), (TagField.Key, null, "8A")], plan.Changes.Select(c => (c.Field, c.Before, c.After)));
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(e.FilePath))));
    }

    [Fact]
    public async Task AReadOnlyFile_FailsCleanly_AndTheLibraryKeepsItsOldValues()
    {
        await using var e = new Env();
        await e.IndexAsync();
        var track = (await e.Db.Tracks.GetAsync(e.TrackId))!;
        File.SetAttributes(e.FilePath, FileAttributes.ReadOnly);
        try
        {
            var summary = await e.Service.ApplyAsync([e.TrackId],
                Ticked(AnalysisProposalBuilder.Build(track, Estimate, KeyNotation.Camelot, false)), new TagWriteOptions(), null, default);

            Assert.Equal(1, summary.Failed);
            var row = (await e.Db.Tracks.GetAsync(e.TrackId))!;
            Assert.Null(row.MusicalKey);
        }
        finally { File.SetAttributes(e.FilePath, FileAttributes.Normal); }
    }
}
