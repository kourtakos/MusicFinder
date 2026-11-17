using System.Windows.Controls;
using MusicFinder.App.ViewModels;

namespace MusicFinder.UiSmokeTests;

/// <summary>Crates through the real window: make one from the selection, open it, take a track out, close it.</summary>
public class CratesTests
{
    // UiTestApp's seeded library: Le Freak, Good Times and a Greek track.
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

    private static async Task SelectAsync(UiTestApp app, DataGrid grid, params string[] titles)
    {
        var search = app.Main.Library.Search;
        grid.SelectedItems.Clear();
        foreach (var t in titles) grid.SelectedItems.Add(search.Results.Single(r => r.Title == t));
        await Ui.WaitUntilAsync(() => search.SelectedCount == titles.Length, what: "selection");
    }

    [Fact]
    public Task ANewCrate_TakesTheSelection_OpensInItsOwnOrder_AndTracksCanBeRemoved() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var library = app.Main.Library;
        var search = library.Search;
        await SelectAsync(app, grid, "Le Freak", "Good Times");

        library.Crates.NewName = "Warm Up";
        library.Crates.CreateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => library.Crates.Items.Count == 1 && library.Crates.Items[0].TrackCount == 2, what: "crate with 2 tracks");
        Assert.Equal("", library.Crates.NewName);

        await library.Crates.Items[0].OpenCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => search.IsCrateMode && search.Results.Count == 2, what: "crate open");
        Assert.Equal("Warm Up", search.CrateName);
        Assert.Equal("2 tracks in the crate", search.ResultsText);
        Assert.DoesNotContain(search.Results, r => r.Title != "Le Freak" && r.Title != "Good Times");

        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(search.Results.Single(r => r.Title == "Le Freak"));
        await Ui.WaitUntilAsync(() => search.SelectedCount == 1, what: "selection in crate");
        search.RemoveFromCrateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => search.Results.Count == 1 && library.Crates.Items[0].TrackCount == 1, what: "track removed");
        Assert.Equal("Good Times", search.Results[0].Title);

        search.CloseCrateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => !search.IsCrateMode && search.Results.Count == 3, what: "back to the library");
    });

    [Fact]
    public Task ADuplicateCrateNameIsRefusedWithAMessage_AndAddingTwiceSkipsTheRepeat() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var library = app.Main.Library;
        await SelectAsync(app, grid, "Le Freak");

        library.Crates.NewName = "Closing";
        library.Crates.CreateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => library.Crates.Items.Count == 1, what: "first crate");

        library.Crates.NewName = "closing";
        library.Crates.CreateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => library.Crates.Message is not null, what: "duplicate message");
        Assert.Single(library.Crates.Items);

        await library.Crates.Items[0].AddSelectedCommand.ExecuteAsync(null);   // Le Freak is already in it
        Assert.Equal(1, library.Crates.Items[0].TrackCount);
        Assert.Contains("already there", library.Search.Warning);
    });

    [Fact]
    public Task DeletingACrate_KeepsTheTracks() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var library = app.Main.Library;
        await SelectAsync(app, grid, "Le Freak");
        library.Crates.NewName = "Temp";
        library.Crates.CreateCommand.Execute(null);
        await Ui.WaitUntilAsync(() => library.Crates.Items.Count == 1, what: "crate");

        await library.Crates.Items[0].DeleteCommand.ExecuteAsync(null);

        Assert.Empty(library.Crates.Items);
        Assert.Equal(3, library.Search.Results.Count);
    });
}
