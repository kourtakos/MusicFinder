using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>Play status in the library: columns, the Played dropdown, manual marking and the drag heuristic's badges.</summary>
public class PlayStatusTests
{
    private sealed record Ctx(UiTestApp App, DataGrid Grid, FakeTimeProvider Clock)
    {
        public SearchViewModel Search => App.Main.Library.Search;
        public ISessionService Session => App.Service<ISessionService>();
        public TrackRowViewModel Row(string title) => Search.Results.Single(r => r.Title == title);
    }

    private static async Task<Ctx> StartAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await UiTestApp.CreateAsync(configure: s => s.AddSingleton<TimeProvider>(clock));
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        await Task.Delay(150);
        var grid = Ui.FindDescendant<DataGrid>(app.Window) ?? throw new InvalidOperationException("TrackGrid not found");
        return new Ctx(app, grid, clock);
    }

    private static async Task DragAsync(Ctx c, params string[] titles)
    {
        var now = c.Clock.GetUtcNow().UtcDateTime;
        await c.Session.RecordDragAsync(new DragRecord(titles.Select(t => c.Row(t).Id).ToList(), now.AddSeconds(-1), now, "Copy", "Traktor"), default);
    }

    [Fact]
    public Task NothingPlayedYet_ShowsNotPlayed_AndZeroPlays() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.All(c.Search.Results, r => Assert.Equal(("Not played", 0, false), (r.LastPlayedText, r.Plays, r.HasSessionBadge)));
        Assert.Equal(6, c.Search.PlayedOptions.Count);
    });

    [Fact]
    public Task MarkAsPlayed_UpdatesTheRowInPlace_AndTheHistoryList() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var row = c.Row("Le Freak");
        c.Grid.SelectedItem = row;
        await Ui.WaitUntilAsync(() => c.Search.SelectedTrack == row, what: "selection");

        c.Search.MarkPlayedCommand.Execute(null);

        await Ui.WaitUntilAsync(() => row.Plays == 1, what: "row refreshed");
        Assert.Equal("just now", row.LastPlayedText);
        Assert.Equal("1 confirmed, 0 possible", row.PlayDetailText);
        Assert.Same(row, c.Grid.SelectedItem);                                               // selection survives
        await Ui.WaitUntilAsync(() => c.Search.SelectedPlays.Count == 1, what: "play history");
        Assert.Equal("Confirmed (manual)", c.Search.SelectedPlays[0].Label);
        Assert.True(c.Search.CanUndoPlay);

        c.Search.MarkNotPlayedCommand.Execute(null);
        await Ui.WaitUntilAsync(() => row.Plays == 0, what: "play removed");
        Assert.Equal("Not played", row.LastPlayedText);
        await Ui.WaitUntilAsync(() => c.Search.SelectedPlays.Count == 0, what: "history cleared");

        c.Search.UndoPlayCommand.Execute(null);
        await Ui.WaitUntilAsync(() => row.Plays == 1, what: "undo restored the play");
    });

    [Fact]
    public Task PlayedDropdown_FiltersByStatus_AndOptionsAreExclusive() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.Session.MarkPlayedAsync(c.Row("Le Freak").Id, null, default);
        await Ui.WaitUntilAsync(() => c.Row("Le Freak").Plays == 1, what: "play recorded");

        var never = c.Search.PlayedOptions.Single(o => o.Label == "Never played");
        var confirmed = c.Search.PlayedOptions.Single(o => o.Label == "Confirmed played");

        never.IsSelected = true;
        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 2, what: "never-played rows");
        Assert.DoesNotContain(c.Search.Results, r => r.Title == "Le Freak");
        Assert.True(c.Search.HasActiveFilters);

        confirmed.IsSelected = true;                                                          // replaces "Never played"
        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 1, what: "confirmed rows");
        Assert.False(never.IsSelected);
        Assert.Equal("Le Freak", c.Search.Results[0].Title);

        c.Search.ResetCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 3, what: "reset");
        Assert.False(c.Search.HasActiveFilters);
    });

    [Fact]
    public Task SortingByLastPlayed_PutsThePlayedTrackFirstWhenDescending() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.Session.MarkPlayedAsync(c.Row("Good Times").Id, null, default);
        await Ui.WaitUntilAsync(() => c.Row("Good Times").Plays == 1, what: "play recorded");

        c.Search.SortByCommand.Execute(SortField.LastPlayed);   // ascending first: never-played last
        await Ui.WaitUntilAsync(() => c.Search.SortField == SortField.LastPlayed && c.Search.Results[0].Title == "Good Times", what: "sorted");
        Assert.Equal("Good Times", c.Search.Results[0].Title);

        c.Search.SortByCommand.Execute(SortField.PlayCount);
        c.Search.SortByCommand.Execute(SortField.PlayCount);    // second click: descending
        await Ui.WaitUntilAsync(() => c.Search.SortDirection == SortDirection.Desc && c.Search.Results[0].Title == "Good Times", what: "sorted by plays");
    });

    [Fact]
    public Task ADrag_ShowsAnExportedBadge_ThenPossibleAfterQuiet_NeverPlayed() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var row = c.Row("Le Freak");

        await DragAsync(c, "Le Freak");
        await Ui.WaitUntilAsync(() => c.Session.Active is not null, what: "auto-started session");

        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(20)));
        await Ui.WaitUntilAsync(() => row.SessionBadge == "Possible", what: "possible badge");
        Assert.Equal("0 confirmed, 1 possible", row.PlayDetailText);
        Assert.False(row.IsSessionConfirmed);
    });

    [Fact]
    public Task TypingAfterADrag_KeepsItExported_AndNeverCreatesAPlay() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var row = c.Row("Le Freak");
        await DragAsync(c, "Le Freak");

        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(8)));
        c.Search.QueryText = "freak";                                                         // the user searches: activity
        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 1, what: "search");
        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(60)));
        await Task.Delay(100);

        Assert.Equal(0, c.Search.Results.Single().Plays);
        Assert.Equal("Exported", c.Search.Results.Single().SessionBadge);
        Assert.Equal(row.Id, c.Search.Results.Single().Id);
    });

    [Fact]
    public Task HidePossibleInSession_RemovesTheTrackOnceItBecomesPossible() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.Session.StartAsync("Traktor", default);
        c.Search.PlayedOptions.Single(o => o.Label == "Hide possible in session").IsSelected = true;
        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 3, what: "still all three");

        await DragAsync(c, "Le Freak");
        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(20)));

        await Ui.WaitUntilAsync(() => c.Search.Results.Count == 2, what: "possible track hidden");
        Assert.DoesNotContain(c.Search.Results, r => r.Title == "Le Freak");
    });
}
