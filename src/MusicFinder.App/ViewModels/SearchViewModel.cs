using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private const int DebounceMs = 120;
    private const int PageSize = 200;

    private readonly ISearchService _search;
    private readonly FolderTreeViewModel _tree;
    private readonly IUserDialogs _dialogs;
    private readonly IGenreService _genres;
    private readonly IDjTagService _tags;
    private readonly ISessionService _session;
    private readonly IPlayHistoryQuery _playHistory;
    private readonly Services.Dragging.TrackDragService _drag;
    private readonly ISettingsStore _settings;
    private int _playsGeneration;

    /// <summary>Thumbnail provider used by the grid and details panel controls.</summary>
    public Services.Artwork.IArtworkService Artwork { get; }

    private CancellationTokenSource? _cts;
    private SearchQuery? _currentQuery;
    private SearchCursor? _nextCursor;
    private int _generation;
    private int _pickerGeneration;
    private bool _isLoadingMore;

    public ObservableCollection<TrackRowViewModel> Results { get; } = [];
    public ObservableCollection<FilterOption> GenreOptions { get; } = [];
    public ObservableCollection<FilterOption> TagOptions { get; } = [];
    public ObservableCollection<FilterOption> DecadeOptions { get; } = [];
    public ObservableCollection<FilterOption> BpmOptions { get; } = [];

    /// <summary>"Played" dropdown: four exclusive play-status choices, then two independent hide-in-session switches.</summary>
    public ObservableCollection<FilterOption> PlayedOptions { get; } = [];
    private const long HideConfirmedId = 100, HidePossibleId = 101;

    /// <summary>Plays of the single selected track, newest first (details panel).</summary>
    public ObservableCollection<PlayHistoryLine> SelectedPlays { get; } = [];

    /// <summary>The "+" popups of the details panel: DJ tags and the user's own genre assignments.</summary>
    public AssignmentPickerViewModel TagPicker { get; } = new();
    public AssignmentPickerViewModel GenrePicker { get; } = new();

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isCountReady;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private SortField _sortField = SortField.Relevance;
    [ObservableProperty] private SortDirection _sortDirection = SortDirection.Asc;
    [ObservableProperty] private TrackRowViewModel? _selectedTrack;
    [ObservableProperty] private int _selectedCount;

    public SearchViewModel(ISearchService search, FolderTreeViewModel tree, IUserDialogs dialogs,
        IGenreService genres, IDjTagService tags, Services.Artwork.IArtworkService artwork, ITagEditingService tagEditing,
        ISessionService session, IPlayHistoryQuery playHistory, Services.Dragging.TrackDragService drag, ISettingsStore settings,
        Services.SearchIndexCoordinator searchIndex, ISimilarTracksService similar)
    {
        _drag = drag;
        _similar = similar;
        _loadingSimilarOptions = true;
        var savedSimilar = settings.Load();
        _similarBpmWindow = savedSimilar.SimilarBpmWindow;
        _similarHalfDouble = savedSimilar.SimilarHalfDoubleTempo;
        _similarHideRecentlyPlayed = savedSimilar.SimilarHideRecentlyPlayed;
        _loadingSimilarOptions = false;
        // The smart-search index may finish while a query is on screen: run it again so results that were not findable a moment ago appear.
        searchIndex.Indexed += (_, _) => { if (QueryText.Length > 0) ScheduleSearch(0); };
        _settings = settings;
        _session = session;
        _playHistory = playHistory;
        _search = search;
        _tree = tree;
        _dialogs = dialogs;
        _genres = genres;
        _tags = tags;
        Artwork = artwork;

        foreach (var decade in new[] { 2020, 2010, 2000, 1990, 1980, 1970, 1960 })
            DecadeOptions.Add(Hook(new FilterOption($"{decade}s", decade, decade)));

        foreach (var (label, min, max) in new[]
                 { ("< 90", 0d, 89.99), ("90 – 110", 90d, 109.99), ("110 – 125", 110d, 124.99), ("125 – 135", 125d, 134.99), ("135+", 135d, double.MaxValue) })
        {
            var option = new FilterOption(label, min, max);
            option.Toggled += (_, _) =>
            {
                if (option.IsSelected)
                    foreach (var other in BpmOptions.Where(o => o != option)) other.SetSilently(false); // BPM ranges are single-select
                OnPropertyChanged(nameof(HasActiveFilters));
                _session.NotifyActivity(UserActivity.Search);
                ScheduleSearch(0);
            };
            BpmOptions.Add(option);
        }

        foreach (var (label, filter) in new[]
                 { ("Never played", PlayedFilter.Never), ("Played in this session", PlayedFilter.Session),
                   ("Possibly played", PlayedFilter.Possible), ("Confirmed played", PlayedFilter.Confirmed) })
        {
            var option = new FilterOption(label) { Id = (long)filter };
            option.Toggled += (_, _) =>
            {
                if (option.IsSelected)
                    foreach (var other in PlayedOptions.Where(o => o != option && o.Id < HideConfirmedId)) other.SetSilently(false);
                OnFilterChanged();
            };
            PlayedOptions.Add(option);
        }
        var saved = _settings.Load();
        foreach (var (label, id, initial) in new[]
                 { ("Hide confirmed in session", HideConfirmedId, saved.HideConfirmedInSession),
                   ("Hide possible in session", HidePossibleId, saved.HidePossibleInSession) })
        {
            var hide = new FilterOption(label) { Id = id };
            if (initial) hide.SetSilently(true);
            hide.Toggled += (_, _) =>
            {
                var s = _settings.Load();   // remembered between runs
                if (id == HideConfirmedId) s.HideConfirmedInSession = hide.IsSelected; else s.HidePossibleInSession = hide.IsSelected;
                _settings.Save(s);
                OnFilterChanged();
            };
            PlayedOptions.Add(hide);
        }

        _tree.ScopeChanged += (_, _) =>
        {
            _session.NotifyActivity(UserActivity.Scope);
            ExitSimilarMode();
            ScheduleSearch(0);
        };
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _session.SessionChanged += (_, _) => OnUi(OnSessionChangedAsync);
        _session.PlayStateChanged += (_, ids) => OnUi(() => OnPlayStateChangedAsync(ids));
        _genres.TaxonomyChanged += (_, _) => OnUi(OnAssignmentsChangedAsync);
        _tags.TagsChanged += (_, _) => OnUi(OnAssignmentsChangedAsync);
        // The tag editor writes on a background thread, so this event does not arrive on the UI thread.
        tagEditing.TracksChanged += (_, ids) => OnUi(() => OnTracksRewrittenAsync(ids));
    }

    private readonly SynchronizationContext _ui;

    /// <summary>Runs the work on the UI thread: it touches observable collections that WPF only allows from there.</summary>
    private void OnUi(Func<Task> work)
    {
        if (SynchronizationContext.Current == _ui) _ = work();
        else _ui.Post(_ => _ = work(), null);
    }

    public string ScopeLabel => _tree.ScopeLabel;
    public string ResultsText => IsSimilarMode
        ? $"{Results.Count:N0} similar {(Results.Count == 1 ? "track" : "tracks")}"
        : IsCountReady
        ? $"{TotalCount:N0} {(TotalCount == 1 ? "result" : "results")} ({ScopeLabel})"
        : $"{Results.Count:N0}+ results ({ScopeLabel})";
    public string SelectedText => SelectedCount == 1 ? "1 selected" : $"{SelectedCount} selected";
    public bool HasActiveFilters =>
        QueryText.Length > 0 || GenreOptions.Concat(TagOptions).Concat(DecadeOptions).Concat(BpmOptions).Concat(PlayedOptions).Any(o => o.IsSelected);

    partial void OnQueryTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _session.NotifyActivity(UserActivity.Search);
        ExitSimilarMode(); // typing means "search for this", not "similar to that"
        ScheduleSearch(DebounceMs);
    }

    /// <summary>The user changed a filter: it counts as activity for the play heuristic, then the search reruns.</summary>
    private void OnFilterChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _session.NotifyActivity(UserActivity.Search);
        ExitSimilarMode();
        ScheduleSearch(0);
    }

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(ResultsText));
    partial void OnIsCountReadyChanged(bool value) => OnPropertyChanged(nameof(ResultsText));

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(SelectionHint));
        OnPropertyChanged(nameof(IsMultiSelection));
    }

    /// <summary>More than one track selected: the details panel shows bulk tag/genre assignment instead of one track.</summary>
    public bool IsMultiSelection => SelectedCount > 1;

    private FilterOption Hook(FilterOption option)
    {
        option.Toggled += (_, _) => OnFilterChanged();
        return option;
    }

    // ===== Facet options (genre taxonomy, DJ tags) =====

    /// <summary>Rebuilds the Genre and Tags dropdowns from the taxonomy, keeping what the user had selected.</summary>
    public async Task LoadFacetOptionsAsync()
    {
        var tree = await _genres.GetTaxonomyAsync();
        var tags = await _tags.GetAllAsync();

        var selectedGenres = GenreOptions.Where(o => o.IsSelected).Select(o => (o.Kind, o.Id)).ToHashSet();
        var selectedTags = TagOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();

        GenreOptions.Clear();
        AddGenre(new FilterOption("Unknown genre") { Kind = FilterKind.UnknownGenre }, selectedGenres);
        AddGenre(new FilterOption("Uncategorized") { Kind = FilterKind.Uncategorized }, selectedGenres);

        void Walk(GenreNode node, int depth)
        {
            // Hide empty genres, but never hide one that is currently selected.
            if (node.TrackCount == 0 && !selectedGenres.Contains((FilterKind.Normal, node.Genre.Id))) return;
            AddGenre(new FilterOption(node.Genre.Name) { Id = node.Genre.Id, Depth = depth, CountText = node.TrackCount.ToString("N0") }, selectedGenres);
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
        foreach (var root in tree) Walk(root, 0);

        TagOptions.Clear();
        foreach (var info in tags)
        {
            var option = Hook(new FilterOption(info.Tag.Name) { Id = info.Tag.Id, CountText = info.TrackCount.ToString("N0") });
            if (selectedTags.Contains(info.Tag.Id)) option.SetSilently(true);
            TagOptions.Add(option);
        }

        BuildPickerChoices(tree, tags);
        await RefreshPickersAsync();
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void AddGenre(FilterOption option, HashSet<(FilterKind Kind, long Id)> selected)
    {
        Hook(option);
        if (selected.Contains((option.Kind, option.Id))) option.SetSilently(true);
        GenreOptions.Add(option);
    }

    private void BuildPickerChoices(IReadOnlyList<GenreNode> tree, IReadOnlyList<DjTagInfo> tags)
    {
        Reconcile(TagPicker, tags.Select(t => (t.Tag.Id, t.Tag.Name, 0)).ToList(), ApplyTagAsync);

        var genres = new List<(long Id, string Label, int Depth)>();
        void Walk(GenreNode node, int depth)
        {
            genres.Add((node.Genre.Id, node.Genre.Name, depth));
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
        foreach (var root in tree) Walk(root, 0);
        Reconcile(GenrePicker, genres, ApplyGenreAsync);
    }

    /// <summary>
    /// Rebuilds a popup's rows only when the set of choices really changed. Assigning a tag reloads facets, and
    /// replacing the rows each time would make an open popup flicker and lose its scroll position.
    /// </summary>
    private static void Reconcile(AssignmentPickerViewModel picker, IReadOnlyList<(long Id, string Label, int Depth)> wanted,
        Func<AssignmentChoice, bool, Task> apply)
    {
        var unchanged = picker.Choices.Count == wanted.Count &&
            picker.Choices.Zip(wanted).All(p => p.First.Id == p.Second.Id && p.First.Label == p.Second.Label && p.First.Depth == p.Second.Depth);
        if (unchanged) return;

        picker.Choices.Clear();
        foreach (var (id, label, depth) in wanted)
            picker.Choices.Add(new AssignmentChoice(id, label, apply) { Depth = depth });
    }

    private IReadOnlyList<long> SelectedIds => SelectedItems.Select(i => i.Id).ToList();

    private async Task ApplyTagAsync(AssignmentChoice choice, bool assign)
    {
        var ids = SelectedIds;
        if (ids.Count == 0) return;
        try
        {
            if (assign) await _tags.AssignAsync(ids, choice.Id);
            else await _tags.UnassignAsync(ids, choice.Id);
        }
        catch (Exception ex) { Warning = "Could not update tags: " + ex.Message; }
    }

    private async Task ApplyGenreAsync(AssignmentChoice choice, bool assign)
    {
        var ids = SelectedIds;
        if (ids.Count == 0) return;
        try { await _genres.SetUserGenreAsync(ids, choice.Id, assign); }
        catch (Exception ex) { Warning = "Could not update genres: " + ex.Message; }
    }

    /// <summary>Sets every popup row's tri-state from the current selection (all / some / none of the tracks have it).</summary>
    private async Task RefreshPickersAsync()
    {
        var generation = ++_pickerGeneration;
        var ids = SelectedIds;
        TagPicker.IsEnabled = GenrePicker.IsEnabled = ids.Count > 0;

        if (ids.Count == 0)
        {
            foreach (var c in TagPicker.Choices.Concat(GenrePicker.Choices)) c.SetState(false);
            return;
        }

        var tagAssignments = await _tags.GetAssignmentsAsync(ids);
        var genreAssignments = await _genres.GetAssignmentsAsync(ids);
        if (generation != _pickerGeneration) return; // the selection changed again while we were loading

        foreach (var choice in TagPicker.Choices)
            choice.SetState(StateOf(ids.Count, tagAssignments.Count(a => a.Value.Contains(choice.Id))));

        var userGenres = genreAssignments.Where(a => a.Source == GenreSource.User).ToLookup(a => a.GenreId);
        foreach (var choice in GenrePicker.Choices)
            choice.SetState(StateOf(ids.Count, userGenres[choice.Id].Select(a => a.TrackId).Distinct().Count()));
    }

    private static bool? StateOf(int selected, int having) => having == 0 ? false : having == selected ? true : null;

    /// <summary>Tags or genres changed: reload dropdowns, refresh loaded rows in place and re-evaluate the popups.</summary>
    private async Task OnAssignmentsChangedAsync()
    {
        try
        {
            await LoadFacetOptionsAsync();
            await RefreshLoadedRowsAsync();
        }
        catch (Exception ex) { Warning = "Could not refresh: " + ex.Message; }
    }

    /// <summary>Files were rewritten by the tag editor: their covers, text, genres and facets all may have changed.</summary>
    private async Task OnTracksRewrittenAsync(IReadOnlyList<long> ids)
    {
        try
        {
            var changed = ids.ToHashSet();
            foreach (var row in Results.Where(r => changed.Contains(r.Id))) Artwork.Invalidate(row.FullPath);
            await RefreshLoadedRowsAsync();
            await LoadFacetOptionsAsync(); // genre counts and the genre list can change with edited genre text
        }
        catch (Exception ex) { Warning = "Could not refresh after editing: " + ex.Message; }
    }

    /// <summary>Re-reads the rows already on screen so chips update without losing selection or scroll position.</summary>
    public async Task RefreshLoadedRowsAsync()
    {
        var byId = Results.ToDictionary(r => r.Id);
        var sessionId = _session.Active?.Id;
        foreach (var chunk in byId.Keys.Chunk(500))
        {
            var fresh = await Task.Run(() => _search.GetRowsAsync(chunk, sessionId, CancellationToken.None));
            foreach (var row in fresh)
                if (byId.TryGetValue(row.Id, out var vm)) vm.Update(row);
        }
    }

    // ===== Searching =====

    public string SelectionHint => SelectedCount switch
    {
        0 => "Select a track to see its details.",
        1 => "",
        _ => $"{SelectedCount} tracks selected."
    };

    [RelayCommand]
    private void ClearQuery() => QueryText = "";

    [RelayCommand]
    private void Reset()
    {
        ExitSimilarMode();
        foreach (var o in GenreOptions.Concat(TagOptions).Concat(DecadeOptions).Concat(BpmOptions).Concat(PlayedOptions)) o.SetSilently(false);
        SortField = SortField.Relevance;
        SortDirection = SortDirection.Asc;
        var queryChanged = QueryText.Length > 0;
        QueryText = "";
        OnPropertyChanged(nameof(HasActiveFilters));
        _session.NotifyActivity(UserActivity.Search);
        if (!queryChanged) ScheduleSearch(0);
    }

    [RelayCommand]
    private void SortBy(SortField field)
    {
        ExitSimilarMode(); // a similar list is ordered by match; choosing a column to sort by means going back to normal results
        if (SortField == field)
        {
            // Ascending, then descending, then back to "best match first" (which has no direction).
            if (SortDirection == SortDirection.Asc) SortDirection = SortDirection.Desc;
            else { SortField = SortField.Relevance; SortDirection = SortDirection.Asc; }
        }
        else
        {
            SortField = field;
            SortDirection = SortDirection.Asc;
        }
        _session.NotifyActivity(UserActivity.Search);
        ScheduleSearch(0);
    }

    /// <summary>Runs (or re-runs) the search; the latest request wins and cancels earlier ones.</summary>
    public void ScheduleSearch(int delayMs) => _ = RunSearchAsync(delayMs);

    public Task RefreshAsync() => RunSearchAsync(0);

    private SearchQuery BuildQuery(SearchCursor? after)
    {
        var bpm = BpmOptions.FirstOrDefault(o => o.IsSelected);
        var genres = GenreOptions.Where(o => o.IsSelected).ToList();
        var played = PlayedOptions.FirstOrDefault(o => o.IsSelected && o.Id < HideConfirmedId);
        return new SearchQuery
        {
            SessionId = _session.Active?.Id,
            Text = QueryText,
            FolderId = _tree.SelectedFolderId,
            IncludeSubfolders = _tree.IncludeSubfolders,
            GlobalScope = _tree.GlobalSearch,
            Sort = SortField,
            Direction = SortDirection,
            PageSize = PageSize,
            After = after,
            Filters = new SearchFilters
            {
                GenreIds = genres.Where(o => o.Kind == FilterKind.Normal).Select(o => o.Id).ToList(),
                UnknownGenre = genres.Any(o => o.Kind == FilterKind.UnknownGenre),
                Uncategorized = genres.Any(o => o.Kind == FilterKind.Uncategorized),
                TagIds = TagOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
                Decades = DecadeOptions.Where(o => o.IsSelected).Select(o => (int)o.Min).ToList(),
                BpmMin = bpm is { Min: > 0 } ? bpm.Min : null,
                BpmMax = bpm is { Max: < double.MaxValue } ? bpm.Max : null,
                Played = played is null ? null : (PlayedFilter)played.Id,
                HideConfirmedInSession = PlayedOptions.Any(o => o.Id == HideConfirmedId && o.IsSelected),
                HidePossibleInSession = PlayedOptions.Any(o => o.Id == HidePossibleId && o.IsSelected)
            }
        };
    }

    private async Task RunSearchAsync(int delayMs)
    {
        if (IsSimilarMode) return; // background refreshes (index finished, session changed) must not replace a similar list; a user action exits the mode first
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var generation = ++_generation;

        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);

            var query = BuildQuery(null);
            // SQLite calls are synchronous underneath; keep them off the UI thread.
            var page = await Task.Run(() => _search.SearchAsync(query, ct), ct);
            if (ct.IsCancellationRequested) return;

            _currentQuery = query;
            _nextCursor = page.Next;
            HasMore = page.HasMore;
            Warning = page.Warnings.Count > 0 ? string.Join(" ", page.Warnings) : null;

            Results.Clear();
            foreach (var item in page.Items) Results.Add(new TrackRowViewModel(item));
            OnPropertyChanged(nameof(ScopeLabel));

            IsCountReady = false;
            OnPropertyChanged(nameof(ResultsText));
            var total = await Task.Run(() => _search.CountAsync(query, ct), ct);
            if (generation == _generation)
            {
                TotalCount = total;
                IsCountReady = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Warning = "Search failed: " + ex.Message;
        }
    }

    /// <summary>Called by the view when the user scrolls near the end of the loaded rows.</summary>
    public async Task LoadMoreAsync()
    {
        if (_isLoadingMore || !HasMore || _currentQuery is null || _nextCursor is null) return;
        _isLoadingMore = true;
        var generation = _generation;
        var query = _currentQuery with { After = _nextCursor };
        try
        {
            var page = await Task.Run(() => _search.SearchAsync(query, CancellationToken.None));
            if (generation != _generation) return; // a newer search replaced the results meanwhile
            foreach (var item in page.Items) Results.Add(new TrackRowViewModel(item));
            _nextCursor = page.Next;
            HasMore = page.HasMore;
            OnPropertyChanged(nameof(ResultsText));
        }
        catch (Exception ex)
        {
            Warning = "Loading more results failed: " + ex.Message;
        }
        finally { _isLoadingMore = false; }
    }

    public IReadOnlyList<TrackRowViewModel> SelectedItems { get; private set; } = [];

    /// <summary>Called by the grid when its selection changes.</summary>
    public void SetSelectedItems(IReadOnlyList<TrackRowViewModel> items)
    {
        // Picking a different track is activity for the play heuristic. Clearing the grid for a new search is not
        // (that search was already reported), and re-selecting the same rows changes nothing.
        if (items.Count > 0 && !items.Select(i => i.Id).ToHashSet().SetEquals(SelectedItems.Select(i => i.Id)))
            _session.NotifyActivity(UserActivity.Selection);

        SelectedItems = items;
        SelectedCount = items.Count;
        SelectedTrack = items.Count == 1 ? items[0] : null;
        _ = RefreshPickersAsync();
    }

    // ===== Find similar =====

    /// <summary>The choices of the tempo window, in BPM either way.</summary>
    public IReadOnlyList<double> SimilarWindowChoices { get; } = [3, 5, 8, 10, 12];

    private readonly ISimilarTracksService _similar;
    private CancellationTokenSource? _similarCts;
    private long? _similarSeedId;
    private bool _loadingSimilarOptions;

    /// <summary>True while the grid lists tracks similar to one track instead of search results.</summary>
    [ObservableProperty] private bool _isSimilarMode;
    [ObservableProperty] private string _similarTitle = "";
    /// <summary>Why there is nothing to show (too little data, or no match), in plain words.</summary>
    [ObservableProperty] private string? _similarMessage;
    [ObservableProperty] private double _similarBpmWindow = 8;
    [ObservableProperty] private bool _similarHalfDouble = true;
    [ObservableProperty] private bool _similarHideRecentlyPlayed;

    partial void OnIsSimilarModeChanged(bool value) => OnPropertyChanged(nameof(ResultsText));

    partial void OnSimilarBpmWindowChanged(double value) => SimilarOptionsChanged();
    partial void OnSimilarHalfDoubleChanged(bool value) => SimilarOptionsChanged();
    partial void OnSimilarHideRecentlyPlayedChanged(bool value) => SimilarOptionsChanged();

    private void SimilarOptionsChanged()
    {
        if (_loadingSimilarOptions) return;
        var s = _settings.Load();
        s.SimilarBpmWindow = SimilarBpmWindow;
        s.SimilarHalfDoubleTempo = SimilarHalfDouble;
        s.SimilarHideRecentlyPlayed = SimilarHideRecentlyPlayed;
        _settings.Save(s);
        if (IsSimilarMode && _similarSeedId is { } seed) _ = RunSimilarAsync(seed);
    }

    /// <summary>Lists the tracks that fit well next to the selected one (metadata only: genre, BPM, key, DJ tags).</summary>
    [RelayCommand]
    private async Task FindSimilar()
    {
        if (SelectedItems.Count != 1)
        {
            Warning = "Select one track to find tracks similar to it.";
            return;
        }
        _session.NotifyActivity(UserActivity.Search); // a new list on screen is activity, like any search
        await EnterSimilarModeAsync(SelectedItems[0].Id);
    }

    public async Task EnterSimilarModeAsync(long trackId)
    {
        _cts?.Cancel(); // a normal search still in flight must not replace the list
        Warning = null;
        IsSimilarMode = true;
        _similarSeedId = trackId;
        await RunSimilarAsync(trackId);
    }

    private async Task RunSimilarAsync(long trackId)
    {
        _similarCts?.Cancel();
        var cts = _similarCts = new CancellationTokenSource();
        try
        {
            var options = new SimilarTracksOptions
            {
                BpmWindow = SimilarBpmWindow,
                AllowHalfDoubleTempo = SimilarHalfDouble,
                ExcludePlayedSinceUtc = SimilarHideRecentlyPlayed ? DateTime.UtcNow.AddHours(-24) : null
            };
            var result = await Task.Run(() => _similar.FindAsync(trackId, options, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !IsSimilarMode) return;

            SimilarTitle = result.Seed is null ? "" : result.Seed.Title + (result.Seed.Artist is { Length: > 0 } a ? " — " + a : "");
            SimilarMessage = result.Message;

            Results.Clear();
            foreach (var item in result.Items)
            {
                var row = new TrackRowViewModel(item.Row);
                row.SetSimilar(item);
                Results.Add(row);
            }
            HasMore = false;
            _nextCursor = null;
            TotalCount = Results.Count;
            IsCountReady = true;
            OnPropertyChanged(nameof(ResultsText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Warning = "Find similar failed: " + ex.Message;
        }
    }

    /// <summary>Leaves the similar list without searching (the caller starts the search it wants).</summary>
    private void ExitSimilarMode()
    {
        if (!IsSimilarMode) return;
        _similarCts?.Cancel();
        IsSimilarMode = false;
        _similarSeedId = null;
        SimilarMessage = null;
    }

    [RelayCommand]
    private void CloseSimilar()
    {
        ExitSimilarMode();
        ScheduleSearch(0);
    }

    // ===== Drag out to other applications =====

    /// <summary>
    /// Drags the selected tracks to another application as real files. Runs the OS drag loop (blocks until the drop),
    /// then records the attempt and, if a target accepted it, the export. Returns the outcome for tests and tooling.
    /// </summary>
    public Services.Dragging.DragOutcome? DragOut(object source)
    {
        var selection = SelectedItems.Select(i => new Application.Dj.DragCandidate(i.Id, i.FullPath, i.IsMissing)).ToList();
        var result = _drag.Drag(selection, source);
        if (result.Note is not null) Warning = result.Note;
        _ = ReportRecordingFailureAsync(result.Recording);
        return result.Outcome;
    }

    private async Task ReportRecordingFailureAsync(Task recording)
    {
        try { await recording; }
        catch (Exception ex) { Warning = "The drag could not be recorded: " + ex.Message; }
    }

    // ===== Play status (DJ session) =====

    partial void OnSelectedTrackChanged(TrackRowViewModel? value) => _ = LoadSelectedPlaysAsync();

    private async Task LoadSelectedPlaysAsync()
    {
        var generation = ++_playsGeneration;
        var track = SelectedTrack;
        if (track is null)
        {
            SelectedPlays.Clear();
            return;
        }

        try
        {
            var items = await Task.Run(() => _playHistory.GetForTrackAsync(track.Id, 30, CancellationToken.None));
            if (generation != _playsGeneration) return; // another track was selected while loading
            SelectedPlays.Clear();
            foreach (var item in items) SelectedPlays.Add(PlayHistoryLine.From(item));
        }
        catch (Exception ex) { Warning = "Could not load the play history: " + ex.Message; }
    }

    /// <summary>The session started or ended: session-dependent filters and every row's session badge change.</summary>
    private async Task OnSessionChangedAsync()
    {
        try
        {
            if (UsesSession) await RunSearchAsync(0);
            else await RefreshLoadedRowsAsync();
        }
        catch (Exception ex) { Warning = "Could not refresh after the session changed: " + ex.Message; }
    }

    /// <summary>Plays changed (a window ended, or the user marked something): refresh those rows and the history list.</summary>
    private async Task OnPlayStateChangedAsync(IReadOnlyList<long> ids)
    {
        try
        {
            // Filters that depend on plays must re-evaluate, otherwise a track that just became "possibly played"
            // would stay on screen under "Hide possible". Otherwise just update the rows in place.
            if (UsesPlayState) await RunSearchAsync(0);
            else await RefreshLoadedRowsAsync();
            if (SelectedTrack is { } t && ids.Contains(t.Id)) await LoadSelectedPlaysAsync();
        }
        catch (Exception ex) { Warning = "Could not refresh play status: " + ex.Message; }
    }

    private bool UsesSession =>
        PlayedOptions.Any(o => o.IsSelected && (o.Id >= HideConfirmedId || o.Id == (long)PlayedFilter.Session)) ||
        QueryText.Contains("played:session", StringComparison.OrdinalIgnoreCase);

    private bool UsesPlayState =>
        PlayedOptions.Any(o => o.IsSelected) || QueryText.Contains("played:", StringComparison.OrdinalIgnoreCase) ||
        SortField is SortField.LastPlayed or SortField.LastConfirmed or SortField.LastPossible or SortField.PlayCount;

    public bool CanUndoPlay => _session.CanUndo;

    [RelayCommand]
    private Task MarkPlayed() => ApplyToSelectionAsync((id, ct) => _session.MarkPlayedAsync(id, null, ct), "mark as played");

    [RelayCommand]
    private Task MarkNotPlayed() => ApplyToSelectionAsync((id, ct) => _session.MarkNotPlayedAsync(id, null, ct), "mark as not played");

    [RelayCommand]
    private async Task UndoPlay()
    {
        try { await _session.UndoAsync(CancellationToken.None); }
        catch (Exception ex) { Warning = "Could not undo: " + ex.Message; }
        OnPropertyChanged(nameof(CanUndoPlay));
    }

    private async Task ApplyToSelectionAsync(Func<long, CancellationToken, Task> action, string what)
    {
        var ids = SelectedIds;
        if (ids.Count == 0) return;
        try
        {
            foreach (var id in ids) await action(id, CancellationToken.None);
        }
        catch (Exception ex) { Warning = $"Could not {what}: {ex.Message}"; }
        OnPropertyChanged(nameof(CanUndoPlay));
    }

    [RelayCommand]
    private void CopyPaths()
    {
        if (SelectedItems.Count == 0) return;
        _dialogs.CopyToClipboard(string.Join(Environment.NewLine, SelectedItems.Select(t => t.FullPath)));
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        if (SelectedItems.Count > 0) _dialogs.RevealInExplorer(SelectedItems[0].FullPath);
    }
}
