using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Application.Genres;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.ViewModels;

/// <summary>The Library tab: folder tree, search/results, library stats and indexing status.</summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly ScanCoordinator _scans;
    private readonly ILibraryRootRepository _roots;
    private readonly ITrackRepository _tracks;
    private readonly DispatcherTimer _liveSearchTimer;

    public FolderTreeViewModel Tree { get; }
    public SearchViewModel Search { get; }

    [ObservableProperty] private int _totalTracks;
    [ObservableProperty] private int _indexedFolders;
    [ObservableProperty] private string _lastScanText = "—";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanText = "";

    /// <summary>Resizable side panels; persisted (debounced) to settings.json.</summary>
    [ObservableProperty] private GridLength _leftPanelWidth = new(240);
    [ObservableProperty] private GridLength _rightPanelWidth = new(320);

    private readonly ISettingsStore _settings;
    private readonly GenreMappingJob _genreJob;
    private readonly ITagEditingService _tagEditing;
    private readonly IUserDialogs _dialogs;
    private readonly MusicFinder.Infrastructure.AppPaths _paths;
    private readonly DispatcherTimer _saveTimer;
    private readonly IIdentificationService _identification;
    private readonly IdentificationSettingsViewModel _identificationSettings;
    private readonly TimeProvider _time;
    private readonly ICoverArtClient _coverArt;

    public LibraryViewModel(FolderTreeViewModel tree, SearchViewModel search, ScanCoordinator scans,
        ILibraryRootRepository roots, ITrackRepository tracks, ISettingsStore settings, GenreMappingJob genreJob,
        ITagEditingService tagEditing, IUserDialogs dialogs, MusicFinder.Infrastructure.AppPaths paths,
        IIdentificationService identification, IdentificationSettingsViewModel identificationSettings, TimeProvider time, ICoverArtClient coverArt)
    {
        Tree = tree;
        Search = search;
        _scans = scans;
        _roots = roots;
        _tracks = tracks;
        _settings = settings;
        _genreJob = genreJob;
        _tagEditing = tagEditing;
        _dialogs = dialogs;
        _paths = paths;
        _identification = identification;
        _identificationSettings = identificationSettings;
        _time = time;
        _coverArt = coverArt;

        var saved = settings.Load();
        _leftPanelWidth = new GridLength(Math.Clamp(saved.LeftPanelWidth, 180, 420));
        _rightPanelWidth = new GridLength(Math.Clamp(saved.RightPanelWidth, 260, 520));
        Tree.IncludeSubfolders = saved.IncludeSubfolders;
        Tree.GlobalSearch = saved.GlobalSearch;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveLayout(); };
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FolderTreeViewModel.IncludeSubfolders) or nameof(FolderTreeViewModel.GlobalSearch))
                QueueSave();
        };

        _scans.StateChanged += (_, _) => UpdateScanState();
        _scans.ScanFinished += async (_, _) =>
        {
            await RefreshStatsAsync();
            await MapGenresAsync();
            await Search.RefreshAsync();
        };

        // Results appear incrementally while indexing: refresh the first page until it is full.
        _liveSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _liveSearchTimer.Tick += async (_, _) =>
        {
            if (!_scans.IsScanning) return;
            await RefreshStatsAsync();
            if (Search.Results.Count < 200) await Search.RefreshAsync();
        };
        _liveSearchTimer.Start();
    }

    partial void OnLeftPanelWidthChanged(GridLength value) => QueueSave();
    partial void OnRightPanelWidthChanged(GridLength value) => QueueSave();

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveLayout()
    {
        try
        {
            var s = _settings.Load();
            if (LeftPanelWidth.IsAbsolute) s.LeftPanelWidth = LeftPanelWidth.Value;
            if (RightPanelWidth.IsAbsolute) s.RightPanelWidth = RightPanelWidth.Value;
            s.IncludeSubfolders = Tree.IncludeSubfolders;
            s.GlobalSearch = Tree.GlobalSearch;
            _settings.Save(s);
        }
        catch (IOException) { /* layout is a convenience; never fail the UI over it */ }
    }

    /// <summary>Opens the tag editor for the selected tracks. Refused while indexing so a scan never reads a file mid-write.</summary>
    [RelayCommand]
    private async Task EditTagsAsync()
    {
        var selected = Search.SelectedItems.ToList();
        if (selected.Count == 0) return;
        if (_scans.IsScanning)
        {
            _dialogs.ShowInfo("Edit tags", "MusicFinder is still indexing your library. Please wait until it finishes, then try again.");
            return;
        }

        var editor = new TagEditorViewModel(selected, _tagEditing, _dialogs, _settings, _paths);
        await editor.LoadAsync();
        _dialogs.ShowTagEditor(editor);
    }

    /// <summary>
    /// Opens the online identification window for the selected tracks. The first time, the user is shown exactly what is sent and
    /// must agree; declining changes nothing. Refused while indexing, like the tag editor, because it can write tags.
    /// </summary>
    [RelayCommand]
    private void Identify()
    {
        var selected = Search.SelectedItems.ToList();
        if (selected.Count == 0) return;
        if (_scans.IsScanning)
        {
            _dialogs.ShowInfo("Identify online", "MusicFinder is still indexing your library. Please wait until it finishes, then try again.");
            return;
        }

        var readiness = _identification.GetReadiness();
        if (!readiness.Enabled || !readiness.Consented)
        {
            var consent = new IdentificationConsentViewModel(_settings, _time);
            _dialogs.ShowIdentificationConsent(consent);
            _identificationSettings.Reload();
            if (!consent.Accepted) return;
        }

        _dialogs.ShowIdentify(new IdentifyViewModel(selected, _identification, _tagEditing, _tracks, _coverArt, _settings, _paths));
    }

    /// <summary>Raised when the view should move keyboard focus to the search box (Ctrl+F / Ctrl+K).</summary>
    public event EventHandler? FocusSearchRequested;

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    public async Task InitializeAsync()
    {
        await Tree.ReloadAsync();
        await Search.LoadFacetOptionsAsync();
        await Search.RefreshAsync();
        await RefreshStatsAsync();
        _ = MapGenresInBackgroundAsync(); // catches up tracks whose genre mapping is stale (first run after an upgrade)
    }

    /// <summary>Maps stale tracks onto the taxonomy, then reloads the genre facets. Never touches the music files.</summary>
    private async Task MapGenresAsync()
    {
        try
        {
            await Task.Run(() => _genreJob.RunAsync());
            await Search.LoadFacetOptionsAsync();
        }
        catch (Exception ex)
        {
            Search.Warning = "Genre mapping failed: " + ex.Message;
        }
    }

    private async Task MapGenresInBackgroundAsync()
    {
        await MapGenresAsync();
        await Search.RefreshLoadedRowsAsync();
    }

    public async Task RefreshStatsAsync()
    {
        var roots = await Task.Run(() => _roots.GetAllAsync());
        TotalTracks = await Task.Run(() => _tracks.CountAsync());
        var enabled = roots.Where(r => r.Enabled).ToList();
        IndexedFolders = enabled.Count;
        var last = enabled.Where(r => r.LastScanUtc is not null).Select(r => r.LastScanUtc!.Value).DefaultIfEmpty().Max();
        LastScanText = last == default ? "—" : last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    private void UpdateScanState()
    {
        IsScanning = _scans.IsScanning;
        if (!_scans.IsScanning) { ScanText = ""; return; }

        var p = _scans.Current;
        ScanText = p is null
            ? $"Indexing {_scans.CurrentRootPath}…"
            : $"Indexing {_scans.CurrentRootPath} — {p.FilesProcessed:N0} of {p.FilesFound:N0} files" + (p.Errors > 0 ? $", {p.Errors:N0} errors" : "");
    }
}
