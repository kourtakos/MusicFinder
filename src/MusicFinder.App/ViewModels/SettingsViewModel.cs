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
        StatusText = root.Status switch
        {
            Domain.Enums.ScanStatus.Pending => Localization.Loc.T("Pending"),
            Domain.Enums.ScanStatus.Scanning => Localization.Loc.T("Scanning"),
            Domain.Enums.ScanStatus.Indexed => Localization.Loc.T("Indexed"),
            Domain.Enums.ScanStatus.Cancelled => Localization.Loc.T("Cancelled"),
            Domain.Enums.ScanStatus.Error => Localization.Loc.T("Error"),
            Domain.Enums.ScanStatus.Offline => Localization.Loc.T("Offline"),
            _ => root.Status.ToString()
        };
        LastScanText = root.LastScanUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? Localization.Loc.T("never");
    }

    async partial void OnEnabledChanged(bool value) => await _service.SetEnabledAsync(Id, value);
}

/// <summary>One entry of the language box: "auto", "en" or "el".</summary>
public sealed record LanguageOption(string Code, string Label);

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
    public IdentificationSettingsViewModel Identification { get; }
    public AnalysisSettingsViewModel Analysis { get; }
    public BackupSettingsViewModel Backup { get; }
    public PreviewViewModel Preview { get; }
    public HistoryImportSettingsViewModel History { get; }

    [ObservableProperty] private int _readerConcurrency;
    [ObservableProperty] private bool _useFolderArtwork;

    public SettingsViewModel(ILibraryRootRepository roots, LibraryRootService rootService, IUserDialogs dialogs,
        ISettingsStore store, AppPaths paths, ScanCoordinator scans, Services.Artwork.ArtworkService artwork,
        Services.Hotkeys.QuickSearchService quickSearch, ISearchAliasService aliasService, ISearchDiagnostics diagnostics,
        IdentificationSettingsViewModel identification, AnalysisSettingsViewModel analysis, BackupSettingsViewModel backup, PreviewViewModel preview,
        HistoryImportSettingsViewModel history, FirstRunViewModel firstRun, IAppRestarter restarter)
    {
        _restarter = restarter;
        _firstRun = firstRun;
        History = history;
        Backup = backup;
        Preview = preview;
        _roots = roots;
        _rootService = rootService;
        _dialogs = dialogs;
        _store = store;
        _scans = scans;
        _artwork = artwork;
        _quickSearch = quickSearch;
        _aliasService = aliasService;
        _diagnostics = diagnostics;
        Identification = identification;
        Analysis = analysis;
        DatabasePath = paths.DatabasePath;
        LogDirectory = paths.LogDir;
        SettingsPath = paths.SettingsPath;
        var saved = store.Load();
        _readerConcurrency = saved.ReaderConcurrency;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == (saved.Language ?? Localization.Loc.Auto).ToLowerInvariant()) ?? Languages[0];
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

        UiZoom.Changed += OnZoomChanged;
        _rootService.RootsChanged += async (_, _) => await ReloadAsync();
        _scans.ScanFinished += async (_, _) => await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var roots = await Task.Run(() => _roots.GetAllAsync());
        Roots.Clear();
        foreach (var root in roots) Roots.Add(new RootItemViewModel(root, _rootService));
        await LoadSearchAliasesAsync();
        await History.ReloadAsync();
    }

    partial void OnUseFolderArtworkChanged(bool value)
    {
        _artwork.UseFolderArtwork = value; // the library view is rebuilt when you return to it, so covers update then
        var settings = _store.Load();
        settings.UseFolderArtwork = value;
        _store.Save(settings);
    }

    // ===== Welcome tour and song-recognition setup =====

    private readonly FirstRunViewModel _firstRun;

    [RelayCommand]
    private void ShowTour()
    {
        _firstRun.Open(toRecognition: false);
        _dialogs.ShowFirstRun(_firstRun);
    }

    [RelayCommand]
    private void SetUpRecognition()
    {
        _firstRun.Open(toRecognition: true);
        _dialogs.ShowFirstRun(_firstRun);
    }

    // ===== Language =====

    private readonly IAppRestarter _restarter;

    /// <summary>What the language box offers. The names of the languages stay in their own language so they can always be found.</summary>
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(Localization.Loc.Auto, Localization.Loc.T("Automatic (Windows language)")),
        new(Localization.Loc.English, "English"),
        new(Localization.Loc.Greek, "Ελληνικά"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        var settings = _store.Load();
        if (string.Equals(settings.Language, value.Code, StringComparison.OrdinalIgnoreCase)) return;
        settings.Language = value.Code;
        _store.Save(settings);
        // The language is read once at start-up, so it applies after a restart; nothing is lost by restarting (the library is on disk).
        if (_dialogs.Confirm(Localization.Loc.T("Language"), Localization.Loc.T("The language changes the next time MusicFinder starts. Restart it now?")))
            _restarter.Restart();
    }

    // ===== Text size (zoom of the whole interface) =====

    [ObservableProperty] private int _uiZoomPercent = MusicFinder.Application.Display.ZoomLevel.ToPercent(UiZoom.Level);

    /// <summary>The slider's top: what this screen can show without cutting the layout.</summary>
    public int UiZoomMaxPercent => MusicFinder.Application.Display.ZoomLevel.ToPercent(UiZoom.MaxLevel);

    partial void OnUiZoomPercentChanged(int value) => UiZoom.Set(MusicFinder.Application.Display.ZoomLevel.FromPercent(value));

    [RelayCommand]
    private void ResetZoom() => UiZoom.Set(MusicFinder.Application.Display.ZoomLevel.Default);

    /// <summary>Keeps the slider in step when the zoom is changed with Ctrl+wheel or the keyboard.</summary>
    private void OnZoomChanged(object? sender, EventArgs e) => UiZoomPercent = MusicFinder.Application.Display.ZoomLevel.ToPercent(UiZoom.Level);

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
        catch (Exception ex) { ExplainOutput = Localization.Loc.T("Could not explain this search: {0}", ex.Message); }
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
        if (!result.Success) _dialogs.ShowError(Localization.Loc.T("Cannot add folder"), result.Message!);
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
