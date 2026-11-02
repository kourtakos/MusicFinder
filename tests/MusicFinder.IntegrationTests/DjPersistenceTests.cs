using Dapper;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.IntegrationTests;

public class DjPersistenceTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);

    private sealed record Env(TestDb Db, long TrackA, long TrackB);

    private static async Task<Env> CreateAsync()
    {
        var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Music");
        var folder = await db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
        await db.Tracks.UpsertBatchAsync(
        [
            TestDb.MakeTrack(folder, @"C:\Music\a.mp3", "Alpha", "Artist A"),
            TestDb.MakeTrack(folder, @"C:\Music\b.mp3", null, "Artist B"), // no title: falls back to the file name
        ]);
        var files = await db.Tracks.GetIndexedFilesAsync(root.Id);
        return new Env(db, files.Single(f => f.FullPath.EndsWith("a.mp3")).Id, files.Single(f => f.FullPath.EndsWith("b.mp3")).Id);
    }

    private static PlayRecord Play(long track, DateTime at, PlayStatus status, string source = PlaySources.InactivityHeuristic,
        string? key = null, long? session = null, long? related = null) => new()
    {
        TrackId = track, PlayedUtc = at, Status = status.ToString(), Source = source, ExternalEventKey = key, SessionId = session, RelatedEventId = related
    };

    private static async Task<(DateTime? Confirmed, DateTime? Possible, DateTime? Played, string? Status, int ConfirmedCount, int PossibleCount)?> Stats(TestDb db, long track)
    {
        using var conn = db.Factory.Open();
        var row = await conn.QuerySingleOrDefaultAsync<(string? C, string? P, string? L, string? S, int CC, int PC)?>(
            "SELECT LastConfirmedUtc, LastPossibleUtc, LastPlayedUtc, LastStatus, ConfirmedCount, PossibleCount FROM TrackPlayStats WHERE TrackId = @track", new { track });
        if (row is null) return null;
        static DateTime? D(string? s) => s is null ? null : DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        return (D(row.Value.C), D(row.Value.P), D(row.Value.L), row.Value.S, row.Value.CC, row.Value.PC);
    }

    // ===== sessions =====

    [Fact]
    public async Task Sessions_StartEndAndTheActiveOneIsTheOpenOne()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        Assert.Null(await e.Db.Sessions.GetActiveAsync());

        var id = await e.Db.Sessions.StartAsync(T0, "Traktor", "Friday", autoStarted: false);
        var active = (await e.Db.Sessions.GetActiveAsync())!;
        Assert.Equal((id, "Traktor", "Friday", false), (active.Id, active.DjSoftware, active.Name, active.AutoStarted));
        Assert.Equal(T0, active.StartedUtc);
        Assert.Equal(DateTimeKind.Utc, active.StartedUtc.Kind);
        Assert.Null(active.EndedUtc);

        await e.Db.Sessions.UpdateAsync(id, "VirtualDJ", "Friday late");
        await e.Db.Sessions.EndAsync(id, T0.AddHours(3));

        Assert.Null(await e.Db.Sessions.GetActiveAsync());
        var ended = (await e.Db.Sessions.GetAsync(id))!;
        Assert.Equal(T0.AddHours(3), ended.EndedUtc);
        Assert.Equal(DateTimeKind.Utc, ended.EndedUtc!.Value.Kind);
        Assert.Equal(("VirtualDJ", "Friday late"), (ended.DjSoftware, ended.Name));

        await e.Db.Sessions.EndAsync(id, T0.AddHours(9)); // ending twice keeps the first end time
        Assert.Equal(T0.AddHours(3), (await e.Db.Sessions.GetAsync(id))!.EndedUtc);
    }

    [Fact]
    public async Task AutoStartedFlag_SurvivesAndRecentSessionsAreNewestFirst()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var first = await e.Db.Sessions.StartAsync(T0, null, null, autoStarted: true);
        await e.Db.Sessions.EndAsync(first, T0.AddHours(1));
        var second = await e.Db.Sessions.StartAsync(T0.AddDays(1), null, null, autoStarted: false);

        var recent = await e.Db.Sessions.GetRecentAsync(10);
        Assert.Equal([second, first], recent.Select(s => s.Id));
        Assert.True(recent[1].AutoStarted);
        Assert.Single(await e.Db.Sessions.GetRecentAsync(1));
        Assert.Equal(second, (await e.Db.Sessions.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task AfterACrash_SeveralOpenSessionsExist_AndTheNewestIsActive()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        await e.Db.Sessions.StartAsync(T0, null, "old, never closed", false);
        var newer = await e.Db.Sessions.StartAsync(T0.AddHours(5), null, "newer", false);
        Assert.Equal(newer, (await e.Db.Sessions.GetActiveAsync())!.Id);
    }

    // ===== events =====

    [Fact]
    public async Task Events_AreAppendedInOrder_AndReadBackPerSessionAndTrack()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var session = await e.Db.Sessions.StartAsync(T0, null, null, false);
        Assert.Null(await e.Db.Sessions.GetLastEventUtcAsync(session));

        var ids = await e.Db.Events.AddAsync(
        [
            new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = nameof(TrackEventType.DragStarted), OccurredUtc = T0, Source = PlaySources.DragExport },
            new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = nameof(TrackEventType.Exported), OccurredUtc = T0.AddSeconds(2), Source = PlaySources.DragExport, EvidenceJson = "{\"effect\":\"Copy\"}" },
            new TrackEvent { TrackId = e.TrackB, SessionId = session, EventType = nameof(TrackEventType.Exported), OccurredUtc = T0.AddSeconds(2), Source = PlaySources.DragExport },
            new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = nameof(TrackEventType.Exported), OccurredUtc = T0.AddSeconds(30), Source = PlaySources.DragExport },
        ]);

        Assert.Equal(4, ids.Count);
        Assert.Equal(ids.Order(), ids);                                   // ids come back in input order, strictly increasing
        Assert.Equal(T0.AddSeconds(30), await e.Db.Sessions.GetLastEventUtcAsync(session));

        var all = await e.Db.Events.GetForSessionAsync(session);
        Assert.Equal(4, all.Count);
        Assert.Equal("{\"effect\":\"Copy\"}", all[1].EvidenceJson);
        Assert.Equal(DateTimeKind.Utc, all[0].OccurredUtc.Kind);

        var anchors = await e.Db.Events.GetAnchorsForTrackAsync(session, e.TrackA);
        Assert.Equal([ids[3], ids[1]], anchors.Select(a => a.Id));          // the same track dragged twice: two anchors, newest first
        Assert.Equal(3, (await e.Db.Events.GetForTrackAsync(e.TrackA, 10)).Count);
        Assert.Single(await e.Db.Events.GetForTrackAsync(e.TrackA, 1));
        Assert.Empty(await e.Db.Events.AddAsync([]));
    }

    // ===== plays and statistics =====

    [Fact]
    public async Task APlay_UpdatesTheTrackStatistics_ThroughTheTriggers()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        Assert.Null(await Stats(e.Db, e.TrackA));

        await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible));
        var s = (await Stats(e.Db, e.TrackA))!.Value;
        Assert.Equal((null, T0, T0, "Possible", 0, 1), (s.Confirmed, s.Possible, s.Played, s.Status, s.ConfirmedCount, s.PossibleCount));

        await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddMinutes(10), PlayStatus.Confirmed, PlaySources.User, "manual:1"));
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddMinutes(5), PlayStatus.Possible, key: "evt:2"));
        s = (await Stats(e.Db, e.TrackA))!.Value;
        Assert.Equal(T0.AddMinutes(10), s.Confirmed);
        Assert.Equal(T0.AddMinutes(5), s.Possible);
        Assert.Equal(T0.AddMinutes(10), s.Played);
        Assert.Equal("Confirmed", s.Status);                                   // the latest play wins
        Assert.Equal((1, 2), (s.ConfirmedCount, s.PossibleCount));

        Assert.Null(await Stats(e.Db, e.TrackB));                              // other tracks are untouched
    }

    [Fact]
    public async Task ASupersededPossible_IsNotCountedTwice_ButTheEvidenceRemains()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var possible = (await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible, key: "evt:1")))!.Value;
        var confirmed = (await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddSeconds(-3), PlayStatus.Confirmed, PlaySources.Traktor, "traktor:abc")))!.Value;

        await e.Db.Plays.MarkSupersededAsync(possible, confirmed);

        var s = (await Stats(e.Db, e.TrackA))!.Value;
        Assert.Equal((1, 0), (s.ConfirmedCount, s.PossibleCount));             // one real play, not "1 confirmed + 1 possible"
        Assert.Equal("Confirmed", s.Status);
        Assert.Null(s.Possible);

        var history = await e.Db.Plays.GetForTrackAsync(e.TrackA, 10);          // the Possible row is still listed, flagged
        Assert.Equal(2, history.Count);
        Assert.Single(history, h => h.Superseded);
        Assert.Equal("Confirmed (Traktor)", history.Single(h => !h.Superseded).Label);
    }

    [Fact]
    public async Task DeletingThePlays_RemovesTheStatisticsRow_NotJustZeroesIt()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var session = await e.Db.Sessions.StartAsync(T0, null, null, false);
        var anchor = (await e.Db.Events.AddAsync([new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = "Exported", OccurredUtc = T0, Source = PlaySources.DragExport }]))[0];
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible, key: $"evt:{anchor}", session: session, related: anchor));
        Assert.NotNull(await Stats(e.Db, e.TrackA));

        var deleted = await e.Db.Plays.DeleteByAnchorAsync(anchor);

        Assert.Single(deleted);
        Assert.Null(await Stats(e.Db, e.TrackA));
        Assert.Empty(await e.Db.Plays.GetForTrackAsync(e.TrackA, 10));
    }

    [Fact]
    public async Task DeleteThenRestore_RoundTripsThePlay_WithItsOriginalId()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var session = await e.Db.Sessions.StartAsync(T0, null, null, false);
        var anchor = (await e.Db.Events.AddAsync([new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = "Exported", OccurredUtc = T0, Source = PlaySources.DragExport }]))[0];
        var id = (await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddSeconds(20), PlayStatus.Possible, key: $"evt:{anchor}", session: session, related: anchor)))!.Value;

        var snapshot = await e.Db.Plays.DeleteByAnchorAsync(anchor);
        await e.Db.Plays.RestoreAsync(snapshot.Single());

        var back = (await e.Db.Plays.GetForTrackAsync(e.TrackA, 10)).Single();
        Assert.Equal(id, back.Id);
        Assert.Equal(T0.AddSeconds(20), back.PlayedUtc);
        Assert.Equal(1, (await Stats(e.Db, e.TrackA))!.Value.PossibleCount);
        await e.Db.Plays.RestoreAsync(snapshot.Single());                      // restoring twice does not duplicate
        Assert.Single(await e.Db.Plays.GetForTrackAsync(e.TrackA, 10));
    }

    [Fact]
    public async Task TheSameExternalKey_IsIdempotent_SoRepeatedImportsAndResolutionsDoNotDoubleCount()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var first = await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Confirmed, PlaySources.VirtualDj, "vdj:2026-11-01T21:00|a.mp3"));
        var again = await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Confirmed, PlaySources.VirtualDj, "vdj:2026-11-01T21:00|a.mp3"));
        var otherSource = await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Confirmed, PlaySources.Traktor, "vdj:2026-11-01T21:00|a.mp3"));

        Assert.NotNull(first);
        Assert.Null(again);                                                    // ignored, no exception
        Assert.NotNull(otherSource);                                           // the key is unique per source
        Assert.Equal(2, (await Stats(e.Db, e.TrackA))!.Value.ConfirmedCount);
    }

    [Fact]
    public async Task PlaysWithoutAnExternalKey_AreAlwaysAdded()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        Assert.NotNull(await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Confirmed, PlaySources.User)));
        Assert.NotNull(await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Confirmed, PlaySources.User)));
        Assert.Equal(2, (await Stats(e.Db, e.TrackA))!.Value.ConfirmedCount);
    }

    [Fact]
    public async Task StatisticsStayCorrect_EvenWhenAnImporterInsertsWithOrIgnoreOrOrReplace()
    {
        // In SQLite a conflict clause on the statement that fires a trigger overrides the clauses inside the trigger body.
        // A trigger that used "INSERT OR REPLACE" would silently freeze the statistics for every importer using OR IGNORE.
        var e = await CreateAsync();
        await using var _ = e.Db;
        foreach (var verb in new[] { "INSERT OR IGNORE", "INSERT OR REPLACE", "INSERT" })
        {
            await e.Db.Writes.EnqueueAsync((c, t) => c.Execute(
                $"{verb} INTO PlayHistory(TrackId, PlayedUtc, Status, Source, ExternalEventKey) VALUES (@track, @at, 'Confirmed', 'Importer', @key)",
                new { track = e.TrackA, at = DapperConfig.ToDb(T0), key = Guid.NewGuid().ToString() }, t));
        }

        Assert.Equal(3, (await Stats(e.Db, e.TrackA))!.Value.ConfirmedCount);
    }

    [Fact]
    public async Task TimesRoundTripExactly_NoMatterWhichTimeZoneTheMachineUses()
    {
        // Microsoft.Data.Sqlite converts text ending in "Z" to local time when reading; entities must undo that.
        var e = await CreateAsync();
        await using var _ = e.Db;
        var at = new DateTime(2026, 6, 15, 23, 30, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        await e.Db.Plays.AddAsync(Play(e.TrackA, at, PlayStatus.Confirmed, PlaySources.User));

        var item = (await e.Db.Plays.GetForTrackAsync(e.TrackA, 1)).Single();
        Assert.Equal(at, item.PlayedUtc);
        Assert.Equal(DateTimeKind.Utc, item.PlayedUtc.Kind);

        var session = await e.Db.Sessions.StartAsync(at, null, null, false);
        Assert.Equal(at, (await e.Db.Sessions.GetAsync(session))!.StartedUtc);
        await e.Db.Sessions.EndAsync(session, at.AddHours(2));
        Assert.Equal(at.AddHours(2), (await e.Db.Sessions.GetAsync(session))!.EndedUtc);
    }

    [Fact]
    public async Task ThePlayStatusCheckConstraint_RejectsAnythingButPossibleOrConfirmed()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        await Assert.ThrowsAnyAsync<Exception>(() => e.Db.Plays.AddAsync(
            new PlayRecord { TrackId = e.TrackA, PlayedUtc = T0, Status = "Exported", Source = "x" })); // "Exported" belongs in TrackEvents
    }

    [Fact]
    public async Task APlayThatMovesToAnotherTrack_UpdatesBothTracks()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var id = (await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible, key: "k")))!.Value;

        await e.Db.Writes.EnqueueAsync((c, t) => c.Execute("UPDATE PlayHistory SET TrackId = @b WHERE Id = @id", new { b = e.TrackB, id }, t));

        Assert.Null(await Stats(e.Db, e.TrackA));
        Assert.Equal(1, (await Stats(e.Db, e.TrackB))!.Value.PossibleCount);
    }

    // ===== history and session entries =====

    [Fact]
    public async Task TrackHistory_IsNewestFirst_WithSessionNamesAndLabels()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var session = await e.Db.Sessions.StartAsync(T0, "Traktor", "Friday", false);
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible, key: "a", session: session));
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddHours(1), PlayStatus.Confirmed, PlaySources.User, "b", session));
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0.AddHours(2), PlayStatus.Confirmed, PlaySources.Traktor, "c"));

        var history = await e.Db.Plays.GetForTrackAsync(e.TrackA, 10);

        Assert.Equal(["Confirmed (Traktor)", "Confirmed (manual)", "Possibly played"], history.Select(h => h.Label));
        Assert.Equal([null, "Friday", "Friday"], history.Select(h => h.SessionName));
        Assert.All(history, h => Assert.Equal(DateTimeKind.Utc, h.PlayedUtc.Kind));
        Assert.Single(await e.Db.Plays.GetForTrackAsync(e.TrackA, 1));
    }

    [Fact]
    public async Task SessionEntries_JoinTheExportWithItsPlayAndItsResolution()
    {
        var e = await CreateAsync();
        await using var _ = e.Db;
        var session = await e.Db.Sessions.StartAsync(T0, "Traktor", null, false);
        var ids = await e.Db.Events.AddAsync(
        [
            new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = "Exported", OccurredUtc = T0, Source = PlaySources.DragExport, EvidenceJson = "{\"effect\":\"Copy\"}" },
            new TrackEvent { TrackId = e.TrackB, SessionId = session, EventType = "Exported", OccurredUtc = T0.AddSeconds(5), Source = PlaySources.DragExport },
            new TrackEvent { TrackId = e.TrackA, SessionId = session, EventType = "Exported", OccurredUtc = T0.AddSeconds(60), Source = PlaySources.DragExport },
        ]);
        // 1st export of A turned into a Possible play; B was interrupted by a search; 2nd export of A is still pending.
        await e.Db.Plays.AddAsync(Play(e.TrackA, T0, PlayStatus.Possible, key: $"evt:{ids[0]}", session: session, related: ids[0]));
        await e.Db.Events.AddAsync([new TrackEvent
        {
            TrackId = e.TrackB, SessionId = session, EventType = "ActivityInterrupted", OccurredUtc = T0.AddSeconds(12), Source = PlaySources.DragExport,
            EvidenceJson = $"{{\"anchorEventId\":{ids[1]},\"activity\":\"Search\",\"atSeconds\":7}}"
        }]);

        var entries = await e.Db.Plays.GetSessionEntriesAsync(session);

        Assert.Equal([ids[2], ids[1], ids[0]], entries.Select(x => x.AnchorEventId)); // newest export first
        var pending = entries[0];
        Assert.Equal((e.TrackA, "Alpha", null, null), (pending.TrackId, pending.Title, pending.PlayStatus, pending.ResolutionEventType));
        var interrupted = entries[1];
        Assert.Equal("b", interrupted.Title);                                          // untitled track falls back to the file name
        Assert.Equal("ActivityInterrupted", interrupted.ResolutionEventType);
        Assert.Contains("\"activity\":\"Search\"", interrupted.ResolutionEvidenceJson);
        Assert.Null(interrupted.PlayStatus);
        var possible = entries[2];
        Assert.Equal(("Possible", PlaySources.InactivityHeuristic), (possible.PlayStatus, possible.PlaySource));
        Assert.Equal("{\"effect\":\"Copy\"}", possible.ExportEvidenceJson);
        Assert.Equal(DateTimeKind.Utc, possible.ExportedUtc.Kind);
    }

    // ===== migration =====

    [Fact]
    public async Task Migration0004_UpgradesAnM2Database_AndBackfillsStatistics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-dj-upgrade-" + Guid.NewGuid().ToString("N"));
        SqliteConnectionFactory? factory = null;
        try
        {
            factory = new SqliteConnectionFactory(Path.Combine(dir, "m2.db"));
            new MigrationRunner(factory).Migrate(maxVersion: 3); // an M2 database

            await using (var writes = new SqliteWriteQueue(factory))
            {
                await writes.EnqueueAsync((c, t) =>
                {
                    c.Execute("INSERT INTO LibraryRoots(Path) VALUES ('C:\\Music')", transaction: t);
                    c.Execute("INSERT INTO Folders(RootId, FullPath, Name) VALUES (1, 'C:\\Music', 'Music')", transaction: t);
                    c.Execute("""
                        INSERT INTO Tracks(FolderId, FullPath, FileName, Extension, FileSize, ModifiedUtc, IndexedUtc)
                        VALUES (1, 'C:\Music\a.mp3', 'a.mp3', '.mp3', 1, '2025-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z')
                        """, transaction: t);
                    c.Execute("INSERT INTO PlayHistory(TrackId, PlayedUtc, Status, Source) VALUES (1, '2026-10-01T20:00:00.0000000Z', 'Confirmed', 'User')", transaction: t);
                    c.Execute("INSERT INTO PlayHistory(TrackId, PlayedUtc, Status, Source) VALUES (1, '2026-10-02T20:00:00.0000000Z', 'Possible', 'Heuristic')", transaction: t);
                });
            }

            Assert.Equal(MigrationRunner.LatestVersion - 3, new MigrationRunner(factory).Migrate());

            using var conn = factory.Open();
            Assert.Equal((1, 1, "Possible"), conn.QuerySingle<(int, int, string)>(
                "SELECT ConfirmedCount, PossibleCount, LastStatus FROM TrackPlayStats WHERE TrackId = 1"));
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT AutoStarted FROM DjSessions LIMIT 1")); // column exists (no rows yet is fine)
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such column")) { throw; }
        finally
        {
            factory?.ReleasePool();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
