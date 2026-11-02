using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;

namespace MusicFinder.UiSmokeTests;

/// <summary>The Search panel in Settings: switches, the user's own aliases, and "Explain a search".</summary>
public class SearchSettingsTests
{
    private static async Task<UiTestApp> StartAsync()
    {
        var app = await UiTestApp.CreateAsync();
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        await app.Service<SearchIndexCoordinator>().RunAsync();
        return app;
    }

    [Fact]
    public Task TheSwitches_AreOnByDefault_AndRemembered() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;
        Assert.True(settings.SmartGreekSearch);
        Assert.True(settings.FuzzySearch);

        settings.SmartGreekSearch = false;
        settings.FuzzySearch = false;

        var saved = app.Settings.Load();
        Assert.False(saved.SmartGreekSearch);
        Assert.False(saved.FuzzySearch);
    });

    [Fact]
    public Task AddingAnAlias_ListsIt_AndMakesTheGreekSpellingFindTheTrack() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;
        var search = app.Main.Library.Search;

        settings.NewAliasSource = "Chic";
        settings.NewAliasAlias = "σικ";
        settings.AddSearchAliasCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.SearchAliases.Count == 1, what: "alias listed");

        Assert.Equal(("Chic", "σικ"), (settings.SearchAliases[0].Source, settings.SearchAliases[0].Alias));
        Assert.Equal(("", ""), (settings.NewAliasSource, settings.NewAliasAlias));      // the form is cleared
        Assert.Null(settings.AliasError);

        search.QueryText = "σικ";
        await Ui.WaitUntilAsync(() => search.Results.Count == 2, what: "both Chic tracks");
    });

    [Fact]
    public Task APoorAlias_ShowsAPlainMessage_AndAddsNothing() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;

        settings.NewAliasSource = "Chic";
        settings.NewAliasAlias = "CHIC";
        settings.AddSearchAliasCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.AliasError is not null, what: "message");

        Assert.Contains("same once case and accents", settings.AliasError);
        Assert.Empty(settings.SearchAliases);
        Assert.Equal("Chic", settings.NewAliasSource);                                  // what was typed stays, so it can be corrected
    });

    [Fact]
    public Task RemovingAnAlias_StopsTheMatch() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;
        settings.NewAliasSource = "Chic";
        settings.NewAliasAlias = "σικ";
        settings.AddSearchAliasCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.SearchAliases.Count == 1, what: "alias listed");

        settings.RemoveSearchAliasCommand.Execute(settings.SearchAliases[0]);
        await Ui.WaitUntilAsync(() => settings.SearchAliases.Count == 0, what: "alias removed");

        app.Main.Library.Search.QueryText = "σικ";
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.IsCountReady && app.Main.Library.Search.Results.Count == 0, what: "no match any more");
    });

    [Fact]
    public Task Explain_DescribesTheQuery_InPlainText() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;

        settings.ExplainQuery = "xaris alexiou";
        settings.ExplainSearchCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.ExplainOutput.Length > 0, what: "explanation");

        Assert.Contains("phonetic key", settings.ExplainOutput);
        Assert.Contains("other keyboard layout", settings.ExplainOutput);
        Assert.Contains("Σ' αγαπώ", settings.ExplainOutput);
        Assert.Contains("best first", settings.ExplainOutput);
    });

    [Fact]
    public Task Explain_OfNothing_SaysSo() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;

        settings.ExplainQuery = "...";
        settings.ExplainSearchCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.ExplainOutput.Length > 0, what: "explanation");

        Assert.Equal("Nothing searchable in this text.", settings.ExplainOutput);
    });

    [Fact]
    public Task TheSearchPanel_HasNoBrokenBindings_WithAnAliasListed() => Ui.RunAsync(async () =>
    {
        var app = await StartAsync();
        await using var _ = app;
        var settings = app.Main.Settings;
        settings.NewAliasSource = "Chic";
        settings.NewAliasAlias = "σικ";
        settings.AddSearchAliasCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.SearchAliases.Count == 1, what: "alias listed");
        settings.ExplainQuery = "chic";
        settings.ExplainSearchCommand.Execute(null);
        await Ui.WaitUntilAsync(() => settings.ExplainOutput.Length > 0, what: "explanation");

        app.Main.NavigateCommand.Execute(AppTab.Settings);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<SettingsView>(app.Window) is not null, what: "settings view");
        await Task.Delay(250);

        Assert.Empty(BindingAudit.FindBroken(app.Window));
    });
}
