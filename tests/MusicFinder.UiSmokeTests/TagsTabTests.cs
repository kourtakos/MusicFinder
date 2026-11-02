using Microsoft.Extensions.DependencyInjection;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.UiSmokeTests;

/// <summary>The Tags tab on a real library: taxonomy editing, aliases, DJ tags and mapping unmapped genre text.</summary>
public class TagsTabTests
{
    private static readonly (string, string, string, string)[] Files =
    [
        ("a.mp3", "Alpha", "A", "Disco"),
        ("b.mp3", "Bravo", "B", "Funk"),
        ("c.mp3", "Charlie", "C", "Vaporwave"),
        ("d.mp3", "Delta", "D", "Vaporwave;Seapunk"),
    ];

    private sealed record Ctx(UiTestApp App, FakeDialogs Dialogs)
    {
        public TagsViewModel Tags => App.Main.Tags;
        public SearchViewModel Search => App.Main.Library.Search;
    }

    private static async Task<Ctx> StartAsync()
    {
        var dialogs = new FakeDialogs();
        var app = await UiTestApp.CreateWithFilesAsync(s => s.AddSingleton<IUserDialogs>(dialogs), Files);
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 4, what: "indexed rows");
        return new Ctx(app, dialogs);
    }

    private static GenreNodeItem Node(TagsViewModel tags, string name) =>
        tags.Taxonomy.SelectMany(n => n.SelfAndDescendants()).Single(n => n.Name == name);

    private static async Task Select(Ctx c, string genre)
    {
        Node(c.Tags, genre).IsSelected = true;
        await Ui.WaitUntilAsync(() => c.Tags.SelectedGenre?.Name == genre && c.Tags.ParentChoices.Count > 0, what: $"{genre} selected");
    }

    [Fact]
    public Task Load_ShowsTheTaxonomyDjTagsAndUnmappedValues() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        Assert.Equal(16, c.Tags.Taxonomy.Count);
        Assert.Equal(["Warm Up", "Peak Time", "Closing", "Wedding", "Greek Party"], c.Tags.DjTags.Select(t => t.Name));
        Assert.Equal(1, Node(c.Tags, "Disco").TrackCount);

        // "Vaporwave" is on two tracks, "Seapunk" on one; most common first.
        Assert.Equal([("Vaporwave", 2), ("Seapunk", 1)], c.Tags.Unmapped.Select(u => (u.Value, u.TrackCount)));
        Assert.True(c.Tags.HasUnmapped);
        Assert.False(c.Tags.HasSelection);
    });

    [Fact]
    public Task SelectingAGenre_FillsTheEditor_AndForbidsMovingIntoItself() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        await Select(c, "Disco");

        Assert.True(c.Tags.HasSelection);
        Assert.Equal("Disco", c.Tags.EditName);
        Assert.Equal("Built-in genre · you can still rename, move or remove it", c.Tags.SelectedGenreSubtitle);
        Assert.Equal("(top level)", c.Tags.SelectedParent!.Label);                   // Disco is a top-level genre
        Assert.DoesNotContain(c.Tags.ParentChoices, p => p.Label is "Disco" or "Nu Disco"); // not itself, not its own child
        Assert.Contains(c.Tags.ParentChoices, p => p.Label == "Jazz");
        Assert.Contains(c.Tags.Aliases, a => a.Alias == "disco funk");

        await Select(c, "Nu Disco");
        Assert.Equal("Disco", c.Tags.SelectedParent!.Label);                         // a child shows its current parent
    });

    [Fact]
    public Task DjTags_CanBeAddedRenamedAndDeleted_WithClearMessages() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Tags.NewTagName = "Sunset Set";
        await c.Tags.AddTagCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => c.Tags.DjTags.Any(t => t.Name == "Sunset Set"), what: "tag added");
        Assert.Equal("", c.Tags.NewTagName);
        Assert.False(c.Tags.MessageIsError);

        c.Tags.NewTagName = "sunset  set";                    // same after normalization
        await c.Tags.AddTagCommand.ExecuteAsync(null);
        Assert.True(c.Tags.MessageIsError);
        Assert.Contains("already exists", c.Tags.Message);
        Assert.Equal(1, c.Tags.DjTags.Count(t => t.Name == "Sunset Set"));

        var sunset = c.Tags.DjTags.Single(t => t.Name == "Sunset Set");
        sunset.Name = "Golden Hour";                          // what leaving the edited box does
        await Ui.WaitUntilAsync(() => c.Tags.Message == "Renamed to 'Golden Hour'.", what: "tag renamed");

        var golden = c.Tags.DjTags.Single(t => t.Name == "Golden Hour");
        golden.Name = "Warm Up";                              // collides with an existing tag
        await Ui.WaitUntilAsync(() => c.Tags.MessageIsError, what: "rename rejected");
        Assert.Equal("Golden Hour", golden.Name);             // the box reverts to what is stored

        golden.Name = "   ";
        await Ui.WaitUntilAsync(() => golden.Name == "Golden Hour" && c.Tags.Message!.Contains("needs a name"), what: "blank rejected");

        await c.Tags.DeleteTagCommand.ExecuteAsync(c.Tags.DjTags.Single(t => t.Name == "Golden Hour"));
        await Ui.WaitUntilAsync(() => c.Tags.DjTags.All(t => t.Name != "Golden Hour"), what: "tag deleted");
    });

    [Fact]
    public Task DeletingATag_IsConfirmed_AndRemovesItFromTracks() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var tags = c.App.Service<IDjTagService>();
        var warmUp = c.Tags.DjTags.Single(t => t.Name == "Warm Up");
        await tags.AssignAsync([c.Search.Results[0].Id], warmUp.Id);
        await Ui.WaitUntilAsync(() => c.Tags.DjTags.Single(t => t.Name == "Warm Up").TrackCount == 1, what: "count refreshed");

        await c.Tags.DeleteTagCommand.ExecuteAsync(c.Tags.DjTags.Single(t => t.Name == "Warm Up"));

        await Ui.WaitUntilAsync(() => c.Tags.DjTags.All(t => t.Name != "Warm Up"), what: "deleted");
        await Ui.WaitUntilAsync(() => c.Search.TagOptions.All(o => o.Label != "Warm Up"), what: "library filter refreshed");
        Assert.All(c.Search.Results, r => Assert.DoesNotContain("Warm Up", r.DjTags));
    });

    [Fact]
    public Task Genres_CanBeAddedRenamedMovedAndDeleted() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Tags.NewTopGenre = "Chillout";
        await c.Tags.AddTopGenreCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => c.Tags.Taxonomy.Any(n => n.Name == "Chillout"), what: "genre added");

        await Select(c, "Chillout");
        c.Tags.NewSubGenre = "Lounge";
        await c.Tags.AddSubGenreCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => Node(c.Tags, "Chillout").Children.Any(n => n.Name == "Lounge"), what: "sub-genre added");

        await Select(c, "Lounge");
        c.Tags.EditName = "Lounge Music";
        await c.Tags.RenameGenreCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => c.Tags.Taxonomy.SelectMany(n => n.SelfAndDescendants()).Any(n => n.Name == "Lounge Music"), what: "renamed");

        await Select(c, "Lounge Music");
        c.Tags.SelectedParent = c.Tags.ParentChoices.Single(p => p.Label == "Jazz");
        await c.Tags.MoveGenreCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => Node(c.Tags, "Jazz").Children.Any(n => n.Name == "Lounge Music"), what: "moved");

        await Select(c, "Lounge Music");
        await c.Tags.DeleteGenreCommand.ExecuteAsync(null);
        await Ui.WaitUntilAsync(() => c.Tags.Taxonomy.SelectMany(n => n.SelfAndDescendants()).All(n => n.Name != "Lounge Music"), what: "deleted");
        Assert.False(c.Tags.HasSelection);
    });

    [Fact]
    public Task InvalidGenreEdits_ShowAMessage_AndChangeNothing() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.Tags.NewTopGenre = "  ";
        await c.Tags.AddTopGenreCommand.ExecuteAsync(null);
        Assert.True(c.Tags.MessageIsError);
        Assert.Equal(16, c.Tags.Taxonomy.Count);

        await Select(c, "Jazz");
        c.Tags.EditName = "Disco";                                   // already a genre
        await c.Tags.RenameGenreCommand.ExecuteAsync(null);
        Assert.Contains("already exists", c.Tags.Message);

        await Select(c, "Greek");
        await c.Tags.DeleteGenreCommand.ExecuteAsync(null);          // has sub-genres
        Assert.True(c.Tags.MessageIsError);
        Assert.Contains("sub-genres", c.Tags.Message);
        Assert.Contains(c.Tags.Taxonomy, n => n.Name == "Greek");

        c.Tags.NewAlias = "disco";                                    // the name of another genre
        await c.Tags.AddAliasCommand.ExecuteAsync(null);
        Assert.True(c.Tags.MessageIsError);
        Assert.DoesNotContain(c.Tags.Aliases, a => a.Alias == "disco");
    });

    [Fact]
    public Task Aliases_AreAddedAndRemoved_AndTheLibraryFollows() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var charlie = c.Search.Results.Single(r => r.Title == "Charlie");
        Assert.Equal(["Vaporwave"], charlie.Genres);                   // raw text, unmapped
        Assert.False(charlie.Row.HasMappedGenres);

        await Select(c, "Electronic");
        c.Tags.NewAlias = "Vaporwave";
        await c.Tags.AddAliasCommand.ExecuteAsync(null);

        await Ui.WaitUntilAsync(() => c.Tags.Aliases.Any(a => a.Alias == "vaporwave"), what: "alias listed");
        await Ui.WaitUntilAsync(() => charlie.Genres.SequenceEqual(["Electronic"]), what: "library row remapped");
        Assert.True(charlie.Row.HasMappedGenres);
        Assert.DoesNotContain(c.Tags.Unmapped, u => u.Value == "Vaporwave");
        Assert.Equal("Vaporwave", charlie.Row.GenreRaw);               // the file's text is never edited

        await c.Tags.RemoveAliasCommand.ExecuteAsync(c.Tags.Aliases.Single(a => a.Alias == "vaporwave"));
        await Ui.WaitUntilAsync(() => charlie.Genres.SequenceEqual(["Vaporwave"]), what: "mapping undone");
        Assert.Contains(c.Tags.Unmapped, u => u.Value == "Vaporwave");
    });

    [Fact]
    public Task Unmapped_CanBeMappedToTheSelectedGenre() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        Assert.False(c.Tags.CanMap);

        c.Tags.SelectedUnmapped = c.Tags.Unmapped.Single(u => u.Value == "Vaporwave");
        Assert.False(c.Tags.CanMap);                                   // needs a genre too
        Assert.True(c.Tags.CanCreateFromUnmapped);
        await Select(c, "Electronic");
        Assert.True(c.Tags.CanMap);

        await c.Tags.MapUnmappedCommand.ExecuteAsync(null);

        await Ui.WaitUntilAsync(() => c.Tags.Unmapped.All(u => u.Value != "Vaporwave"), what: "no longer unmapped");
        Assert.Contains("now maps to Electronic", c.Tags.Message);
        Assert.Equal(2, Node(c.Tags, "Electronic").TrackCount);        // both Vaporwave tracks count now
        Assert.Contains(c.Search.GenreOptions, o => o.Label == "Electronic");
    });

    [Fact]
    public Task Unmapped_CanBecomeANewGenre() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.Tags.SelectedUnmapped = c.Tags.Unmapped.Single(u => u.Value == "Seapunk");

        await c.Tags.CreateGenreFromUnmappedCommand.ExecuteAsync(null); // nothing selected: created at the top level

        await Ui.WaitUntilAsync(() => c.Tags.Taxonomy.Any(n => n.Name == "Seapunk"), what: "top-level genre created");
        await Ui.WaitUntilAsync(() => c.Tags.Unmapped.All(u => u.Value != "Seapunk"), what: "mapped");
        Assert.Equal(1, Node(c.Tags, "Seapunk").TrackCount);
    });

    [Fact]
    public Task TheTab_IsReachableFromTheHeader_AndShowsFreshData() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;

        c.App.Main.NavigateCommand.Execute(AppTab.Tags);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<TagsView>(c.App.Window) is not null, what: "tags view");
        Assert.Null(Ui.FindDescendant<PlaceholderView>(c.App.Window));
        Assert.Same(c.Tags, c.App.Main.CurrentView);
    });

    [Fact]
    public Task HeaderTabs_StayInSyncWithTheContent_HoweverTheyAreChecked() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        var tabs = FindAll<System.Windows.Controls.RadioButton>(c.App.Window).ToList();
        Assert.Equal(6, tabs.Count);                                // Library, Tags, Health, History, Session, Settings
        Assert.True(tabs[0].IsChecked);                                 // Library

        // Keyboard navigation and UI Automation check a tab without clicking it; the content must follow.
        tabs[1].IsChecked = true;
        Assert.Equal(AppTab.Tags, c.App.Main.SelectedTab);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<TagsView>(c.App.Window) is not null, what: "tags view");

        tabs[2].IsChecked = true;
        Assert.Equal(AppTab.Health, c.App.Main.SelectedTab);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<HealthView>(c.App.Window) is not null, what: "health view");

        tabs[5].IsChecked = true;
        Assert.Equal(AppTab.Settings, c.App.Main.SelectedTab);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<SettingsView>(c.App.Window) is not null, what: "settings view");

        // And the other way round: navigating in code moves the highlighted tab.
        c.App.Main.NavigateCommand.Execute(AppTab.Library);
        Assert.True(tabs[0].IsChecked);
        Assert.False(tabs[5].IsChecked);
    });

    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    [Fact]
    public Task TagsView_HasNoBrokenBindings_IncludingOpenDropdowns() => Ui.RunAsync(async () =>
    {
        var c = await StartAsync();
        await using var _ = c.App;
        c.App.Main.NavigateCommand.Execute(AppTab.Tags);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<TagsView>(c.App.Window) is not null);
        await Select(c, "Greek");
        c.Tags.SelectedUnmapped = c.Tags.Unmapped[0];
        c.Tags.Message = "Something worth saying";
        await Task.Delay(300);

        var view = Ui.FindDescendant<TagsView>(c.App.Window)!;
        var combo = (System.Windows.Controls.ComboBox)view.FindName("ParentBox");
        combo.IsDropDownOpen = true;                                   // its popup is only realized while open
        await Task.Delay(300);

        var broken = BindingAudit.FindBroken(c.App.Window);
        combo.IsDropDownOpen = false;
        Assert.True(broken.Count == 0, "Broken bindings:\n" + string.Join("\n", broken.Distinct()));
    });
}
