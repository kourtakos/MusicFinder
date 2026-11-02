using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>The whole edit path on real files: preview, write, verify, database, search index, genre mapping, rescan.</summary>
public class TagEditingServiceTests
{
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
        public TagEditingService Service { get; }
        public SqliteSearchService Search { get; }
        public long RootId { get; private set; }
        public Dictionary<string, long> Ids { get; } = [];

        public Env()
        {
            Music = Path.Combine(Db.Dir, "Music");
            Directory.CreateDirectory(Music);
            Service = new TagEditingService(Db.Tracks, new TagLibMetadataWriter(), Db.GenreJob);
            Search = new SqliteSearchService(Db.Factory);
        }

        public string File_(string name) => Path.Combine(Music, name);

        /// <summary>Creates mp3 files with some tags and indexes them with the real scanner.</summary>
        public async Task IndexAsync(params (string Name, string Title, string Genre)[] files)
        {
            await Db.SeedTaxonomyAsync();
            foreach (var (name, title, genre) in files)
            {
                var path = Fixtures.MakeMp3(File_(name));
                using var f = TagLib.File.Create(path);
                f.Tag.Title = title;
                f.Tag.Performers = ["Original Artist"];
                f.Tag.Genres = [genre];
                f.Tag.Album = "Original Album";
                f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(ArtworkTests.TinyPng)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/png" }];
                f.Save();
            }
            var root = await Db.Roots.AddAsync(Music);
            RootId = root.Id;
            await Scanner().ScanRootAsync(root.Id, null, default);
            await Db.GenreJob.RunAsync();
            foreach (var file in await Db.Tracks.GetIndexedFilesAsync(root.Id))
                Ids[Path.GetFileName(file.FullPath)] = file.Id;
        }

        public LibraryScanner Scanner() => new(Db.Roots, Db.Folders, Db.Tracks, Reader, NullLogger<LibraryScanner>.Instance);

        public async Task<List<string>> TitlesFor(string text) =>
            (await Search.SearchAsync(new SearchQuery { Text = text }, default)).Items.Select(i => i.Title).OrderBy(t => t).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static TagChangeSet Set(params (TagField Field, string? Value)[] edits) =>
        new(edits.ToDictionary(e => e.Field, e => e.Value));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Apply_UpdatesFileDatabaseSearchIndexAndKeepsTheCover()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Old Title", "Disco"));
        var id = e.Ids["a.mp3"];
        var before = (await e.Db.Tracks.GetAsync(id))!;
        Assert.Equal(["Old Title"], await e.TitlesFor("old"));

        var summary = await e.Service.ApplyAsync([id], Set((TagField.Title, "Ξανά στη ζωή μου"), (TagField.Genre, "Nu-Disco"), (TagField.Year, "1985")),
            new TagWriteOptions(), null, default);

        Assert.Equal((1, 0, 0), (summary.Written, summary.Unchanged, summary.Failed));
        var row = (await e.Db.Tracks.GetAsync(id))!;
        Assert.Equal("Ξανά στη ζωή μου", row.Title);
        Assert.Equal("Nu-Disco", row.GenreRaw);
        Assert.Equal(1985, row.Year);
        Assert.Equal("Original Album", row.Album);                    // untouched field
        Assert.Equal(before.ArtworkCacheKey, row.ArtworkCacheKey);    // cover survived and its key is unchanged
        Assert.NotNull(row.ArtworkCacheKey);

        // The search index follows: new text found (accent-insensitively), old text gone.
        Assert.Equal(["Ξανά στη ζωή μου"], await e.TitlesFor("ξανα"));
        Assert.Empty(await e.TitlesFor("old"));

        // Genre mapping was recomputed from the new raw text.
        var genres = (await e.Db.Genres.GetAllAsync()).ToDictionary(g => g.Id, g => g.Name);
        Assert.Equal(["Nu Disco"], (await e.Db.TrackGenres.GetForTracksAsync([id])).Select(a => genres[a.GenreId]));

        // The stored size/time match the file on disk, so the next scan skips it instead of re-reading.
        var info = new FileInfo(e.File_("a.mp3"));
        Assert.Equal(info.Length, row.FileSize);
        Assert.Equal(info.LastWriteTimeUtc, row.ModifiedUtc);
        var readsBefore = e.Reader.Reads;
        await e.Scanner().ScanRootAsync(e.RootId, null, default);
        Assert.Equal(readsBefore, e.Reader.Reads);
        Assert.Equal("Ξανά στη ζωή μου", (await e.Db.Tracks.GetAsync(id))!.Title);
    }

    [Fact]
    public async Task Apply_RaisesTracksChanged_OnlyForFilesThatWereWritten()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "A", "Disco"), ("b.mp3", "B", "Disco"));
        File.SetAttributes(e.File_("b.mp3"), FileAttributes.ReadOnly);
        var raised = new List<long>();
        e.Service.TracksChanged += (_, ids) => raised.AddRange(ids);

        var summary = await e.Service.ApplyAsync([e.Ids["a.mp3"], e.Ids["b.mp3"]], Set((TagField.Album, "New")), new TagWriteOptions(), null, default);

        Assert.Equal((1, 1), (summary.Written, summary.Failed));
        Assert.Equal([e.Ids["a.mp3"]], raised);
        File.SetAttributes(e.File_("b.mp3"), FileAttributes.Normal);
    }

    [Fact]
    public async Task AFailedFile_LeavesItsRowAndFileUntouched_WhileOthersSucceed()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"), ("b.mp3", "Bravo", "Disco"), ("c.mp3", "Charlie", "Disco"));
        File.SetAttributes(e.File_("b.mp3"), FileAttributes.ReadOnly);
        var bHash = Hash(e.File_("b.mp3"));

        var summary = await e.Service.ApplyAsync(e.Ids.Values.ToList(), Set((TagField.Album, "Shared Album")), new TagWriteOptions(), null, default);

        Assert.Equal((2, 1), (summary.Written, summary.Failed));
        var failed = summary.Files.Single(f => f.Outcome == TagEditOutcome.Failed);
        Assert.EndsWith("b.mp3", failed.Path);
        Assert.Contains("read-only", failed.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("Original Album", (await e.Db.Tracks.GetAsync(e.Ids["b.mp3"]))!.Album); // the database never claims success
        Assert.Equal(bHash, Hash(e.File_("b.mp3")));                                          // and the file is byte-identical
        Assert.Equal("Shared Album", (await e.Db.Tracks.GetAsync(e.Ids["a.mp3"]))!.Album);
        Assert.Equal("Shared Album", (await e.Db.Tracks.GetAsync(e.Ids["c.mp3"]))!.Album);
        File.SetAttributes(e.File_("b.mp3"), FileAttributes.Normal);

        // Recovery is just a rescan: nothing is stuck.
        await e.Scanner().ScanRootAsync(e.RootId, null, default);
        Assert.Equal("Original Album", (await e.Db.Tracks.GetAsync(e.Ids["b.mp3"]))!.Album);
    }

    [Fact]
    public async Task FilesThatAlreadyMatch_AreSkipped_AndNeverRewritten()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Same", "Disco"));
        var hash = Hash(e.File_("a.mp3"));
        var stamp = File.GetLastWriteTimeUtc(e.File_("a.mp3"));

        var summary = await e.Service.ApplyAsync([e.Ids["a.mp3"]], Set((TagField.Title, "Same"), (TagField.Album, " Original Album ")),
            new TagWriteOptions(), null, default);

        Assert.Equal((0, 1, 0), (summary.Written, summary.Unchanged, summary.Failed));
        Assert.Equal(hash, Hash(e.File_("a.mp3")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(e.File_("a.mp3")));
    }

    [Fact]
    public async Task Preview_ListsOnlyRealChanges_AndWritesNothing()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"), ("b.mp3", "Bravo", "Funk"));
        var hashes = e.Ids.Keys.ToDictionary(k => k, k => Hash(e.File_(k)));

        var plan = await e.Service.PreviewAsync(e.Ids.Values.ToList(),
            Set((TagField.Title, "Alpha"), (TagField.Genre, "Disco"), (TagField.Album, "Greatest Hits")));

        Assert.Empty(plan.Errors);
        // Title/Genre already match on a.mp3; Album changes for both; Genre changes on b.mp3 only.
        Assert.Equal(2, plan.TracksWithChanges);
        var a = plan.Changes.Where(c => c.FileName == "a.mp3").ToList();
        var b = plan.Changes.Where(c => c.FileName == "b.mp3").ToList();
        Assert.Equal([TagField.Album], a.Select(c => c.Field));
        Assert.Equal(("Original Album", "Greatest Hits"), (a[0].Before, a[0].After));
        Assert.Contains(b, c => c is { Field: TagField.Genre, Before: "Funk", After: "Disco" });
        Assert.Contains(b, c => c is { Field: TagField.Title, Before: "Bravo", After: "Alpha" });

        foreach (var (name, hash) in hashes) Assert.Equal(hash, Hash(e.File_(name))); // a preview never touches files
    }

    [Fact]
    public async Task Preview_ReportsValidationErrors_WithoutDiffRows()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"));

        var plan = await e.Service.PreviewAsync([e.Ids["a.mp3"]], Set((TagField.Year, "1700"), (TagField.Bpm, "999")));

        Assert.Equal(2, plan.Errors.Count);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task Apply_RejectsInvalidValues_BeforeTouchingAnything()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"));
        var hash = Hash(e.File_("a.mp3"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            e.Service.ApplyAsync([e.Ids["a.mp3"]], Set((TagField.Year, "1700")), new TagWriteOptions(), null, default));
        Assert.Equal(hash, Hash(e.File_("a.mp3")));
    }

    [Fact]
    public async Task BulkEdit_SetsOneFieldOnManyTracks_AndKeepsTheirOtherValues()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"), ("b.mp3", "Bravo", "Funk"), ("c.mp3", "Charlie", "Jazz"));
        var progress = new List<TagEditProgress>();

        var summary = await e.Service.ApplyAsync(e.Ids.Values.ToList(), Set((TagField.Artist, "Various Artists"), (TagField.Year, "2001")),
            new TagWriteOptions(), new Progress<TagEditProgress>(progress.Add), default);
        await Task.Delay(100); // Progress<T> posts to the thread pool

        Assert.Equal(3, summary.Written);
        var titles = new Dictionary<string, string?>();
        foreach (var (name, id) in e.Ids)
        {
            var row = (await e.Db.Tracks.GetAsync(id))!;
            Assert.Equal("Various Artists", row.Artist);
            Assert.Equal(2001, row.Year);
            titles[name] = row.Title;
        }
        Assert.Equal(("Alpha", "Bravo", "Charlie"), (titles["a.mp3"], titles["b.mp3"], titles["c.mp3"]));
        Assert.Equal(3, progress.Max(p => p.Done));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], await e.TitlesFor("various"));
    }

    [Fact]
    public async Task Cancellation_StopsBetweenFiles_AndKeepsFinishedFilesConsistent()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"), ("b.mp3", "Bravo", "Disco"), ("c.mp3", "Charlie", "Disco"));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Done == 1) cts.Cancel(); }); // after the first file is done

        var summary = await e.Service.ApplyAsync(e.Ids.Values.ToList(), Set((TagField.Album, "Cancelled Album")), new TagWriteOptions(), progress, cts.Token);

        Assert.True(summary.Cancelled);
        Assert.Equal(1, summary.Written);
        var albums = new List<string?>();
        foreach (var id in e.Ids.Values.OrderBy(i => i)) albums.Add((await e.Db.Tracks.GetAsync(id))!.Album);
        Assert.Equal(1, albums.Count(a => a == "Cancelled Album"));
        Assert.Equal(2, albums.Count(a => a == "Original Album"));

        // Files and database agree for every track: a rescan finds nothing to fix.
        var reads = e.Reader.Reads;
        await e.Scanner().ScanRootAsync(e.RootId, null, default);
        Assert.Equal(reads, e.Reader.Reads);
    }

    [Fact]
    public async Task MissingFile_IsReportedAsFailed_NotThrown()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"));
        File.Delete(e.File_("a.mp3"));

        var summary = await e.Service.ApplyAsync([e.Ids["a.mp3"]], Set((TagField.Album, "x")), new TagWriteOptions(), null, default);

        Assert.Equal(1, summary.Failed);
        Assert.Equal("Original Album", (await e.Db.Tracks.GetAsync(e.Ids["a.mp3"]))!.Album);
    }

    [Fact]
    public async Task Backup_IsCreatedPerFile_WhenRequested()
    {
        await using var e = new Env();
        await e.IndexAsync(("a.mp3", "Alpha", "Disco"));
        var original = Hash(e.File_("a.mp3"));
        var backups = Path.Combine(e.Db.Dir, "TagBackups");

        var summary = await e.Service.ApplyAsync([e.Ids["a.mp3"]], Set((TagField.Album, "x")), new TagWriteOptions(true, backups), null, default);

        var backup = summary.Files.Single().BackupPath;
        Assert.NotNull(backup);
        Assert.Equal(original, Hash(backup!));
    }

    private sealed class SyncProgress(Action<TagEditProgress> action) : IProgress<TagEditProgress>
    {
        public void Report(TagEditProgress value) => action(value);
    }
}
