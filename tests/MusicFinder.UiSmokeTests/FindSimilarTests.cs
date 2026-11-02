using System.Windows;
using System.Windows.Controls;
using MusicFinder.App.ViewModels;

namespace MusicFinder.UiSmokeTests;

/// <summary>Find Similar through the real window: the list, why each track is in it, and the ways in and out.</summary>
public class FindSimilarTests
{
    // UiTestApp's seeded library: Le Freak (Chic, Disco+Funk, 120 BPM, 8A), Good Times (Chic, Disco, 119, 8A), and a Greek track (100 BPM, 8A).
    private static async Task<(UiTestApp App, DataGrid Grid)> ShowAsync()
    {
        var app = await UiTestApp.CreateAsync();
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        await Task.Delay(150);
        var grid = Ui.FindDescendant<DataGrid>(app.Window) ?? throw new InvalidOperationException("TrackGrid not found");
        return (app, grid);
    }

    private static TrackRowViewModel Row(SearchViewModel s, string title) => s.Results.Single(r => r.Title == title);

    private static async Task SelectAsync(UiTestApp app, DataGrid grid, string title)
    {
        var row = Row(app.Main.Library.Search, title);
        grid.SelectedItem = row;
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.SelectedTrack == row, what: "selection");
    }

    [Fact]
    public Task FindSimilar_ListsTheBestMatchFirst_WithItsReasons() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        await SelectAsync(app, grid, "Le Freak");

        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.IsSimilarMode && search.Results.Any(r => r.Similar is not null), what: "similar list");

        Assert.Equal("Le Freak — Chic", search.SimilarTitle);
        Assert.DoesNotContain(search.Results, r => r.Title == "Le Freak");                    // never itself
        var best = search.Results[0];
        Assert.Equal("Good Times", best.Title);
        Assert.True(int.Parse(best.MatchText) >= 90);
        Assert.Contains("same genre (Disco)", best.WhyText);
        Assert.Contains("BPM -1", best.WhyText);
        Assert.Contains("same key (8A)", best.WhyText);
        Assert.Equal("1 similar track", search.ResultsText);                                  // the Greek track is far in tempo and in genre family: not a candidate
        Assert.False(search.HasMore);
    });

    [Fact]
    public Task TheMatchAndWhyColumns_AppearOnlyInSimilarMode() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        var match = grid.Columns.Single(c => c.Header as string == "Match");
        var why = grid.Columns.Single(c => c.Header as string == "Why");
        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed), (match.Visibility, why.Visibility));

        await SelectAsync(app, grid, "Le Freak");
        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => match.Visibility == Visibility.Visible, what: "columns shown");
        Assert.Equal(Visibility.Visible, why.Visibility);

        search.CloseSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => match.Visibility == Visibility.Collapsed && search.Results.Count == 3, what: "back to the library");
        Assert.Equal(Visibility.Collapsed, why.Visibility);
        Assert.False(search.IsSimilarMode);
    });

    [Fact]
    public Task Typing_LeavesTheSimilarList_AndSearchesAsUsual() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        await SelectAsync(app, grid, "Le Freak");
        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.IsSimilarMode && search.Results.Count == 1, what: "similar list");

        search.QueryText = "freak";

        await Ui.WaitUntilAsync(() => !search.IsSimilarMode && search.Results.Count == 1 && search.Results[0].Title == "Le Freak", what: "normal search");
        Assert.Equal("Le Freak", search.Results[0].Title);
        Assert.Equal("", search.Results[0].MatchText);
    });

    [Fact]
    public Task AFilter_OrASortClick_AlsoLeavesTheSimilarList() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        await SelectAsync(app, grid, "Le Freak");
        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.IsSimilarMode, what: "similar list");

        search.SortByCommand.Execute(MusicFinder.Domain.Enums.SortField.Title);

        await Ui.WaitUntilAsync(() => !search.IsSimilarMode && search.Results.Count == 3, what: "normal list");
    });

    [Fact]
    public Task WithoutExactlyOneSelectedTrack_ItAsksForOne() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;

        search.FindSimilarCommand.Execute(null);
        Assert.Equal("Select one track to find tracks similar to it.", search.Warning);
        Assert.False(search.IsSimilarMode);

        grid.SelectedItems.Add(grid.Items[0]);
        grid.SelectedItems.Add(grid.Items[1]);
        await Ui.WaitUntilAsync(() => search.SelectedCount == 2, what: "two selected");
        search.FindSimilarCommand.Execute(null);
        Assert.False(search.IsSimilarMode);
    });

    [Fact]
    public Task ATrackWithTooLittleData_ExplainsInsteadOfGuessing() => Ui.RunAsync(async () =>
    {
        // Real files tagged with a genre only: no BPM and no key, so they cannot be compared.
        var app = await UiTestApp.CreateWithFilesAsync(null, ("a.mp3", "Alpha", "A", "Disco"), ("b.mp3", "Bravo", "B", "Disco"));
        await using var _ = app;
        app.Window.Show();
        await app.Main.InitializeAsync();
        var search = app.Main.Library.Search;
        await Ui.WaitUntilAsync(() => search.Results.Count == 2, what: "indexed rows");
        search.SetSelectedItems([search.Results.Single(r => r.Title == "Alpha")]);

        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.SimilarMessage is not null, what: "message");

        Assert.Empty(search.Results);
        Assert.Contains("at least two of genre, BPM and key", search.SimilarMessage);
        Assert.Contains("does not guess", search.SimilarMessage);
        Assert.Equal("0 similar tracks", search.ResultsText);
    });

    [Fact]
    public Task TheOptions_AreRemembered_AndRerunTheList() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        await SelectAsync(app, grid, "Le Freak");
        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.IsSimilarMode && search.Results.Count == 1, what: "similar list");
        Assert.Equal(8, search.SimilarBpmWindow);
        Assert.True(search.SimilarHalfDouble);

        search.SimilarBpmWindow = 3;
        search.SimilarHalfDouble = false;
        search.SimilarHideRecentlyPlayed = true;

        var saved = app.Settings.Load();
        Assert.Equal((3d, false, true), (saved.SimilarBpmWindow, saved.SimilarHalfDoubleTempo, saved.SimilarHideRecentlyPlayed));
        await Ui.WaitUntilAsync(() => search.IsSimilarMode && search.Results.Count > 0, what: "list rerun with the new options");
    });

    [Fact]
    public Task TheBannerAndColumns_HaveNoBrokenBindings() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        await SelectAsync(app, grid, "Le Freak");
        search.FindSimilarCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.IsSimilarMode && search.Results.Count == 1, what: "similar list");
        grid.SelectedIndex = 0;                                   // the details panel only binds once a track is selected
        await Ui.WaitUntilAsync(() => search.SelectedTrack is not null, what: "a selected result");
        await Task.Delay(300);

        var broken = BindingAudit.FindBroken(app.Window);
        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    });
}
