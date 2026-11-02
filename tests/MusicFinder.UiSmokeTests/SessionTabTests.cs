using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

/// <summary>The Session tab, the Live badge, the status bar summary and the DJ session settings.</summary>
public class SessionTabTests
{
    private sealed record Ctx(UiTestApp App, FakeTimeProvider Clock)
    {
        public SessionViewModel Tab => App.Main.Session;
        public SearchViewModel Search => App.Main.Library.Search;
        public ISessionService Service => App.Service<ISessionService>();
        public TrackRowViewModel Row(string title) => Search.Results.Single(r => r.Title == title);
        public async Task DragAsync(string title)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            await Service.RecordDragAsync(new DragRecord([Row(title).Id], now.AddSeconds(-1), now, "Copy", "Traktor"), default);
        }
    }

    private static async Task<Ctx> StartAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await UiTestApp.CreateAsync(configure: s => s.AddSingleton<TimeProvider>(clock));
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        return new Ctx(app, clock);
    }

    [Fact]
    public Task WithoutASession_TheTabInvitesYouToStartOne_AndNothingIsLive() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.False(c.Tab.IsLive);
        Assert.Equal("No session", c.Tab.StatusBarText);
        Assert.Empty(c.Tab.Entries);
        Assert.Equal(Visibility.Collapsed, LiveBadgeVisibility(c));
    });

    private static Visibility LiveBadgeVisibility(Ctx c) => c.Tab.IsLive ? Visibility.Visible : Visibility.Collapsed;

    [Fact]
    public Task StartingAndStopping_UpdatesTheTab_AndPicksTheSoftware() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Tab.EditSoftware = "Traktor";

        c.Tab.StartCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Tab.IsLive, what: "live");
        Assert.Equal("LIVE · Traktor", c.Tab.LiveBadgeText);
        Assert.StartsWith("Session · Traktor", c.Tab.StatusBarText);

        c.Tab.StopCommand.Execute(null);
        await Ui.WaitUntilAsync(() => !c.Tab.IsLive, what: "stopped");
        Assert.Equal("No session", c.Tab.StatusBarText);
    });

    [Fact]
    public Task ADrag_AppearsAsExported_ThenPossible_WithItsReason() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        await c.DragAsync("Le Freak");
        await Ui.WaitUntilAsync(() => c.Tab.Entries.Count == 1, what: "entry");
        var entry = c.Tab.Entries[0];
        Assert.Equal(("Le Freak", "Exported", "Copy → Traktor"), (entry.Title, entry.StatusText, entry.TargetText));
        Assert.True(c.Tab.IsLive);                                                              // the drag auto-started the session
        Assert.Contains("Waiting for 20 s", entry.Evidence);
        Assert.Equal("Session · 1 exported · 0 possible · 0 played", c.Tab.StatusBarText);

        await Task.Run(() => c.Clock.Advance(TimeSpan.FromSeconds(20)));
        await Ui.WaitUntilAsync(() => c.Tab.PossibleCount == 1, what: "possible");
        Assert.Equal("Possibly played", c.Tab.Entries[0].StatusText);
        Assert.Contains("weak evidence, not proof", c.Tab.Entries[0].Evidence);
        Assert.Equal(0, c.Tab.ConfirmedCount);
    });

    [Fact]
    public Task TheUserCanConfirmOrRejectAnEntry_AndUndo() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.DragAsync("Le Freak");
        await Ui.WaitUntilAsync(() => c.Tab.Entries.Count == 1, what: "entry");

        c.Tab.MarkPlayedCommand.Execute(c.Tab.Entries[0]);
        await Ui.WaitUntilAsync(() => c.Tab.ConfirmedCount == 1, what: "confirmed");
        Assert.Contains("Marked as played by you", c.Tab.Entries[0].Evidence);
        Assert.True(c.Tab.CanUndo);

        c.Tab.MarkNotPlayedCommand.Execute(c.Tab.Entries[0]);
        await Ui.WaitUntilAsync(() => c.Tab.ConfirmedCount == 0, what: "not played");
        Assert.Equal("Exported", c.Tab.Entries[0].StatusText);                                  // never "not played" as a status

        c.Tab.UndoCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Tab.ConfirmedCount == 1, what: "undone");
    });

    [Fact]
    public Task SavingTheDetails_RenamesTheLiveSession() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Tab.StartCommand.Execute(null);
        await Ui.WaitUntilAsync(() => c.Tab.IsLive, what: "live");

        c.Tab.EditName = "Friday at the club";
        c.Tab.EditSoftware = "VirtualDJ";
        c.Tab.SaveDetailsCommand.Execute(null);

        await Ui.WaitUntilAsync(() => c.Tab.Title == "Friday at the club", what: "renamed");
        Assert.Equal("LIVE · VirtualDJ", c.Tab.LiveBadgeText);
    });

    [Fact]
    public Task TheSettings_ClampTheQuietPeriod_AndRememberTheChoices() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var settings = c.App.Main.Settings;

        settings.SessionHeuristicSeconds = 99;
        Assert.Equal(20, settings.SessionHeuristicSeconds);
        Assert.Equal(20, c.App.Settings.Load().SessionHeuristicSeconds);
        settings.SessionHeuristicSeconds = 3;
        Assert.Equal(15, settings.SessionHeuristicSeconds);
        Assert.Equal(15, c.App.Settings.Load().SessionHeuristicSeconds);

        settings.AutoStartSessionOnDrag = false;
        Assert.False(c.App.Settings.Load().AutoStartSessionOnDrag);

        c.Search.PlayedOptions.Single(o => o.Label == "Hide confirmed in session").IsSelected = true;
        Assert.True(c.App.Settings.Load().HideConfirmedInSession);
    });

    [Fact]
    public Task WithAutoStartOff_ADragDoesNotCreateASession() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.App.Main.Settings.AutoStartSessionOnDrag = false;

        await c.DragAsync("Le Freak");
        await Task.Delay(150);

        Assert.False(c.Tab.IsLive);
        Assert.Null(c.Service.Active);
    });

    [Fact]
    public Task TheSessionTabAndHeader_HaveNoBrokenBindings_WithEntriesShown() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        await c.DragAsync("Le Freak");
        await c.DragAsync("Good Times");
        await Ui.WaitUntilAsync(() => c.Tab.Entries.Count == 2, what: "entries");

        c.App.Main.NavigateCommand.Execute(AppTab.Session);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<MusicFinder.App.Views.SessionView>(c.App.Window) is not null, what: "session view");
        await Task.Delay(250);

        Assert.Empty(BindingAudit.FindBroken(c.App.Window));
    });
}
