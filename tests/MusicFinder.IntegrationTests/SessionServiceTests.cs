using Dapper;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// CLAUDE.md section 9 on a real database with a fake clock: drag then 20 s of quiet => Possible, never Confirmed;
/// activity => stays Exported; manual decisions; sessions. No sleeping, no real OS drag.
/// </summary>
public class SessionServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);

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
        public FakeSettings Settings { get; } = new();
        public DjSessionService Service { get; }
        public long A { get; private set; }
        public long B { get; private set; }
        public long C { get; private set; }
        public List<IReadOnlyList<long>> PlayStateEvents { get; } = [];
        public int SessionChangedCount;

        public Env()
        {
            Service = new DjSessionService(Db.Sessions, Db.Events, Db.Plays, Settings, Clock);
            Service.PlayStateChanged += (_, ids) => { lock (PlayStateEvents) PlayStateEvents.Add(ids); };
            Service.SessionChanged += (_, _) => Interlocked.Increment(ref SessionChangedCount);
        }

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(folder, @"C:\Music\a.mp3", "Alpha", "Artist A"),
                TestDb.MakeTrack(folder, @"C:\Music\b.mp3", "Bravo", "Artist B"),
                TestDb.MakeTrack(folder, @"C:\Music\c.mp3", "Charlie", "Artist C"),
            ]);
            var files = await e.Db.Tracks.GetIndexedFilesAsync(root.Id);
            long Id(string n) => files.Single(f => f.FullPath.EndsWith(n)).Id;
            e.A = Id("a.mp3"); e.B = Id("b.mp3"); e.C = Id("c.mp3");
            return e;
        }

        /// <summary>The drag finished now (the fake clock's time), took 1.5 s and was accepted as a copy.</summary>
        public Task DragAsync(string effect = "Copy", string? foreground = "Traktor", params long[] tracks)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            return Service.RecordDragAsync(new DragRecord(tracks, now.AddSeconds(-1.5), now, effect, foreground), default);
        }

        /// <summary>Moves time on and waits for the background resolution that the timer started.</summary>
        public async Task AdvanceAsync(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            await Service.WaitForIdleAsync();
        }

        public async Task<List<TrackEvent>> EventsAsync(long? session = null)
        {
            var s = session ?? Service.Active?.Id ?? (await Db.Sessions.GetRecentAsync(1)).FirstOrDefault()?.Id ?? 0;
            return (await Db.Events.GetForSessionAsync(s)).ToList();
        }

        public async Task<List<PlayHistoryItem>> PlaysAsync(long track) => (await Db.Plays.GetForTrackAsync(track, 50)).ToList();

        public async Task<(int Confirmed, int Possible)> CountsAsync(long track)
        {
            using var conn = Db.Factory.Open();
            return conn.QuerySingleOrDefault<(int, int)>("SELECT ConfirmedCount, PossibleCount FROM TrackPlayStats WHERE TrackId = @track", new { track });
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    // ===== sessions =====

    [Fact]
    public async Task StartingAndStopping_AreManual_AndStartingTwiceReturnsTheLiveSession()
    {
        await using var e = await Env.CreateAsync();
        Assert.Null(e.Service.Active);

        var id = await e.Service.StartAsync("Traktor", default);

        Assert.Equal(id, e.Service.Active!.Id);
        Assert.Equal("Traktor", e.Service.Active.DjSoftware);
        Assert.False(e.Service.Active.AutoStarted);
        Assert.Equal("Session 01/11 21:00", e.Service.Active.Name);
        Assert.Equal(T0, e.Service.Active.StartedUtc);
        Assert.Equal(id, await e.Service.StartAsync("VirtualDJ", default)); // idempotent: no second session
        Assert.Equal(1, e.SessionChangedCount);

        e.Clock.Advance(TimeSpan.FromMinutes(90));
        await e.Service.StopAsync(default);

        Assert.Null(e.Service.Active);
        var stored = (await e.Db.Sessions.GetAsync(id))!;
        Assert.Equal(T0.AddMinutes(90), stored.EndedUtc);
        Assert.Equal(2, e.SessionChangedCount);
        await e.Service.StopAsync(default); // stopping when nothing is live is harmless
    }

    [Fact]
    public async Task Rename_ChangesTheLiveSession()
    {
        await using var e = await Env.CreateAsync();
        var id = await e.Service.StartAsync(null, default);

        await e.Service.RenameAsync("Friday at the club", "VirtualDJ", default);

        var stored = (await e.Db.Sessions.GetAsync(id))!;
        Assert.Equal(("Friday at the club", "VirtualDJ"), (stored.Name, stored.DjSoftware));
        Assert.Equal("Friday at the club", e.Service.Active!.Name);
    }

    // ===== drags =====

    [Fact]
    public async Task ADrag_IsRecordedAsAnAttemptAndAnExport_NeverAsAPlay()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync("Traktor", default);

        await e.DragAsync("Copy", "Traktor", e.A);

        var events = await e.EventsAsync();
        Assert.Equal([nameof(TrackEventType.DragStarted), nameof(TrackEventType.Exported)], events.Select(x => x.EventType));
        Assert.All(events, x => Assert.Equal(session, x.SessionId));
        var export = events[1];
        Assert.Contains("\"effect\":\"Copy\"", export.EvidenceJson);
        Assert.Contains("\"outcome\":\"DragCompletedUnknown\"", export.EvidenceJson);   // observed: dropped; unknown: loaded/played
        Assert.Contains("\"foreground\":\"Traktor\"", export.EvidenceJson);
        Assert.Contains("\"windowSeconds\":20", export.EvidenceJson);
        Assert.Equal(T0, export.OccurredUtc);
        Assert.Equal(T0.AddSeconds(-1.5), events[0].OccurredUtc);

        Assert.Empty(await e.PlaysAsync(e.A));                                           // no play yet, and none is ever Confirmed by a drag
        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Exported, entry.Status);
        Assert.Equal(("Copy", "Traktor"), (entry.Effect, entry.Target));
        Assert.Contains("Waiting for 20 s", entry.Evidence);
    }

    [Fact]
    public async Task ACancelledDrag_RecordsNothing_AndStartsNoSession()
    {
        await using var e = await Env.CreateAsync();

        await e.DragAsync("None", null, e.A);                                            // Esc, or a target that refused it
        await e.Service.RecordDragAsync(new DragRecord([], T0, T0, "Copy"), default);     // nothing selected

        Assert.Null(e.Service.Active);
        Assert.Empty(await e.Db.Sessions.GetRecentAsync(10));
        Assert.Empty(await e.Db.Events.GetForTrackAsync(e.A, 10));
    }

    // ===== the 20 s heuristic =====

    [Fact]
    public async Task TwentySecondsOfQuiet_BecomesAPossiblePlay_NeverConfirmed()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);

        await e.AdvanceAsync(19.9);
        Assert.Empty(await e.PlaysAsync(e.A));

        await e.AdvanceAsync(0.1);

        var play = Assert.Single(await e.PlaysAsync(e.A));
        Assert.Equal(("Possibly played", PlayStatus.Possible, PlaySources.InactivityHeuristic), (play.Label, play.Status, play.Source));
        Assert.Equal(T0, play.PlayedUtc);                                                // stamped with the drop time
        Assert.Equal(session, play.SessionId);
        Assert.Equal((0, 1), await e.CountsAsync(e.A));

        var events = await e.EventsAsync();
        var resolved = events.Single(x => x.EventType == nameof(TrackEventType.HeuristicPossible));
        Assert.Equal(PlaySources.InactivityHeuristic, resolved.Source);
        Assert.Equal(T0.AddSeconds(20), resolved.OccurredUtc);
        Assert.Contains("\"quietSeconds\":20", resolved.EvidenceJson);

        var raised = e.PlayStateEvents.SelectMany(x => x).ToList();
        Assert.Contains(e.A, raised);

        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Possible, entry.Status);
        Assert.Contains("weak evidence, not proof", entry.Evidence);

        await e.AdvanceAsync(600);                                                       // resolved exactly once
        Assert.Equal((0, 1), await e.CountsAsync(e.A));
    }

    [Fact]
    public async Task ActivityBeforeTheDeadline_LeavesTheExportExported_AndNeverCreatesAPlay()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);

        await e.AdvanceAsync(19);
        e.Service.NotifyActivity(UserActivity.Search);
        await e.Service.WaitForIdleAsync();
        await e.AdvanceAsync(600);

        Assert.Empty(await e.PlaysAsync(e.A));
        var interrupted = (await e.EventsAsync()).Single(x => x.EventType == nameof(TrackEventType.ActivityInterrupted));
        Assert.Contains("\"activity\":\"Search\"", interrupted.EvidenceJson);
        Assert.Contains("\"atSeconds\":19", interrupted.EvidenceJson);
        Assert.DoesNotContain(await e.EventsAsync(), x => x.EventType == nameof(TrackEventType.HeuristicPossible));

        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Exported, entry.Status);                           // never "not played"
        Assert.Contains("Search or filters changed 19 s after the drop", entry.Evidence);
    }

    [Theory]
    [InlineData(UserActivity.Search, "Search or filters changed")]
    [InlineData(UserActivity.Scope, "The folder changed")]
    [InlineData(UserActivity.Selection, "A different track was selected")]
    public async Task EachKindOfActivity_InterruptsAndIsExplained(UserActivity activity, string expected)
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(7);

        e.Service.NotifyActivity(activity);
        await e.Service.WaitForIdleAsync();

        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Exported, entry.Status);
        Assert.Contains(expected, entry.Evidence);
        Assert.Contains("7 s after the drop", entry.Evidence);
    }

    [Fact]
    public async Task ActivityAfterTheWindowAlreadyRan_DoesNotUndoThePossiblePlay()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);

        await e.AdvanceAsync(25);                                                         // the window ended at 20 s
        e.Service.NotifyActivity(UserActivity.Search);                                    // ...and only then the user types
        await e.Service.WaitForIdleAsync();

        Assert.Single(await e.PlaysAsync(e.A));
        Assert.DoesNotContain(await e.EventsAsync(), x => x.EventType == nameof(TrackEventType.ActivityInterrupted));
    }

    [Fact]
    public async Task ActivityWithNothingPending_WritesNothing()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);

        e.Service.NotifyActivity(UserActivity.Search);
        await e.Service.WaitForIdleAsync();

        Assert.Empty(await e.EventsAsync());
    }

    [Fact]
    public async Task AnotherDrag_DoesNotInterruptEarlierWindows_EachExportHasItsOwnClock()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);                       // deck 1 at 0 s
        await e.AdvanceAsync(10);
        await e.DragAsync(tracks: e.B);                       // deck 2 at 10 s

        await e.AdvanceAsync(10);                             // t = 20: A is done, B has 10 s left
        Assert.Single(await e.PlaysAsync(e.A));
        Assert.Empty(await e.PlaysAsync(e.B));

        await e.AdvanceAsync(10);                             // t = 30
        Assert.Single(await e.PlaysAsync(e.B));
        Assert.DoesNotContain(await e.EventsAsync(), x => x.EventType == nameof(TrackEventType.ActivityInterrupted));
    }

    [Fact]
    public async Task TheSameTrackDraggedTwice_GivesTwoExports_AndTwoPossiblePlays()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(5);
        await e.DragAsync(tracks: e.A);

        await e.AdvanceAsync(30);

        Assert.Equal(2, (await e.EventsAsync()).Count(x => x.EventType == nameof(TrackEventType.Exported)));
        Assert.Equal(2, (await e.PlaysAsync(e.A)).Count);
        Assert.Equal((0, 2), await e.CountsAsync(e.A));
    }

    [Fact]
    public async Task ADragOfSeveralTracks_OpensOneWindowPerTrack()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);

        await e.DragAsync("Copy", "Traktor", e.A, e.B, e.C);
        Assert.Equal(6, (await e.EventsAsync()).Count);                                    // 3 attempts + 3 exports
        await e.AdvanceAsync(20);

        Assert.All(new[] { e.A, e.B, e.C }, t => Assert.Equal((0, 1), e.CountsAsync(t).Result));
        var entries = await e.Service.GetEntriesAsync(session, default);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, x => Assert.Equal(SessionPlayStatus.Possible, x.Status));
    }

    [Theory]
    [InlineData(15, 14.9, 0.1)]
    [InlineData(17, 16.9, 0.1)]
    [InlineData(20, 19.9, 0.1)]
    [InlineData(5, 14.9, 0.1)]     // below the allowed range: clamped to 15
    [InlineData(300, 19.9, 0.1)]   // above: clamped to 20
    public async Task TheConfiguredWindow_IsUsed_AndClampedToFifteenToTwenty(int configured, double before, double rest)
    {
        await using var e = await Env.CreateAsync();
        e.Settings.Value.SessionHeuristicSeconds = configured;
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);

        await e.AdvanceAsync(before);
        Assert.Empty(await e.PlaysAsync(e.A));
        await e.AdvanceAsync(rest);
        Assert.Single(await e.PlaysAsync(e.A));
    }

    [Fact]
    public async Task ChangingTheSetting_AffectsOnlyTheNextDrag()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);                       // opened with 20 s
        e.Settings.Value.SessionHeuristicSeconds = 15;
        await e.DragAsync(tracks: e.B);                       // opened with 15 s

        await e.AdvanceAsync(15);
        Assert.Empty(await e.PlaysAsync(e.A));
        Assert.Single(await e.PlaysAsync(e.B));
        await e.AdvanceAsync(5);
        Assert.Single(await e.PlaysAsync(e.A));
    }

    // ===== manual decisions =====

    [Fact]
    public async Task MarkingAsPlayed_ReplacesThePossiblePlay_SoTheTrackCountsOnce()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(20);
        Assert.Equal((0, 1), await e.CountsAsync(e.A));

        await e.Service.MarkPlayedAsync(e.A, null, default);

        var play = Assert.Single(await e.PlaysAsync(e.A));
        Assert.Equal(("Confirmed (manual)", PlayStatus.Confirmed), (play.Label, play.Status));
        Assert.Equal((1, 0), await e.CountsAsync(e.A));                                    // not "1 confirmed + 1 possible"
        Assert.Contains(await e.EventsAsync(), x => x.EventType == nameof(TrackEventType.ManualMarkPlayed));
        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Confirmed, entry.Status);
        Assert.Contains("Marked as played by you", entry.Evidence);
        Assert.True(e.Service.CanUndo);
    }

    [Fact]
    public async Task MarkingAsPlayed_BeforeTheWindowEnds_CancelsItSoNothingIsCountedTwice()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(5);

        await e.Service.MarkPlayedAsync(e.A, null, default);
        await e.AdvanceAsync(60);

        Assert.Equal((1, 0), await e.CountsAsync(e.A));
        Assert.Single(await e.PlaysAsync(e.A));
    }

    [Fact]
    public async Task MarkingTheSameExportAsPlayedTwice_StillCountsOnce()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.Service.MarkPlayedAsync(e.A, null, default);
        await e.Service.MarkPlayedAsync(e.A, null, default);

        Assert.Equal((1, 0), await e.CountsAsync(e.A));
    }

    [Fact]
    public async Task MarkingAsNotPlayed_RemovesThePossiblePlay_AndUndoBringsItBack()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(20);

        await e.Service.MarkNotPlayedAsync(e.A, null, default);

        Assert.Empty(await e.PlaysAsync(e.A));
        Assert.Equal((0, 0), await e.CountsAsync(e.A));
        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Exported, entry.Status);
        Assert.Contains("Marked as not played by you", entry.Evidence);

        Assert.True(await e.Service.UndoAsync(default));

        var back = Assert.Single(await e.PlaysAsync(e.A));
        Assert.Equal(PlayStatus.Possible, back.Status);
        Assert.Equal((0, 1), await e.CountsAsync(e.A));
        Assert.Contains(await e.EventsAsync(), x => x.EventType == nameof(TrackEventType.ManualUndo));
        Assert.False(e.Service.CanUndo);
        Assert.False(await e.Service.UndoAsync(default));                                   // nothing left to undo
    }

    [Fact]
    public async Task MarkingAsNotPlayed_WhileTheWindowIsOpen_CancelsIt()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(5);

        await e.Service.MarkNotPlayedAsync(e.A, null, default);
        await e.AdvanceAsync(60);

        Assert.Empty(await e.PlaysAsync(e.A));                                              // the user decided; the heuristic must not override it
    }

    [Fact]
    public async Task UndoingAMarkAsPlayed_RestoresThePossiblePlay()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(20);
        await e.Service.MarkPlayedAsync(e.A, null, default);

        Assert.True(await e.Service.UndoAsync(default));

        var play = Assert.Single(await e.PlaysAsync(e.A));
        Assert.Equal(PlayStatus.Possible, play.Status);
        Assert.Equal((0, 1), await e.CountsAsync(e.A));
    }

    [Fact]
    public async Task NotPlayed_NeverDeletesWhatADjApplicationConfirmed()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.Plays.AddAsync(new PlayRecord
        {
            TrackId = e.A, PlayedUtc = T0, Status = "Confirmed", Source = PlaySources.Traktor, ExternalEventKey = "traktor:1"
        });

        await e.Service.MarkNotPlayedAsync(e.A, null, default);

        var play = Assert.Single(await e.PlaysAsync(e.A));
        Assert.Equal("Confirmed (Traktor)", play.Label);                                    // evidence from outside is not the user's to erase here
        Assert.False(e.Service.CanUndo);
    }

    [Fact]
    public async Task MarkingAsPlayed_WithoutAnExport_AddsAManualPlay_InNoSessionIfNoneIsLive()
    {
        await using var e = await Env.CreateAsync();

        await e.Service.MarkPlayedAsync(e.B, null, default);

        var play = Assert.Single(await e.PlaysAsync(e.B));
        Assert.Equal((PlayStatus.Confirmed, null), (play.Status, play.SessionId));
        Assert.Equal((1, 0), await e.CountsAsync(e.B));
    }

    [Fact]
    public async Task ConfirmPlay_RecordsAConfirmedPlayAtTheGivenTime()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        var at = new DateTimeOffset(T0.AddMinutes(-7));

        await e.Service.ConfirmPlayAsync(session, e.C, at, default);

        var play = Assert.Single(await e.PlaysAsync(e.C));
        Assert.Equal((PlayStatus.Confirmed, session, at.UtcDateTime), (play.Status, play.SessionId, play.PlayedUtc));
    }

    // ===== automatic sessions =====

    [Fact]
    public async Task TheFirstDrag_StartsASessionAutomatically_AndSaysSo()
    {
        await using var e = await Env.CreateAsync();

        await e.DragAsync(tracks: e.A);

        var session = e.Service.Active!;
        Assert.True(session.AutoStarted);
        Assert.Equal(1, e.SessionChangedCount);
        Assert.All(await e.EventsAsync(), x => Assert.Equal(session.Id, x.SessionId));
    }

    [Fact]
    public async Task WithAutoStartOff_ADragIsStillRecorded_ButNoSessionIsCreated()
    {
        await using var e = await Env.CreateAsync();
        e.Settings.Value.AutoStartSessionOnDrag = false;

        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(20);

        Assert.Null(e.Service.Active);
        Assert.Empty(await e.Db.Sessions.GetRecentAsync(10));
        var events = await e.Db.Events.GetForTrackAsync(e.A, 10);
        Assert.Equal(3, events.Count);                                                      // attempt, export, heuristic result
        Assert.All(events, x => Assert.Null(x.SessionId));
        Assert.Single(await e.PlaysAsync(e.A));                                             // the play still counts for the library
    }

    [Fact]
    public async Task ALongGap_StartsANewSession_InsteadOfMergingUnrelatedNights()
    {
        await using var e = await Env.CreateAsync();
        var first = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(60);

        e.Clock.Advance(TimeSpan.FromHours(5));                                              // next night
        await e.DragAsync(tracks: e.B);

        var second = e.Service.Active!;
        Assert.NotEqual(first, second.Id);
        Assert.True(second.AutoStarted);
        var old = (await e.Db.Sessions.GetAsync(first))!;
        Assert.NotNull(old.EndedUtc);
        Assert.True(old.EndedUtc < T0.AddMinutes(5));                                         // closed where it really stopped, not "now"
        var entries = await e.Service.GetEntriesAsync(second.Id, default);
        Assert.Equal(["Bravo"], entries.Select(x => x.Title));
    }

    [Fact]
    public async Task ABreakWithinTheGap_KeepsTheSameSession()
    {
        await using var e = await Env.CreateAsync();
        var first = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);

        e.Clock.Advance(TimeSpan.FromHours(3.5));
        await e.DragAsync(tracks: e.B);

        Assert.Equal(first, e.Service.Active!.Id);
        Assert.Equal(1, (await e.Db.Sessions.GetRecentAsync(10)).Count);
    }

    // ===== lifetime =====

    [Fact]
    public async Task Stopping_ResolvesOpenWindowsAsInterrupted_NotAsPlayed()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(8);

        await e.Service.StopAsync(default);
        await e.AdvanceAsync(120);

        Assert.Empty(await e.PlaysAsync(e.A));
        var entry = (await e.Service.GetEntriesAsync(session, default)).Single();
        Assert.Equal(SessionPlayStatus.Exported, entry.Status);
        Assert.Contains("The session was stopped 8 s after the drop", entry.Evidence);
    }

    [Fact]
    public async Task Shutdown_InterruptsPendingWindows_AndEndsTheSession()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(3);

        await e.Service.ShutdownAsync();
        e.Clock.Advance(TimeSpan.FromMinutes(5));                                             // a late timer must not fire into a closed app
        await e.Service.WaitForIdleAsync();

        Assert.Empty(await e.PlaysAsync(e.A));
        Assert.NotNull((await e.Db.Sessions.GetAsync(session))!.EndedUtc);
        Assert.Null(e.Service.Active);
        var interrupted = (await e.EventsAsync(session)).Single(x => x.EventType == nameof(TrackEventType.ActivityInterrupted));
        Assert.Contains("\"activity\":\"AppClosed\"", interrupted.EvidenceJson);
        await e.Service.ShutdownAsync();                                                       // twice is fine
    }

    [Fact]
    public async Task Initialize_ClosesASessionAnEarlierRunLeftOpen_AtItsLastSignOfLife()
    {
        await using var e = await Env.CreateAsync();
        var crashed = await e.Db.Sessions.StartAsync(T0.AddHours(-10), "Traktor", "crashed night", false);
        await e.Db.Events.AddAsync([new TrackEvent
        {
            TrackId = e.A, SessionId = crashed, EventType = "Exported", OccurredUtc = T0.AddHours(-9), Source = PlaySources.DragExport
        }]);

        await e.Service.InitializeAsync(default);

        Assert.Null(e.Service.Active);                                                         // a restart does not resume it
        Assert.Equal(T0.AddHours(-9), (await e.Db.Sessions.GetAsync(crashed))!.EndedUtc);
    }

    // ===== robustness =====

    [Fact]
    public async Task ManyParallelDrags_AreAllRecorded_AndEachResolvesExactlyOnce()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => e.DragAsync("Copy", null, i % 2 == 0 ? e.A : e.B)));
        await e.AdvanceAsync(30);

        Assert.Equal(20, (await e.EventsAsync()).Count(x => x.EventType == nameof(TrackEventType.Exported)));
        Assert.Equal(10, (await e.CountsAsync(e.A)).Possible);
        Assert.Equal(10, (await e.CountsAsync(e.B)).Possible);
    }

    [Fact]
    public async Task EntriesAreNewestFirst_AndNameTheTrack()
    {
        await using var e = await Env.CreateAsync();
        var session = await e.Service.StartAsync(null, default);
        await e.DragAsync("Copy", "Traktor", e.A);
        await e.AdvanceAsync(10);
        await e.DragAsync("Link", "VirtualDJ", e.B);

        var entries = await e.Service.GetEntriesAsync(session, default);

        Assert.Equal(["Bravo", "Alpha"], entries.Select(x => x.Title));
        Assert.Equal(["Link", "Copy"], entries.Select(x => x.Effect));
        Assert.Equal(["VirtualDJ", "Traktor"], entries.Select(x => x.Target));
        Assert.Equal(["Artist B", "Artist A"], entries.Select(x => x.Artist));
    }

    [Fact]
    public async Task ThePlayHistoryQuery_ReturnsTheTracksPlays()
    {
        await using var e = await Env.CreateAsync();
        await e.Service.StartAsync(null, default);
        await e.DragAsync(tracks: e.A);
        await e.AdvanceAsync(20);

        IPlayHistoryQuery query = e.Service;
        var items = await query.GetForTrackAsync(e.A, 10, default);

        Assert.Equal(["Possibly played"], items.Select(x => x.Label));
    }
}
