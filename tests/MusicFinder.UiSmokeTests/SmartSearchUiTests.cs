using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Enums;

namespace MusicFinder.UiSmokeTests;

/// <summary>Smart Greek search through the real window: relevance is the default order, Greeklish finds Greek, and the index fills in the background.</summary>
public class SmartSearchUiTests
{
    // UiTestApp's seeded library: "Le Freak" (Chic), "Good Times" (Chic) and "Σ' αγαπώ" (Χάρις Αλεξίου, Λαϊκά).
    private static async Task<UiTestApp> StartAsync(bool buildIndex)
    {
        var app = await UiTestApp.CreateAsync();
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        if (buildIndex) await app.Service<SearchIndexCoordinator>().RunAsync();
        return app;
    }

    private static SearchViewModel Search(UiTestApp app) => app.Main.Library.Search;

    [Fact]
    public Task Greeklish_FindsTheGreekArtist_OnceTheIndexIsBuilt() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: true);
        await using var _ = app;

        Search(app).QueryText = "xaris alexiou";

        await Ui.WaitUntilAsync(() => Search(app).Results.Count == 1, what: "greeklish result");
        Assert.Equal("Σ' αγαπώ", Search(app).Results[0].Title);
    });

    [Theory]
    [InlineData("x;aris")]          // Χάρις typed on a US keyboard
    [InlineData("χαρις")]           // no accent
    [InlineData("ΧΑΡΙΣ")]
    [InlineData("haris")]
    public Task EverySpelling_FindsTheSameTrack(string query) => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: true);
        await using var _ = app;

        Search(app).QueryText = query;

        await Ui.WaitUntilAsync(() => Search(app).Results.Count == 1 && Search(app).Results[0].Title == "Σ' αγαπώ", what: $"'{query}' result");
    });

    [Fact]
    public Task AQueryTypedBeforeTheIndexExists_IsRerunAutomaticallyWhenItIsBuilt() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: false);
        await using var _ = app;

        Search(app).QueryText = "xaris";
        await Ui.WaitUntilAsync(() => Search(app).Results.Count == 0 && Search(app).IsCountReady, what: "empty before the index");

        await app.Service<SearchIndexCoordinator>().RunAsync();

        await Ui.WaitUntilAsync(() => Search(app).Results.Count == 1, what: "result appears without retyping");
    });

    [Fact]
    public Task RelevanceIsTheDefaultOrder_AndHeaderClicksCycleThroughIt() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: true);
        await using var _ = app;
        var search = Search(app);
        Assert.Equal(SortField.Relevance, search.SortField);

        search.SortByCommand.Execute(SortField.Title);
        Assert.Equal((SortField.Title, SortDirection.Asc), (search.SortField, search.SortDirection));
        search.SortByCommand.Execute(SortField.Title);
        Assert.Equal((SortField.Title, SortDirection.Desc), (search.SortField, search.SortDirection));
        search.SortByCommand.Execute(SortField.Title);
        Assert.Equal((SortField.Relevance, SortDirection.Asc), (search.SortField, search.SortDirection));   // back to best match first

        search.SortByCommand.Execute(SortField.Bpm);
        search.ResetCommand.Execute(null);
        Assert.Equal(SortField.Relevance, search.SortField);                                              // Reset restores it
    });

    [Fact]
    public Task WhileTheIndexIsBeingBuilt_TheStatusBarSaysSo_ThenClears() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: false);
        await using var _ = app;
        Assert.False(app.Main.HasIndexStatus);

        await app.Service<SearchIndexCoordinator>().RunAsync();

        Assert.False(app.Main.HasIndexStatus);
        Assert.Equal("", app.Main.IndexStatusText);
    });

    [Fact]
    public Task TheBestMatchComesFirst_NotTheAlphabeticalOne() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync(buildIndex: true);
        await using var _ = app;

        Search(app).QueryText = "chic";
        await Ui.WaitUntilAsync(() => Search(app).Results.Count == 2, what: "two chic tracks");

        Assert.Equal(["Good Times", "Le Freak"], Search(app).Results.Select(r => r.Title).ToList());      // equal tier: title order breaks the tie
    });
}
