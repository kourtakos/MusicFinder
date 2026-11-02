using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.ViewModels;

public sealed partial class RootItemViewModel : ObservableObject
{
    private readonly LibraryRootService _service;

    public long Id { get; }
    public string Path { get; }
    public string StatusText { get; }
    public string LastScanText { get; }

    [ObservableProperty] private bool _enabled;

    public RootItemViewModel(LibraryRoot root, LibraryRootService service)
    {
        _service = service;
        Id = root.Id;
        Path = root.Path;
        _enabled = root.Enabled;
        StatusText = root.Status.ToString();
        LastScanText = root.LastScanUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "never";
    }

    async partial void OnEnabledChanged(bool value) => await _service.SetEnabledAsync(Id, value);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ILibraryRootRepository _roots;
    private readonly LibraryRootService _rootService;
    private readonly IUserDialogs _dialogs;
    private readonly ISettingsStore _store;
    private readonly ScanCoordinator _scans;
    private readonly Services.Artwork.ArtworkService _artwork;

    public ObservableCollection<RootItemViewModel> Roots { get; } = [];
    public string DatabasePath { get; }
    public string LogDirectory { get; }
    public string SettingsPath { get; }

    [ObservableProperty] private int _readerConcurrency;
    [ObservableProperty] private bool _useFolderArtwork;

    public SettingsViewModel(ILibraryRootRepository roots, LibraryRootService rootService, IUserDialogs dialogs,
        ISettingsStore store, AppPaths paths, ScanCoordinator scans, Services.Artwork.ArtworkService artwork,
        Services.Hotkeys.QuickSearchService quickSearch, ISearchAliasService aliasService, ISearchDiagnostics diagnostics)
    {
        _roots = roots;
        _rootService = rootService;
        _dialogs = dialogs;
        _store = store;
        _scans = scans;
        _artwork = artwork;
        _quickSearch = quickSearch;
        _aliasService = aliasService;
        _diagnostics = diagnostics;
        DatabasePath = paths.DatabasePath;
        LogDirectory = paths.LogDir;
        SettingsPath = paths.SettingsPath;
        var saved = store.Load();
        _readerConcurrency = saved.ReaderConcurrency;
        _useFolderArtwork = saved.UseFolderArtwork;
        _sessionHeuristicSeconds = Math.Clamp(saved.SessionHeuristicSeconds, 15, 20);
        _autoStartSessionOnDrag = saved.AutoStartSessionOnDrag;
        _loadingQuickSearch = true;
        _quickSearchEnabled = saved.QuickSearchEnabled;
        _quickSearchHotkey = saved.QuickSearchHotkey;
        _loadingQuickSearch = false;
        _loadingSearch = true;
        _smartGreekSearch = saved.SmartGreekSearch;
        _fuzzySearch = saved.FuzzySearch;
        _loadingSearch = false;
        _hotkeyStatus = quickSearch.StatusText;
        _hotkeyStatusIsError = quickSearch.StatusIsError;
        quickSearch.StatusChanged += (_, _) => ShowHotkeyStatus(); // e.g. registration failed at start-up, after this page was created

        _rootService.RootsChanged += async (_, _) => await ReloadAsync();
        _scans.ScanFinished += async (_, _) => await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var roots = await Task.Run(() => _roots.GetAllAsync());
        Roots.Clear();
        foreach (var root in roots) Roots.Add(new RootItemViewModel(root, _rootService));
        await LoadSearchAliasesAsync();
    }

    partial void OnUseFolderArtworkChanged(bool value)
    {
        _artwork.UseFolderArtwork = value; // the library view is rebuilt when you return to it, so covers update then
        var settings = _store.Load();
        settings.UseFolderArtwork = value;
        _store.Save(settings);
    }

    [ObservableProperty] private int _sessionHeuristicSeconds;
    [ObservableProperty] private bool _autoStartSessionOnDrag;

    partial void OnSessionHeuristicSecondsChanged(int value)
    {
        // The heuristic is only meaningful between 15 and 20 s (CLAUDE.md section 9); show what was really saved.
        var clamped = Math.Clamp(value, 15, 20);
        var settings = _store.Load();
        settings.SessionHeuristicSeconds = clamped;
        _store.Save(settings);
        if (clamped != value) SessionHeuristicSeconds = clamped;
    }

    partial void OnAutoStartSessionOnDragChanged(bool value)
    {
        var settings = _store.Load();
        settings.AutoStartSessionOnDrag = value;
        _store.Save(settings);
    }

    // ===== Quick search shortcut =====

    private readonly Services.Hotkeys.QuickSearchService _quickSearch;
    private bool _loadingQuickSearch;

    [ObservableProperty] private bool _quickSearchEnabled;
    [ObservableProperty] private string _quickSearchHotkey = "";
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private bool _hotkeyStatusIsError;

    public string DefaultHotkey => MusicFinder.Application.Hotkeys.HotkeyGesture.DefaultText;

    partial void OnQuickSearchEnabledChanged(bool value)
    {
        if (_loadingQuickSearch) return;
        if (!MusicFinder.Application.Hotkeys.HotkeyGesture.TryParse(QuickSearchHotkey, out var gesture, out _)) gesture = MusicFinder.Application.Hotkeys.HotkeyGesture.Default;
        var result = _quickSearch.Apply(value, gesture);
        ShowHotkeyStatus();
        if (!result.Success && value)
        {
            // It could not be switched on (the keys are taken): show it as off rather than pretending.
            _loadingQuickSearch = true;
            QuickSearchEnabled = false;
            _loadingQuickSearch = false;
        }
    }

    /// <summary>
    /// The user pressed a new shortcut. It is checked, registered with Windows and only then saved and shown; any problem is
    /// reported in plain language and the previous shortcut stays in force.
    /// </summary>
    public bool TryChangeHotkey(string text)
    {
        if (!MusicFinder.Application.Hotkeys.HotkeyGesture.TryParse(text, out var gesture, out var error))
        {
            HotkeyStatus = error;
            HotkeyStatusIsError = true;
            return false;
        }

        var result = _quickSearch.Apply(QuickSearchEnabled, gesture);
        if (result.Success) QuickSearchHotkey = gesture.ToString();
        ShowHotkeyStatus();
        return result.Success;
    }

    [RelayCommand]
    private void ResetHotkey() => TryChangeHotkey(DefaultHotkey);

    private void ShowHotkeyStatus()
    {
        HotkeyStatus = _quickSearch.StatusText;
        HotkeyStatusIsError = _quickSearch.StatusIsError;
    }

    // ===== Search: smart Greek matching, typo matching, own aliases, diagnostics =====

    private readonly ISearchAliasService _aliasService;
    private readonly ISearchDiagnostics _diagnostics;
    private bool _loadingSearch;

    public ObservableCollection<SearchAliasItem> SearchAliases { get; } = [];

    [ObservableProperty] private bool _smartGreekSearch;
    [ObservableProperty] private bool _fuzzySearch;
    [ObservableProperty] private string _newAliasSource = "";
    [ObservableProperty] private string _newAliasAlias = "";
    [ObservableProperty] private string? _aliasError;
    [ObservableProperty] private string _explainQuery = "";
    [ObservableProperty] private string _explainOutput = "";

    partial void OnSmartGreekSearchChanged(bool value)
    {
        if (_loadingSearch) return;
        var s = _store.Load();
        s.SmartGreekSearch = value;
        _store.Save(s);
    }

    partial void OnFuzzySearchChanged(bool value)
    {
        if (_loadingSearch) return;
        var s = _store.Load();
        s.FuzzySearch = value;
        _store.Save(s);
    }

    public async Task LoadSearchAliasesAsync()
    {
        var all = await Task.Run(() => _aliasService.GetAllAsync());
        SearchAliases.Clear();
        foreach (var a in all) SearchAliases.Add(new SearchAliasItem(a));
    }

    [RelayCommand]
    private async Task AddSearchAlias()
    {
        var result = await _aliasService.AddAsync(NewAliasSource, NewAliasAlias);
        AliasError = result.Error;
        if (!result.Success) return;
        NewAliasSource = "";
        NewAliasAlias = "";
        await LoadSearchAliasesAsync();
    }

    [RelayCommand]
    private async Task RemoveSearchAlias(SearchAliasItem? item)
    {
        if (item is null) return;
        await _aliasService.RemoveAsync(item.Id);
        await LoadSearchAliasesAsync();
    }

    /// <summary>Shows how a query is read and why each result matched, in plain text. For finding out why a name is (not) found.</summary>
    [RelayCommand]
    private async Task ExplainSearch()
    {
        try
        {
            var x = await Task.Run(() => _diagnostics.ExplainAsync(ExplainQuery));
            ExplainOutput = SearchExplanationFormatter.Format(x);
        }
        catch (Exception ex) { ExplainOutput = "Could not explain this search: " + ex.Message; }
    }

    partial void OnReaderConcurrencyChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 8);
        var settings = _store.Load();
        settings.ReaderConcurrency = clamped;
        _store.Save(settings);
    }

    [RelayCommand]
    private async Task AddRootAsync()
    {
        var path = _dialogs.PickFolder();
        if (path is null) return;
        var result = await _rootService.AddAsync(path);
        if (!result.Success) _dialogs.ShowError("Cannot add folder", result.Message!);
    }

    [RelayCommand]
    private Task RescanAllAsync() => _rootService.RescanAllAsync();

    [RelayCommand]
    private void RescanRoot(RootItemViewModel? root)
    {
        if (root is { Enabled: true }) _rootService.Rescan(root.Id);
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _dialogs.RevealInExplorer(path);
    }
}
