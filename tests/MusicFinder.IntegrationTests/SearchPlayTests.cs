using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.IntegrationTests;

/// <summary>Played filters (query and pill), hide-in-session, play-based sorts and per-row play statistics.</summary>
public class SearchPlayTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);

    private sealed class Env : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public SqliteSearchService Search { get; }
        public Dictionary<string, long> Ids { get; } = [];
        public long Session { get; private set; }
        public long OtherSession { get; private set; }

        public Env() => Search = new SqliteSearchService(Db.Factory);

        public static async Task<Env> CreateAsync()
        {
            var e = new Env();
            var root = await e.Db.Roots.AddAsync(@"C:\Music");
            var folder = await e.Db.Folders.UpsertAsync(root.Id, null, @"C:\Music", "Music");
            await e.Db.Tracks.UpsertBatchAsync(
            [
                TestDb.MakeTrack(folder, @"C:\Music\never.mp3", "Never", "X"),
                TestDb.MakeTrack(folder, @"C:\Music\poss.mp3", "Poss", "X"),
                TestDb.MakeTrack(folder, @"C:\Music\conf.mp3", "Conf", "X"),
                TestDb.MakeTrack(folder, @"C:\Music\old.mp3", "Old", "X"),
                TestDb.MakeTrack(folder, @"C:\Music\both.mp3", "Both", "X"),
            ]);
            foreach (var f in await e.Db.Tracks.GetIndexedFilesAsync(root.Id))
                e.Ids[Path.GetFileNameWithoutExtension(f.FullPath)] = f.Id;

            e.Session = await e.Db.Sessions.StartAsync(T0, null, "now", false);
            e.OtherSession = await e.Db.Sessions.StartAsync(T0.AddDays(-7), null, "last week", false);

            // Poss: possible in this session. Conf: confirmed in this session. Old: confirmed last week only.
            // Both: possible last week, confirmed in this session (two plays).
            await e.PlayAsync("poss", PlayStatus.Possible, T0.AddMinutes(1), e.Session);
            await e.PlayAsync("conf", PlayStatus.Confirmed, T0.AddMinutes(2), e.Session);
            await e.PlayAsync("old", PlayStatus.Confirmed, T0.AddDays(-7), e.OtherSession);
            await e.PlayAsync("both", PlayStatus.Possible, T0.AddDays(-7), e.OtherSession);
            await e.PlayAsync("both", PlayStatus.Confirmed, T0.AddMinutes(3), e.Session);
            return e;
        }

        public Task<long?> PlayAsync(string track, PlayStatus status, DateTime at, long session) =>
            Db.Plays.AddAsync(new PlayRecord
            {
                TrackId = Ids[track], PlayedUtc = at, Status = status.ToString(), Source = PlaySources.User,
                SessionId = session, ExternalEventKey = $"t:{track}:{at.Ticks}"
            });

        public async Task<List<string>> TitlesAsync(SearchQuery q) =>
            (await Search.SearchAsync(q, default)).Items.Select(i => i.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Theory]
    [InlineData("played:never", new[] { "Never" })]
    [InlineData("played:possible", new[] { "Both", "Poss" })]
    [InlineData("played:confirmed", new[] { "Both", "Conf", "Old" })]
    public async Task PlayedQueryFilters_UseTheMaintainedStatistics(string text, string[] expected)
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(expected, await e.TitlesAsync(new SearchQuery { Text = text, SessionId = e.Session }));
        Assert.Equal(expected.Length, await e.Search.CountAsync(new SearchQuery { Text = text, SessionId = e.Session }, default));
    }

    [Fact]
    public async Task PlayedSession_MeansPlayedInTheLiveSession_AndNothingWithoutOne()
    {
        await using var e = await Env.CreateAsync();

        Assert.Equal(["Both", "Conf", "Poss"], await e.TitlesAsync(new SearchQuery { Text = "played:session", SessionId = e.Session }));
        Assert.Equal(["Both", "Old"], await e.TitlesAsync(new SearchQuery { Text = "played:session", SessionId = e.OtherSession }));
        Assert.Empty(await e.TitlesAsync(new SearchQuery { Text = "played:session" }));
        Assert.Equal(0, await e.Search.CountAsync(new SearchQuery { Text = "played:session" }, default));
    }

    [Fact]
    public async Task ThePlayedPill_CombinesWithTextAndOtherFilters()
    {
        await using var e = await Env.CreateAsync();
        var pill = new SearchFilters { Played = PlayedFilter.Never };

        Assert.Equal(["Never"], await e.TitlesAsync(new SearchQuery { Filters = pill }));
        Assert.Empty(await e.TitlesAsync(new SearchQuery { Text = "Poss", Filters = pill }));
        // Pill AND query filter: confirmed (query) but not never (pill) => nothing.
        Assert.Empty(await e.TitlesAsync(new SearchQuery { Text = "played:confirmed", Filters = pill }));
    }

    [Fact]
    public async Task HideConfirmedAndPossible_ApplyToTheLiveSessionOnly()
    {
        await using var e = await Env.CreateAsync();

        var hideConfirmed = new SearchQuery { SessionId = e.Session, Filters = new SearchFilters { HideConfirmedInSession = true } };
        Assert.Equal(["Never", "Old", "Poss"], await e.TitlesAsync(hideConfirmed));       // Old was confirmed last week: still shown

        var hidePossible = new SearchQuery { SessionId = e.Session, Filters = new SearchFilters { HidePossibleInSession = true } };
        Assert.Equal(["Both", "Conf", "Never", "Old"], await e.TitlesAsync(hidePossible)); // Both's possible play was last week

        var both = hideConfirmed with { Filters = new SearchFilters { HideConfirmedInSession = true, HidePossibleInSession = true } };
        Assert.Equal(["Never", "Old"], await e.TitlesAsync(both));
        Assert.Equal(2, await e.Search.CountAsync(both, default));

        // No live session: the toggles have nothing to compare against.
        var noSession = both with { SessionId = null };
        Assert.Equal(5, (await e.TitlesAsync(noSession)).Count);
    }

    [Fact]
    public async Task Rows_CarryTheirPlayStatistics()
    {
        await using var e = await Env.CreateAsync();

        var rows = (await e.Search.SearchAsync(new SearchQuery { PageSize = 50 }, default)).Items.ToDictionary(r => r.Title);

        Assert.Equal((0, 0, null), (rows["Never"].ConfirmedCount, rows["Never"].PossibleCount, rows["Never"].LastPlayedUtc));
        var both = rows["Both"];
        Assert.Equal((1, 1, 2), (both.ConfirmedCount, both.PossibleCount, both.PlayCount));
        Assert.Equal(T0.AddMinutes(3), both.LastConfirmedUtc);
        Assert.Equal(T0.AddDays(-7), both.LastPossibleUtc);
        Assert.Equal(T0.AddMinutes(3), both.LastPlayedUtc);
        Assert.Equal(DateTimeKind.Utc, both.LastPlayedUtc!.Value.Kind);
    }

    [Fact]
    public async Task Rows_ReportTheirStatusInTheLiveSession()
    {
        await using var e = await Env.CreateAsync();
        await e.Db.Events.AddAsync([new TrackEvent
        {
            TrackId = e.Ids["never"], SessionId = e.Session, EventType = "Exported", OccurredUtc = T0, Source = PlaySources.DragExport
        }]);

        var rows = (await e.Search.SearchAsync(new SearchQuery { SessionId = e.Session }, default)).Items.ToDictionary(r => r.Title);

        Assert.Equal(SessionPlayStatus.Exported, rows["Never"].SessionStatus);
        Assert.Equal(SessionPlayStatus.Possible, rows["Poss"].SessionStatus);
        Assert.Equal(SessionPlayStatus.Confirmed, rows["Conf"].SessionStatus);
        Assert.Equal(SessionPlayStatus.Confirmed, rows["Both"].SessionStatus);          // strongest evidence wins
        Assert.Equal(SessionPlayStatus.None, rows["Old"].SessionStatus);                 // played, but in another session

        var fresh = await e.Search.GetRowsAsync(new[] { e.Ids["poss"] }, e.Session, default);
        Assert.Equal(SessionPlayStatus.Possible, fresh.Single().SessionStatus);
        var without = await e.Search.GetRowsAsync(new[] { e.Ids["poss"] }, null, default);
        Assert.Equal(SessionPlayStatus.None, without.Single().SessionStatus);
    }

    [Fact]
    public async Task SortingByLastPlayed_PutsNeverPlayedLast_InBothDirections()
    {
        await using var e = await Env.CreateAsync();

        async Task<List<string>> Order(SortField s, SortDirection d) =>
            (await e.Search.SearchAsync(new SearchQuery { Sort = s, Direction = d }, default)).Items.Select(i => i.Title).ToList();

        Assert.Equal(["Both", "Conf", "Poss", "Old", "Never"], await Order(SortField.LastPlayed, SortDirection.Desc));
        Assert.Equal(["Old", "Poss", "Conf", "Both", "Never"], await Order(SortField.LastPlayed, SortDirection.Asc));
        // Tracks without the value come last, in id order.
        Assert.Equal(["Both", "Conf", "Old", "Never", "Poss"], await Order(SortField.LastConfirmed, SortDirection.Desc));
        Assert.Equal(["Poss", "Both", "Never", "Conf", "Old"], await Order(SortField.LastPossible, SortDirection.Desc));
    }

    [Fact]
    public async Task SortingByPlayCount_UsesTheTotal_WithStableTies()
    {
        await using var e = await Env.CreateAsync();

        var order = (await e.Search.SearchAsync(new SearchQuery { Sort = SortField.PlayCount, Direction = SortDirection.Desc }, default))
            .Items.Select(i => i.Title).ToList();

        Assert.Equal("Both", order[0]);                                                   // 2 plays
        Assert.Equal("Never", order[^1]);                                                 // 0 plays
        Assert.Equal(new[] { "Poss", "Conf", "Old" }.OrderBy(x => e.Ids[x.ToLowerInvariant()]).ToList(), order.Skip(1).Take(3).ToList());
    }

    [Theory]
    [InlineData(SortField.LastPlayed, SortDirection.Desc)]
    [InlineData(SortField.LastPlayed, SortDirection.Asc)]
    [InlineData(SortField.LastConfirmed, SortDirection.Desc)]
    [InlineData(SortField.LastPossible, SortDirection.Asc)]
    [InlineData(SortField.PlayCount, SortDirection.Desc)]
    [InlineData(SortField.PlayCount, SortDirection.Asc)]
    public async Task KeysetPaging_WalksEveryRowExactlyOnce_ForPlaySorts(SortField sort, SortDirection dir)
    {
        await using var e = await Env.CreateAsync();
        var seen = new List<long>();
        SearchCursor? cursor = null;
        do
        {
            var page = await e.Search.SearchAsync(new SearchQuery { Sort = sort, Direction = dir, PageSize = 2, After = cursor }, default);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task APlaySupersededByAnImporter_NoLongerCounts()
    {
        await using var e = await Env.CreateAsync();
        var possible = (await e.Db.Plays.GetForTrackAsync(e.Ids["poss"], 10)).Single();
        var confirmed = await e.Db.Plays.AddAsync(new PlayRecord
        {
            TrackId = e.Ids["poss"], PlayedUtc = T0.AddMinutes(1), Status = "Confirmed", Source = PlaySources.Traktor,
            SessionId = e.Session, ExternalEventKey = "traktor:42"
        });

        await e.Db.Plays.MarkSupersededAsync(possible.Id, confirmed!.Value);

        var row = (await e.Search.GetRowsAsync(new[] { e.Ids["poss"] }, e.Session, default)).Single();
        Assert.Equal((1, 0), (row.ConfirmedCount, row.PossibleCount));
        Assert.Equal(["Both", "Conf", "Old", "Poss"], await e.TitlesAsync(new SearchQuery { Text = "played:confirmed" }));
    }
}
