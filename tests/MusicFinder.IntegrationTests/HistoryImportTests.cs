using Dapper;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// M4 on a real database with a fake clock: how a play reported by VirtualDJ is tied to a library track, and how it replaces the drag-based guess
/// without ever counting twice.
/// </summary>
public class HistoryImportTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSettings : ISettingsStore
    {
        public AppSettings Value { get; } = new();
        public AppSettings Load() => Value;
        public void Save(AppSettings settings) { }
    }

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(T0));
        public DjSessionService Service { get; }
        public HistoryTrackMatcher Matcher { get; }
        public long A { get; private set; }      // C:\MF-Fixtures\Booze\a.mp3, 4 495 565 bytes, 120.047 s
        public long B { get; private set; }      // C:\MF-Fixtures\Booze\b.mp3
        public long D1 { get; private set; }     // two files named same.mp3 of the same size in different folders
        public long D2 { get; private set; }

        public Env()
        {
            Service = new DjSessionService(Db.Sessions, Db.Events, Db.Plays, new FakeSettings(), Clock);
            Matcher = new HistoryTrackMatcher(Db.HistoryImport);
        }

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\MF-Fixtures");
            var booze = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\MF-Fixtures\Booze", "Booze");
            var other = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\MF-Fixtures\Other", "Other");

            Track Make(long folder, string path, long size, long durationMs)
            {
                var t = TestDb.MakeTrack(folder, path, Path.GetFileNameWithoutExtension(path), "Artist");
                t.FileSize = size;
                t.DurationMs = durationMs;
                return t;
            }

            await e.Db.Tracks.UpsertBatchAsync(
            [
                Make(booze, @"C:\MF-Fixtures\Booze\a.mp3", 4495565, 120047),
                Make(booze, @"C:\MF-Fixtures\Booze\b.mp3", 11364900, 352850),
                Make(booze, @"C:\MF-Fixtures\Booze\same.mp3", 5000, 90000),
                Make(other, @"C:\MF-Fixtures\Other\same.mp3", 5000, 90000),
            ]);
            var files = await e.Db.Tracks.GetIndexedFilesAsync(root.Id);
            e.A = files.Single(f => f.FullPath.EndsWith(@"Booze\a.mp3")).Id;
            e.B = files.Single(f => f.FullPath.EndsWith(@"Booze\b.mp3")).Id;
            e.D1 = files.Single(f => f.FullPath.EndsWith(@"Booze\same.mp3")).Id;
            e.D2 = files.Single(f => f.FullPath.EndsWith(@"Other\same.mp3")).Id;
            return e;
        }

        public Task DragAsync(long track)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            return Service.RecordDragAsync(new DragRecord([track], now.AddSeconds(-1.5), now, "Copy", "virtualdj"), default);
        }

        public async Task AdvanceAsync(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            await Service.WaitForIdleAsync();
        }

        public Task<ImportedPlayOutcome> ImportAsync(long track, ImportedPlayEvent play) =>
            Service.RecordImportedPlayAsync(play, track, HistoryMatchMethod.Path, 1.0);

        public async Task<List<PlayHistoryItem>> PlaysAsync(long track) => (await Db.Plays.GetForTrackAsync(track, 50)).ToList();

        public (int Confirmed, int Possible) Counts(long track)
        {
            using var conn = Db.Factory.Open();
            return conn.QuerySingleOrDefault<(int, int)>("SELECT ConfirmedCount, PossibleCount FROM TrackPlayStats WHERE TrackId = @track", new { track });
        }

        public async Task<List<string>> EventTypesAsync(long track) =>
            (await Db.Events.GetForTrackAsync(track, 50)).OrderBy(x => x.Id).Select(x => x.EventType).ToList();

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private static ImportedPlayEvent Play(DateTime utc, string path = @"C:\MF-Fixtures\Booze\a.mp3", long? size = 4495565, double? seconds = 120.047, string? key = null) =>
        new("VirtualDJ", key ?? VirtualDjHistoryParser.EventKey(new DateTimeOffset(utc).ToUnixTimeSeconds(), path), utc, path, "Artist", "Title", null, size, seconds);

    // ===== matching =====

    [Fact]
    public async Task Match_ByPath_IgnoresCase()
    {
        await using var e = await Env.CreateAsync();

        var m = await e.Matcher.MatchAsync(Play(T0, @"c:\mf-fixtures\BOOZE\A.MP3"), default);

        Assert.Equal(e.A, m.TrackId);
        Assert.Equal(HistoryMatchMethod.Path, m.Method);
        Assert.Equal(1.0, m.Confidence);
    }

    [Fact]
    public async Task Match_OnAnotherDriveLetter_FallsBackToNameAndSize()
    {
        await using var e = await Env.CreateAsync();

        var m = await e.Matcher.MatchAsync(Play(T0, @"D:\Elsewhere\a.mp3"), default);

        Assert.Equal(e.A, m.TrackId);
        Assert.Equal(HistoryMatchMethod.NameAndSize, m.Method);
        Assert.Equal(0.9, m.Confidence);
    }

    [Fact]
    public async Task Match_NameWithAWrongSize_UsesTheDurationWithinTolerance()
    {
        await using var e = await Env.CreateAsync();

        var near = await e.Matcher.MatchAsync(Play(T0, @"D:\x\a.mp3", size: 1, seconds: 121.0), default);
        var far = await e.Matcher.MatchAsync(Play(T0, @"D:\x\a.mp3", size: 1, seconds: 124.0), default);

        Assert.Equal(e.A, near.TrackId);
        Assert.Equal(HistoryMatchMethod.NameAndDuration, near.Method);
        Assert.Null(far.TrackId);
        Assert.Equal(HistoryUnmatchedReason.NotInLibrary, far.Reason);
    }

    [Fact]
    public async Task Match_TwoTracksFitTheSameNameAndSize_IsAmbiguousAndNeverGuessed()
    {
        await using var e = await Env.CreateAsync();

        var m = await e.Matcher.MatchAsync(Play(T0, @"D:\x\same.mp3", size: 5000), default);

        Assert.Null(m.TrackId);
        Assert.Equal(HistoryUnmatchedReason.Ambiguous, m.Reason);
        Assert.Equal(new[] { e.D1, e.D2 }, m.Candidates.OrderBy(x => x));
    }

    [Fact]
    public async Task Match_UnknownFile_IsNotInTheLibrary_EvenWhenArtistAndTitleMatchSomething()
    {
        await using var e = await Env.CreateAsync();

        var m = await e.Matcher.MatchAsync(Play(T0, @"D:\Greek\Βανδαλούπ.mp3", size: 123, seconds: 140), default);

        Assert.Null(m.TrackId);
        Assert.Equal(HistoryUnmatchedReason.NotInLibrary, m.Reason);
    }

    // ===== store =====

    [Fact]
    public async Task Store_FileStatesAreRemembered_AndUpdatedInPlace()
    {
        await using var e = await Env.CreateAsync();
        var store = e.Db.HistoryImport;

        await store.SaveFileStateAsync("VirtualDJ", new HistoryFileState(@"C:\H\2026-11-03.m3u", 499, T0));
        await store.SaveFileStateAsync("VirtualDJ", new HistoryFileState(@"C:\H\2026-11-03.m3u", 1790, T0.AddMinutes(5)));
        await store.SaveFileStateAsync("Traktor", new HistoryFileState(@"C:\H\2026-11-03.m3u", 1, T0));

        var states = await store.GetFileStatesAsync("VirtualDJ");

        var state = Assert.Single(states).Value;
        Assert.Equal(1790, state.Length);
        Assert.Equal(T0.AddMinutes(5), state.LastWriteUtc);
        Assert.True(states.ContainsKey(@"c:\h\2026-11-03.M3U"));      // the key ignores case, like Windows paths
    }

    [Fact]
    public async Task Store_UnmatchedPlays_AreStoredOnce_AndCanBeLinkedOrIgnored()
    {
        await using var e = await Env.CreateAsync();
        var store = e.Db.HistoryImport;
        UnmatchedHistoryEvent Entry(string key) => new(0, "VirtualDJ", key, T0, @"D:\x.mp3", "A", "T", 10, 1.5, HistoryUnmatchedReason.NotInLibrary, null, UnmatchedStatus.Pending, null);

        Assert.True(await store.AddUnmatchedAsync(Entry("k1")));
        Assert.False(await store.AddUnmatchedAsync(Entry("k1")));       // the same play again changes nothing
        Assert.True(await store.AddUnmatchedAsync(Entry("k2")));
        Assert.Equal(2, await store.CountPendingAsync());

        var pending = await store.GetUnmatchedAsync(UnmatchedStatus.Pending);
        await store.SetUnmatchedStatusAsync(pending[0].Id, UnmatchedStatus.Linked, e.A);
        await store.SetUnmatchedStatusAsync(pending[1].Id, UnmatchedStatus.Ignored, null);

        Assert.Equal(0, await store.CountPendingAsync());
        var linked = Assert.Single(await store.GetUnmatchedAsync(UnmatchedStatus.Linked));
        Assert.Equal(e.A, linked.ResolvedTrackId);
        Assert.Equal(T0, linked.PlayedUtc);
        Assert.Single(await store.GetUnmatchedAsync(UnmatchedStatus.Ignored));
    }

    // ===== upgrading a guess =====

    [Fact]
    public async Task ImportAfterAPossiblePlay_ReplacesItWithAConfirmedOne_AndCountsOnce()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync("VirtualDJ", default);
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);                                       // 20 s of quiet => Possible
        Assert.Equal((0, 1), e.Counts(e.A));

        var outcome = await e.ImportAsync(e.A, Play(T0.AddMinutes(3)));

        Assert.Equal(ImportedPlayOutcome.Upgraded, outcome);
        Assert.Equal((1, 0), e.Counts(e.A));                            // one play, now confirmed
        var plays = await e.PlaysAsync(e.A);
        Assert.Equal(2, plays.Count);
        var confirmed = plays.Single(p => p.Status == PlayStatus.Confirmed);
        Assert.Equal("VirtualDJ", confirmed.Source);
        Assert.Equal(T0.AddMinutes(3), confirmed.PlayedUtc);
        Assert.Equal(e.Service.Active!.Id, confirmed.SessionId);        // it belongs to the session of the drag
        Assert.True(plays.Single(p => p.Status == PlayStatus.Possible).Superseded);
        var events = await e.EventTypesAsync(e.A);
        Assert.Contains(nameof(TrackEventType.Exported), events);       // the original inference is kept
        Assert.Contains(nameof(TrackEventType.HeuristicPossible), events);
        Assert.Contains(nameof(TrackEventType.HistoryConfirmed), events);
    }

    [Fact]
    public async Task ImportingTheSamePlayTwice_ChangesNothingTheSecondTime()
    {
        await using var e = await Env.CreateAsync();
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);
        var play = Play(T0.AddMinutes(3));

        Assert.Equal(ImportedPlayOutcome.Upgraded, await e.ImportAsync(e.A, play));
        var eventsAfterFirst = (await e.EventTypesAsync(e.A)).Count;
        Assert.Equal(ImportedPlayOutcome.Duplicate, await e.ImportAsync(e.A, play));

        Assert.Equal((1, 0), e.Counts(e.A));
        Assert.Equal(2, (await e.PlaysAsync(e.A)).Count);
        Assert.Equal(eventsAfterFirst, (await e.EventTypesAsync(e.A)).Count);
    }

    [Fact]
    public async Task ImportWithoutAnyDrag_AddsAConfirmedPlay_AndCreatesNoSession()
    {
        await using var e = await Env.CreateAsync();

        var outcome = await e.ImportAsync(e.A, Play(T0.AddMinutes(-90)));

        Assert.Equal(ImportedPlayOutcome.Added, outcome);
        Assert.Equal((1, 0), e.Counts(e.A));
        Assert.Null(e.Service.Active);
        Assert.Empty(await e.Db.Sessions.GetRecentAsync(5));
        Assert.Null(Assert.Single(await e.PlaysAsync(e.A)).SessionId);
    }

    [Fact]
    public async Task ImportWhileASessionRuns_AttachesThePlayToThatSession()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        e.Clock.Advance(TimeSpan.FromMinutes(10));

        await e.ImportAsync(e.A, Play(T0.AddMinutes(5)));

        Assert.Equal(session, Assert.Single(await e.PlaysAsync(e.A)).SessionId);
    }

    [Fact]
    public async Task ImportWhileTheDragWindowIsStillOpen_ConfirmsItAndNoPossibleFollows()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(e.A);
        await e.AdvanceAsync(5);                                        // the 20 s window is still running

        var outcome = await e.ImportAsync(e.A, Play(T0.AddSeconds(5)));
        await e.AdvanceAsync(30);                                       // the window would have ended by now

        Assert.Equal(ImportedPlayOutcome.Upgraded, outcome);
        Assert.Equal((1, 0), e.Counts(e.A));                            // the guess never happened
        Assert.DoesNotContain(nameof(TrackEventType.HeuristicPossible), await e.EventTypesAsync(e.A));
    }

    [Fact]
    public async Task ADragFarFromTheHistoryTime_IsNotTouched()
    {
        await using var e = await Env.CreateAsync();
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);                                       // Possible at 19:00

        var outcome = await e.ImportAsync(e.A, Play(T0.AddHours(3)));   // played three hours later: a different play

        Assert.Equal(ImportedPlayOutcome.Added, outcome);
        Assert.Equal((1, 1), e.Counts(e.A));
    }

    [Fact]
    public async Task TwoPlaysAndOneDrag_TheFirstUpgradesAndTheSecondIsAdded()
    {
        await using var e = await Env.CreateAsync();
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);

        var first = await e.ImportAsync(e.A, Play(T0.AddMinutes(2)));
        var second = await e.ImportAsync(e.A, Play(T0.AddMinutes(8)));

        Assert.Equal(ImportedPlayOutcome.Upgraded, first);
        Assert.Equal(ImportedPlayOutcome.Added, second);
        Assert.Equal((2, 0), e.Counts(e.A));
    }

    [Fact]
    public async Task ADifferentTrackIsNeverUpgraded()
    {
        await using var e = await Env.CreateAsync();
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);

        var outcome = await e.ImportAsync(e.B, Play(T0.AddMinutes(2), @"C:\MF-Fixtures\Booze\b.mp3"));

        Assert.Equal(ImportedPlayOutcome.Added, outcome);
        Assert.Equal((0, 1), e.Counts(e.A));
        Assert.Equal((1, 0), e.Counts(e.B));
    }

    [Fact]
    public async Task AManualConfirmedPlay_IsReplacedByTheDjProgramsOwnHistory()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(e.A);
        await e.Service.MarkPlayedAsync(e.A, null, default);            // the user said so: Confirmed (manual)
        Assert.Equal((1, 0), e.Counts(e.A));

        await e.ImportAsync(e.A, Play(T0.AddMinutes(1)));

        Assert.Equal((1, 0), e.Counts(e.A));                            // still one play, not two
        Assert.Contains(await e.PlaysAsync(e.A), p => p.Source == "VirtualDJ" && !p.Superseded);
        Assert.Contains(await e.PlaysAsync(e.A), p => p.Source == PlaySources.User && p.Superseded);
    }

    [Fact]
    public async Task MarkNotPlayed_CannotRemoveAPlayTheDjProgramReported()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(e.A);
        await e.AdvanceAsync(25);
        await e.ImportAsync(e.A, Play(T0.AddMinutes(1)));

        await e.Service.MarkNotPlayedAsync(e.A, null, default);

        Assert.Equal((1, 0), e.Counts(e.A));                            // outside evidence stays
    }

    [Fact]
    public async Task TheWindow_IsThirtyMinutesBeforeAndTwoMinutesAfterTheDrag()
    {
        // The drag at 19:00 is accepted for a history time from 18:58 to 19:30, and not outside it.
        foreach (var (minutes, upgrades) in new[] { (-3, false), (-2, true), (0, true), (30, true), (31, false) })
        {
            await using var e = await Env.CreateAsync();
            await e.DragAsync(e.A);
            await e.AdvanceAsync(25);

            var outcome = await e.ImportAsync(e.A, Play(T0.AddMinutes(minutes)));

            Assert.True(upgrades == (outcome == ImportedPlayOutcome.Upgraded), $"history at {minutes:+0;-0} min: {outcome}");
        }
    }
}
