using System.Text;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Dj;
using Env = MusicFinder.IntegrationTests.HistoryImportTests.Env;

namespace MusicFinder.IntegrationTests;

/// <summary>The whole import: VirtualDJ history files in a folder -> matched tracks -> confirmed plays, run repeatedly like the app does.</summary>
public class HistoryImportServiceTests
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("GTB Standard Time");
    private static readonly DateTime T0 = new(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IAsyncDisposable
    {
        public Env Env { get; }
        public string History { get; } = Path.Combine(Path.GetTempPath(), "mf-vdj-" + Guid.NewGuid().ToString("N"));
        public string Docs { get; } = Path.Combine(Path.GetTempPath(), "mf-docs-" + Guid.NewGuid().ToString("N"));
        public HistoryImportTests.FakeSettings Settings { get; } = new();
        public HistoryImportService Service { get; }
        public List<IReadOnlyList<HistoryImportResult>> Completed { get; } = [];

        private Rig(Env env)
        {
            Env = env;
            Directory.CreateDirectory(History);
            Settings.Value.VirtualDjHistoryFolder = History;
            var importers = new IHistoryImporter[] { new VirtualDjHistoryImporter(Athens, Path.Combine(Docs, "local"), Docs), new TraktorHistoryImporter(Docs) };
            Service = new HistoryImportService(importers, env.Matcher, env.Db.HistoryImport, env.Service, Settings);
            Service.ImportCompleted += (_, r) => Completed.Add(r);
        }

        public static async Task<Rig> CreateAsync() => new(await Env.CreateAsync());

        public string DayFile(string day = "2026-11-03") => Path.Combine(History, day + ".m3u");

        public void Write(string text, string day = "2026-11-03") => File.WriteAllText(DayFile(day), text, new UTF8Encoding(false));

        public async Task<HistoryImportResult> ImportAsync() => Assert.Single(await Service.RunAsync(PlaySources.VirtualDj));

        public async ValueTask DisposeAsync()
        {
            await Env.DisposeAsync();
            foreach (var dir in new[] { History, Docs })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { /* temp folder, best effort */ }
        }
    }

    private static string Entry(DateTime utc, string path, long size, string artist = "Artist", string title = "Title") =>
        $"#EXTVDJ:<time>{TimeZoneInfo.ConvertTimeFromUtc(utc, Athens):HH:mm}</time><lastplaytime>{new DateTimeOffset(utc).ToUnixTimeSeconds()}</lastplaytime>" +
        $"<filesize>{size}</filesize><artist>{artist}</artist><title>{title}</title><songlength>120.047</songlength>\r\n{path}\r\n";

    private const string A = @"C:\MF-Fixtures\Booze\a.mp3";
    private const string B = @"C:\MF-Fixtures\Booze\b.mp3";
    private const string Unknown = @"D:\Greek\Βανδαλούπ.mp3";

    [Fact]
    public async Task FirstImport_ConfirmsKnownTracks_AndKeepsTheRestForReview()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Write(Entry(T0.AddMinutes(1), A, 4495565) + Entry(T0.AddMinutes(5), B, 11364900) + Entry(T0.AddMinutes(9), Unknown, 4496503));

        var result = await rig.ImportAsync();

        Assert.Null(result.Error);
        Assert.Equal(1, result.Files);
        Assert.Equal(3, result.Entries);
        Assert.Equal(2, result.New);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal((1, 0), rig.Env.Counts(rig.Env.A));
        Assert.Equal((1, 0), rig.Env.Counts(rig.Env.B));
        var pending = Assert.Single(await rig.Env.Db.HistoryImport.GetUnmatchedAsync(UnmatchedStatus.Pending));
        Assert.Equal(Unknown, pending.SourcePath);
        Assert.Equal(HistoryUnmatchedReason.NotInLibrary, pending.Reason);
    }

    [Fact]
    public async Task ImportingAgain_ReadsNothingAndCountsNothingTwice()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Write(Entry(T0.AddMinutes(1), A, 4495565) + Entry(T0.AddMinutes(9), Unknown, 4496503));
        await rig.ImportAsync();

        var again = await rig.ImportAsync();

        Assert.Equal(0, again.Files);                                    // the file did not change, so it was not even opened
        Assert.Equal(0, again.Entries);
        Assert.Equal((1, 0), rig.Env.Counts(rig.Env.A));
        Assert.Equal(1, await rig.Env.Db.HistoryImport.CountPendingAsync());
    }

    [Fact]
    public async Task ARewrittenFile_OnlyAddsTheNewPlays()
    {
        await using var rig = await Rig.CreateAsync();
        var first = Entry(T0.AddMinutes(1), A, 4495565);
        rig.Write(first);
        await rig.ImportAsync();

        rig.Write(first + Entry(T0.AddMinutes(8), A, 4495565));          // VirtualDJ added the same track again, later
        var result = await rig.ImportAsync();

        Assert.Equal(1, result.Files);
        Assert.Equal(1, result.New);
        Assert.Equal(1, result.Duplicates);                              // the first play is recognised, not counted again
        Assert.Equal((2, 0), rig.Env.Counts(rig.Env.A));
    }

    [Fact]
    public async Task ADragGuess_IsUpgradedByTheImport()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Env.Service.StartAsync("VirtualDJ", default);
        await rig.Env.DragAsync(rig.Env.A);
        await rig.Env.AdvanceAsync(25);
        Assert.Equal((0, 1), rig.Env.Counts(rig.Env.A));
        rig.Write(Entry(T0.AddMinutes(3), A, 4495565));

        var result = await rig.ImportAsync();

        Assert.Equal(1, result.Upgraded);
        Assert.Equal(0, result.New);
        Assert.Equal((1, 0), rig.Env.Counts(rig.Env.A));
    }

    [Fact]
    public async Task AFileStillBeingWritten_IsReadAgainUntilItIsComplete()
    {
        await using var rig = await Rig.CreateAsync();
        var done = Entry(T0.AddMinutes(1), A, 4495565);
        var half = "#EXTVDJ:<time>21:08</time><lastplaytime>" + new DateTimeOffset(T0.AddMinutes(8)).ToUnixTimeSeconds() + "</lastplaytime><artist>X</artist><title>Y</title>\r\n";
        rig.Write(done + half);

        var first = await rig.ImportAsync();
        Assert.Equal(1, first.Entries);

        rig.Write(done + half + B + "\r\n");                             // the path line arrives
        var second = await rig.ImportAsync();

        Assert.Equal(1, second.Files);                                   // read again, because the first pass did not finish it
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(2, second.Entries);
    }

    [Fact]
    public async Task APlayWithoutATrack_IsLinkedWhenTheFileIsIndexedLater()
    {
        await using var rig = await Rig.CreateAsync();
        var path = @"C:\MF-Fixtures\Late\late.mp3";
        rig.Write(Entry(T0.AddMinutes(1), path, 777));
        var first = await rig.ImportAsync();
        Assert.Equal(1, first.Unmatched);

        var root = (await rig.Env.Db.Roots.GetAllAsync()).Single();
        var folder = await rig.Env.Db.Folders.UpsertAsync(root.Id, null, @"C:\MF-Fixtures\Late", "Late");
        var track = TestDb.MakeTrack(folder, path, "Late", "Artist");
        track.FileSize = 777;
        await rig.Env.Db.Tracks.UpsertBatchAsync([track]);
        var id = (await rig.Env.Db.Tracks.GetIndexedFilesAsync(root.Id)).Single(f => f.FullPath == path).Id;

        var second = await rig.ImportAsync();

        Assert.Equal(1, second.New);                                      // the earlier play found its track now
        Assert.Equal((1, 0), rig.Env.Counts(id));
        Assert.Equal(0, await rig.Env.Db.HistoryImport.CountPendingAsync());
    }

    [Fact]
    public async Task LinkAndIgnore_ResolveWaitingPlays()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Write(Entry(T0.AddMinutes(1), @"D:\x\one.mp3", 1) + Entry(T0.AddMinutes(2), @"D:\x\two.mp3", 2));
        await rig.ImportAsync();
        var waiting = await rig.Env.Db.HistoryImport.GetUnmatchedAsync(UnmatchedStatus.Pending);

        var outcome = await rig.Service.LinkAsync(waiting[0].Id, rig.Env.B);
        await rig.Service.IgnoreAsync(waiting[1].Id);

        Assert.Equal(ImportedPlayOutcome.Added, outcome);
        Assert.Equal((1, 0), rig.Env.Counts(rig.Env.B));
        Assert.Equal(0, await rig.Env.Db.HistoryImport.CountPendingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.LinkAsync(waiting[0].Id, rig.Env.B));
    }

    [Fact]
    public async Task TheFolderTheUserChose_WinsOverTheDetectedOne_AndAMissingFolderIsReported()
    {
        await using var rig = await Rig.CreateAsync();
        var detected = Path.Combine(rig.Docs, "local", "VirtualDJ", "History");
        Directory.CreateDirectory(detected);
        File.WriteAllText(Path.Combine(detected, "2026-11-03.m3u"), Entry(T0.AddMinutes(1), B, 11364900));
        rig.Settings.Value.VirtualDjHistoryFolder = null;
        Assert.Equal(detected, rig.Service.ResolveFolder(PlaySources.VirtualDj));          // nothing chosen: the detected folder

        rig.Settings.Value.VirtualDjHistoryFolder = rig.History;
        Assert.Equal(rig.History, rig.Service.ResolveFolder(PlaySources.VirtualDj));       // chosen: that one, even if it is empty
        Assert.Equal(0, (await rig.ImportAsync()).Entries);

        rig.Settings.Value.VirtualDjHistoryFolder = Path.Combine(rig.History, "gone");
        var missing = await rig.ImportAsync();
        Assert.Contains("does not exist", missing.Error);
        Assert.Equal((0, 0), rig.Env.Counts(rig.Env.B));
    }

    [Fact]
    public async Task NoFolderAtAll_AsksTheUserToChooseOne()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Settings.Value.VirtualDjHistoryFolder = null;

        var result = await rig.ImportAsync();

        Assert.Contains("Choose it in Settings", result.Error);
    }

    [Fact]
    public async Task ImportCompleted_IsRaised_AndTraktorSaysItIsNotReadableYet()
    {
        await using var rig = await Rig.CreateAsync();
        var traktorHistory = Path.Combine(rig.Docs, "Native Instruments", "Traktor Pro 4", "History");
        Directory.CreateDirectory(traktorHistory);
        rig.Settings.Value.TraktorHistoryFolder = null;
        rig.Write(Entry(T0.AddMinutes(1), A, 4495565));

        var all = await rig.Service.RunAsync(null);

        Assert.Equal(2, all.Count);
        Assert.Single(rig.Completed);
        var traktor = all.Single(r => r.ProviderId == PlaySources.Traktor);
        Assert.Equal(traktorHistory, traktor.Folder);
        Assert.Contains("not available yet", traktor.Error);
        Assert.Equal(HistorySyncMode.AfterClose, rig.Service.Importers.Single(i => i.ProviderId == PlaySources.Traktor).SyncMode);
        Assert.Null(all.Single(r => r.ProviderId == PlaySources.VirtualDj).Error);       // one program failing does not stop the other
    }

    [Fact]
    public async Task FilesThatAreNotDailyHistory_AreIgnored_AndAnUnreadableOneDoesNotStopTheRest()
    {
        await using var rig = await Rig.CreateAsync();
        File.WriteAllText(Path.Combine(rig.History, "playlist.m3u"), Entry(T0, A, 4495565));
        rig.Write(Entry(T0.AddMinutes(2), B, 11364900), "2026-11-02");
        using var locked = new FileStream(rig.DayFile("2026-11-03"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);   // another program holds it

        var result = await rig.ImportAsync();

        Assert.Null(result.Error);
        Assert.Equal(1, result.New);                                      // yesterday's file was read
        Assert.Contains(result.Warnings, w => w.Contains("2026-11-03") && w.Contains("could not be read"));
        Assert.Equal((0, 0), rig.Env.Counts(rig.Env.A));                  // playlist.m3u is not a history file
    }
}
