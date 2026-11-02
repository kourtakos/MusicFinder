using Dapper;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.UiSmokeTests;

/// <summary>The Library Health tab on a real library: counts, "Show in Library", and the quality details of a track.</summary>
public class HealthTabTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "A", "Disco"),
        ("b.mp3", "Bravo", "B", "Funk"),
        ("c.mp3", "Charlie", "C", "House"),
    ];

    private static async Task<UiTestApp> StartAsync()
    {
        var app = await UiTestApp.CreateWithFilesAsync(null, Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "indexed rows");
        return app;
    }

    private static Task Sql(UiTestApp app, string sql) =>
        app.Service<SqliteWriteQueue>().EnqueueAsync((conn, tx) => conn.Execute(sql, transaction: tx));

    private static HealthCardViewModel Card(UiTestApp app, HealthIssueKind kind) =>
        app.Main.Health.Groups.SelectMany(g => g.Cards).Single(c => c.Kind == kind);

    [Fact]
    public Task TheTab_ShowsCountsFromTheLibrary_AndHasNoBrokenBindings() => Ui.RunAsync(async () =>
    {
        await using var app = await StartAsync();
        await Sql(app, "UPDATE Tracks SET IssueFlags = 1, BitrateKbps = 96 WHERE Title = 'Alpha'");

        app.Main.SelectedTab = AppTab.Health;
        await Ui.WaitUntilAsync(() => Card(app, HealthIssueKind.LowBitrate).Count == 1, what: "health counts");
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<HealthView>(app.Window) is not null, what: "health view");
        await Task.Delay(250);

        Assert.Equal(1, Card(app, HealthIssueKind.LowBitrate).Count);
        Assert.Equal(0, Card(app, HealthIssueKind.Unreadable).Count);
        Assert.Contains("3 tracks", app.Main.Health.SummaryText);
        Assert.Empty(BindingAudit.FindBroken(app.Window));
    });

    [Fact]
    public Task ShowInLibrary_OpensTheLibraryListingExactlyThoseTracks() => Ui.RunAsync(async () =>
    {
        await using var app = await StartAsync();
        await Sql(app, "UPDATE Tracks SET IssueFlags = 1 WHERE Title IN ('Alpha', 'Charlie')");
        app.Main.SelectedTab = AppTab.Health;
        await Ui.WaitUntilAsync(() => Card(app, HealthIssueKind.LowBitrate).Count == 2, what: "health counts");

        Card(app, HealthIssueKind.LowBitrate).ShowCommand.Execute(null);

        Assert.Equal(AppTab.Library, app.Main.SelectedTab);
        Assert.Equal("issue:lowbitrate", app.Main.Library.Search.QueryText);
        Assert.True(app.Main.Library.Tree.GlobalSearch);
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 2, what: "filtered rows");
        Assert.Equal(["Alpha", "Charlie"], app.Main.Library.Search.Results.Select(r => r.Title).OrderBy(t => t));
    });

    [Fact]
    public Task ACardWithNothingToShow_CannotBeOpened_AndFileListsHaveNoSearch() => Ui.RunAsync(async () =>
    {
        await using var app = await StartAsync();
        app.Main.SelectedTab = AppTab.Health;
        await Ui.WaitUntilAsync(() => app.Main.Health.SummaryText.Contains("3 tracks"), what: "health counts");

        Assert.False(Card(app, HealthIssueKind.Unreadable).ShowCommand.CanExecute(null));
        Assert.False(Card(app, HealthIssueKind.MissingFiles).CanShow);
    });

    [Fact]
    public Task ATracksQualityAndSuspicions_AreInItsDetails() => Ui.RunAsync(async () =>
    {
        await using var app = await StartAsync();
        await Sql(app, "UPDATE Tracks SET IssueFlags = 5, Codec = 'MPEG Version 1 Audio, Layer 3', SampleRateHz = 44100, Channels = 2 WHERE Title = 'Alpha'");
        await app.Main.Library.Search.RefreshAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "rows");

        var row = app.Main.Library.Search.Results.Single(r => r.Title == "Alpha");

        Assert.Equal("MPEG Version 1 Audio, Layer 3 · 44.1 kHz · stereo", row.QualityText);
        Assert.True(row.HasIssues);
        Assert.Equal("Low bitrate; Audio is not the kind the extension suggests", row.IssueText);
        Assert.False(app.Main.Library.Search.Results.Single(r => r.Title == "Bravo").HasIssues);
    });
}
