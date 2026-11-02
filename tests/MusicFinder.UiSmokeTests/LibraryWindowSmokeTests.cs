using System.Windows;
using System.Windows.Controls;
using MusicFinder.App.ViewModels;
using MusicFinder.App.Views;

namespace MusicFinder.UiSmokeTests;

public class LibraryWindowSmokeTests
{
    private static async Task<(UiTestApp App, DataGrid Grid)> ShowAsync()
    {
        var app = await UiTestApp.CreateAsync();
        app.Window.Show();
        await app.Main.InitializeAsync();
        await Ui.WaitUntilAsync(() => app.Main.Library.Search.Results.Count == 3, what: "seeded rows");
        await Task.Delay(150); // let layout and templates settle
        var grid = Ui.FindDescendant<DataGrid>(app.Window) ?? throw new InvalidOperationException("TrackGrid not found");
        return (app, grid);
    }

    [Fact]
    public Task Window_HasNoBrokenBindings_OnAnyTab() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        grid.SelectedIndex = 0; // the details panel only binds once a track is selected
        await Task.Delay(150);

        var broken = new List<string>();
        foreach (var tab in new[] { AppTab.Library, AppTab.Settings, AppTab.Tags, AppTab.History, AppTab.Session, AppTab.Library })
        {
            app.Main.NavigateCommand.Execute(tab);
            await Task.Delay(150);
            app.Window.UpdateLayout();
            broken.AddRange(BindingAudit.FindBroken(app.Window).Select(b => $"[{tab}] {b}"));
        }

        Assert.True(broken.Count == 0, "Broken bindings:\n" + string.Join("\n", broken.Distinct()));
    });

    [Fact]
    public Task BindingAudit_ActuallyDetectsBrokenBindings() => Ui.RunAsync(() =>
    {
        // Guards the guard: if this stops finding the error, the test above proves nothing.
        var block = new TextBlock { DataContext = new object() };
        block.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DoesNotExist"));

        var broken = BindingAudit.FindBroken(block);
        Assert.Single(broken);
        Assert.Contains("DoesNotExist", broken[0]);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Popups_HaveNoBrokenBindings_WhenOpen() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        grid.SelectedIndex = 0;
        await Task.Delay(150);

        // Popup contents are only realized while open, so open every dropdown and picker before auditing.
        var popups = new List<System.Windows.Controls.Primitives.Popup>();
        foreach (var dropDown in AllDescendants<MusicFinder.App.Controls.FilterDropDown>(app.Window))
            popups.Add((System.Windows.Controls.Primitives.Popup)dropDown.FindName("Popup"));
        foreach (var picker in AllDescendants<MusicFinder.App.Controls.AssignmentPicker>(app.Window))
            popups.Add((System.Windows.Controls.Primitives.Popup)picker.FindName("Popup"));
        Assert.True(popups.Count >= 5, $"expected Genre/BPM/Decade/Tags dropdowns and two pickers, found {popups.Count}");

        foreach (var p in popups) p.IsOpen = true;
        await Task.Delay(300);
        var broken = BindingAudit.FindBroken(app.Window);
        foreach (var p in popups) p.IsOpen = false;

        Assert.True(broken.Count == 0, "Broken bindings:\n" + string.Join("\n", broken.Distinct()));
    });

    private static IEnumerable<T> AllDescendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in AllDescendants<T>(child)) yield return nested;
        }
    }

    [Fact]
    public Task GenreFilter_ListsTheTaxonomyWithHierarchy_AndFiltersByParent() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;

        var labels = search.GenreOptions.Select(o => o.Label).ToList();
        Assert.Equal(["Unknown genre", "Uncategorized"], labels.Take(2)); // pinned virtual buckets first
        Assert.Contains("Disco", labels);
        Assert.Contains("Funk", labels);
        Assert.Contains("Laiko", labels);
        Assert.DoesNotContain("Jazz", labels); // genres without tracks are hidden

        var greek = search.GenreOptions.Single(o => o.Label == "Greek");
        var laiko = search.GenreOptions.Single(o => o.Label == "Laiko");
        Assert.Equal((0, 1), (greek.Depth, laiko.Depth));
        Assert.True(search.GenreOptions.ToList().IndexOf(laiko) > search.GenreOptions.ToList().IndexOf(greek)); // child follows parent

        // Selecting the parent genre matches the track mapped to its child.
        greek.IsSelected = true;
        await Ui.WaitUntilAsync(() => grid.Items.Count == 1, what: "genre-filtered rows");
        Assert.Equal("Σ' αγαπώ", ((TrackRowViewModel)grid.Items[0]).Title);

        search.ResetCommand.Execute(null);
        await Ui.WaitUntilAsync(() => grid.Items.Count == 3, what: "reset rows");
        Assert.False(greek.IsSelected);
    });

    [Fact]
    public Task Chips_ShowTheTaxonomyName_AndKeepRawTextVisibleWhenUnmapped() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var rows = grid.Items.Cast<TrackRowViewModel>().ToDictionary(r => r.Title);

        Assert.Equal(["Disco", "Funk"], rows["Le Freak"].GenreChips.Select(c => c.Text));
        Assert.All(rows["Le Freak"].GenreChips, c => Assert.True(c.IsMapped));
        Assert.Equal("Laiko", rows["Σ' αγαπώ"].GenreChips.Single().Text); // "Λαϊκά" shown as its taxonomy genre

        // An unmapped value stays visible as raw text, flagged so the grid draws it muted.
        var untouched = rows["Good Times"];
        Assert.True(untouched.GenreChips.Single().IsMapped);
    });

    [Fact]
    public Task TagPicker_AssignsToSelection_AndRefreshesRowsInPlace() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        Assert.False(search.TagPicker.IsEnabled); // nothing selected yet

        grid.SelectedItem = grid.Items[0];
        await Ui.WaitUntilAsync(() => search.TagPicker.IsEnabled, what: "picker enabled by selection");
        var selected = (TrackRowViewModel)grid.SelectedItem;

        var warmUp = search.TagPicker.Choices.Single(c => c.Label == "Warm Up");
        Assert.False(warmUp.IsChecked);

        warmUp.IsChecked = true; // what a click in the popup does
        await Ui.WaitUntilAsync(() => selected.DjTags.Contains("Warm Up"), what: "row refreshed in place");

        // Selection and scroll survive: the same row object is still selected, no results reload.
        Assert.Same(selected, grid.SelectedItem);
        Assert.Equal(3, grid.Items.Count);
        Assert.Equal("1", search.TagOptions.Single(o => o.Label == "Warm Up").CountText);

        // Filtering by the tag now finds exactly that track.
        search.TagOptions.Single(o => o.Label == "Warm Up").IsSelected = true;
        await Ui.WaitUntilAsync(() => grid.Items.Count == 1, what: "tag-filtered rows");
        Assert.Equal(selected.Id, ((TrackRowViewModel)grid.Items[0]).Id);
    });

    [Fact]
    public Task TagPicker_ShowsMixedStateForAMultiSelection_AndAssignsToAll() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;

        // Tag only the first track, then select everything.
        grid.SelectedItem = grid.Items[0];
        await Ui.WaitUntilAsync(() => search.TagPicker.IsEnabled);
        var peak = search.TagPicker.Choices.Single(c => c.Label == "Peak Time");
        peak.IsChecked = true;
        await Ui.WaitUntilAsync(() => ((TrackRowViewModel)grid.Items[0]).DjTags.Contains("Peak Time"));

        grid.SelectAll();
        await Ui.WaitUntilAsync(() => search.SelectedCount == 3 && peak.IsChecked is null, what: "mixed state");
        Assert.True(search.IsMultiSelection);

        peak.IsChecked = true; // clicking a mixed row assigns it to every selected track
        await Ui.WaitUntilAsync(() => grid.Items.Cast<TrackRowViewModel>().All(r => r.DjTags.Contains("Peak Time")), what: "assigned to all");
        await Ui.WaitUntilAsync(() => peak.IsChecked == true);

        peak.IsChecked = false;
        await Ui.WaitUntilAsync(() => grid.Items.Cast<TrackRowViewModel>().All(r => !r.DjTags.Contains("Peak Time")), what: "removed from all");
    });

    [Fact]
    public Task GenrePicker_AssignsUserGenres_WithoutTouchingTheEmbeddedText() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;

        grid.SelectedItem = grid.Items.Cast<TrackRowViewModel>().Single(r => r.Title == "Good Times");
        await Ui.WaitUntilAsync(() => search.GenrePicker.IsEnabled);
        var selected = (TrackRowViewModel)grid.SelectedItem;

        search.GenrePicker.Choices.Single(c => c.Label == "Boogie").IsChecked = true;
        await Ui.WaitUntilAsync(() => selected.Genres.Contains("Boogie"), what: "user genre shown on the row");

        Assert.Contains("Disco", selected.Genres);                 // the mapped embedded genre is still there
        Assert.Equal("Disco", selected.Row.GenreRaw);              // and the embedded text was never edited
        Assert.Contains(search.GenreOptions, o => o.Label == "Boogie");
    });

    [Fact]
    public Task GenreDropdown_FilterBox_MatchesIgnoringCaseAndAccents() => Ui.RunAsync(async () =>
    {
        var (app, _) = await ShowAsync();
        await using var __ = app;
        var dropDown = AllDescendants<MusicFinder.App.Controls.FilterDropDown>(app.Window).First(d => d.Header == "Genre");
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(dropDown.Options);

        dropDown.FilterText = "lAiK";
        Assert.Equal(["Laiko"], view.Cast<FilterOption>().Select(o => o.Label));

        dropDown.FilterText = "";
        Assert.True(view.Cast<FilterOption>().Count() > 3);
    });

    [Fact]
    public Task Grid_ShowsSeededTracks_AndSearchFilters() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        Assert.Equal(3, grid.Items.Count);

        app.Main.Library.Search.QueryText = "chic";
        await Ui.WaitUntilAsync(() => grid.Items.Count == 2, what: "filtered rows");

        app.Main.Library.Search.QueryText = "αλεξιου"; // accent-insensitive Greek
        await Ui.WaitUntilAsync(() => grid.Items.Count == 1, what: "Greek rows");

        app.Main.Library.Search.ResetCommand.Execute(null);
        await Ui.WaitUntilAsync(() => grid.Items.Count == 3, what: "reset rows");
    });

    [Fact]
    public Task SelectingRow_UpdatesDetailsAndStatusBar() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        var search = app.Main.Library.Search;
        Assert.Null(search.SelectedTrack);
        Assert.Equal("0 selected", search.SelectedText);

        grid.SelectedItem = grid.Items[0];
        await Task.Delay(50);
        Assert.NotNull(search.SelectedTrack);
        Assert.Equal("1 selected", search.SelectedText);

        grid.SelectAll();
        await Task.Delay(50);
        Assert.Null(search.SelectedTrack);
        Assert.Equal("3 selected", search.SelectedText);
    });

    [Fact]
    public Task Tabs_SwitchViews() => Ui.RunAsync(async () =>
    {
        var (app, _) = await ShowAsync();
        await using var __ = app;
        Assert.NotNull(Ui.FindDescendant<LibraryView>(app.Window));

        app.Main.NavigateCommand.Execute(AppTab.Settings);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<SettingsView>(app.Window) is not null, what: "settings view");
        Assert.Null(Ui.FindDescendant<LibraryView>(app.Window));

        app.Main.NavigateCommand.Execute(AppTab.Session);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<SessionView>(app.Window) is not null, what: "session view");

        app.Main.NavigateCommand.Execute(AppTab.History);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<PlaceholderView>(app.Window) is not null, what: "placeholder view");

        app.Main.NavigateCommand.Execute(AppTab.Library);
        await Ui.WaitUntilAsync(() => Ui.FindDescendant<LibraryView>(app.Window) is not null, what: "library view");
    });

    [Fact]
    public Task MinimumWindowSize_StillLaysOutTheGrid() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        app.Window.WindowState = WindowState.Normal;
        app.Window.Width = app.Window.MinWidth;
        app.Window.Height = app.Window.MinHeight;
        app.Window.UpdateLayout();
        await Task.Delay(150);

        Assert.Equal(1000, app.Window.MinWidth);
        Assert.Equal(650, app.Window.MinHeight);
        Assert.True(grid.ActualWidth >= 440, $"results grid too narrow: {grid.ActualWidth}");
    });

    [Fact]
    public Task FocusShortcut_SwitchesToLibraryAndRequestsFocus() => Ui.RunAsync(async () =>
    {
        var (app, _) = await ShowAsync();
        await using var __ = app;
        app.Main.NavigateCommand.Execute(AppTab.Settings);
        await Task.Delay(50);

        var raised = false;
        app.Main.Library.FocusSearchRequested += (_, _) => raised = true;
        app.Main.FocusSearchCommand.Execute(null);

        Assert.Equal(AppTab.Library, app.Main.SelectedTab);
        Assert.True(raised);
    });

    [Fact]
    public Task PanelWidthsAndScopeToggles_ArePersisted() => Ui.RunAsync(async () =>
    {
        var (app, _) = await ShowAsync();
        await using var __ = app;
        var library = app.Main.Library;

        library.LeftPanelWidth = new GridLength(300);
        library.RightPanelWidth = new GridLength(410);
        library.Tree.IncludeSubfolders = false;
        await Ui.WaitUntilAsync(() => app.Settings.Load().LeftPanelWidth == 300, what: "debounced settings save");

        var saved = app.Settings.Load();
        Assert.Equal(300, saved.LeftPanelWidth);
        Assert.Equal(410, saved.RightPanelWidth);
        Assert.False(saved.IncludeSubfolders);
    });

    [Fact]
    public Task DeleteKey_NeverRemovesTracks() => Ui.RunAsync(async () =>
    {
        var (app, grid) = await ShowAsync();
        await using var _ = app;
        grid.SelectAll();
        var args = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "t", IntPtr.Zero),
            0, System.Windows.Input.Key.Delete)
        { RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent };
        grid.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Equal(3, grid.Items.Count);
    });
}
