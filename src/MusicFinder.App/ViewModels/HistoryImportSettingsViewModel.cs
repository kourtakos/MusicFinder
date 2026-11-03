using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One DJ program in Settings: where its history is read from, and the last result.</summary>
public sealed partial class HistoryProgramViewModel : ObservableObject
{
    private readonly IHistoryImporter _importer;
    private readonly IHistoryImportService _service;
    private readonly IUserDialogs _dialogs;
    private readonly ISettingsStore _store;
    private readonly Func<Task> _runOne;

    public HistoryProgramViewModel(IHistoryImporter importer, IHistoryImportService service, IUserDialogs dialogs, ISettingsStore store, Func<Task> runOne)
    {
        _importer = importer;
        _service = service;
        _dialogs = dialogs;
        _store = store;
        _runOne = runOne;
        Refresh();
    }

    public string ProviderId => _importer.ProviderId;
    public string DisplayName => _importer.DisplayName;

    /// <summary>"Written while you play" for VirtualDJ; "Sync after export/close" for Traktor (never promised to be live).</summary>
    public string SyncText => _importer.SyncMode == HistorySyncMode.Live ? "Read while you play" : "Sync after export/close";

    [ObservableProperty] private string _folderText = "";
    [ObservableProperty] private string _folderHint = "";
    [ObservableProperty] private bool _hasChosenFolder;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _resultIsError;
    [ObservableProperty] private bool _isBusy;

    private string? Chosen => ProviderId == PlaySources.VirtualDj ? _store.Load().VirtualDjHistoryFolder : _store.Load().TraktorHistoryFolder;

    /// <summary>Shows the folder in use and where it came from.</summary>
    public void Refresh()
    {
        var chosen = Chosen;
        HasChosenFolder = !string.IsNullOrWhiteSpace(chosen);
        var resolved = _service.ResolveFolder(ProviderId);
        FolderText = resolved ?? "No folder found";
        FolderHint = HasChosenFolder
            ? "The folder you chose."
            : resolved is null ? $"{DisplayName}'s history folder was not found. Choose it with Browse." : "Found automatically.";
    }

    private void Save(string? folder)
    {
        var settings = _store.Load();
        if (ProviderId == PlaySources.VirtualDj) settings.VirtualDjHistoryFolder = folder;
        else settings.TraktorHistoryFolder = folder;
        _store.Save(settings);
        Refresh();
    }

    [RelayCommand]
    private void Browse()
    {
        var path = _dialogs.PickFolder();
        if (!string.IsNullOrWhiteSpace(path)) Save(path);
    }

    [RelayCommand]
    private void UseDetected() => Save(null);

    [RelayCommand]
    private async Task ImportNowAsync()
    {
        IsBusy = true;
        try { await _runOne(); }
        finally { IsBusy = false; }
    }

    public void Show(HistoryImportResult result)
    {
        ResultIsError = result.Error is not null;
        ResultText = result.Summary;
        Refresh();
    }
}

/// <summary>Settings > DJ history: choose each program's history folder and import now.</summary>
public sealed partial class HistoryImportSettingsViewModel : ObservableObject
{
    private readonly IHistoryImportService _service;
    private readonly IHistoryImportStore _store;
    private readonly ISettingsStore _settings;
    private bool _loading;

    public HistoryImportSettingsViewModel(IHistoryImportService service, IHistoryImportStore store, IUserDialogs dialogs, ISettingsStore settings)
    {
        _service = service;
        _store = store;
        _settings = settings;
        foreach (var importer in service.Importers)
            Programs.Add(new HistoryProgramViewModel(importer, service, dialogs, settings, () => RunAsync(importer.ProviderId)));
        _loading = true;
        _autoImport = settings.Load().HistoryAutoImport;
        _loading = false;
        service.ImportCompleted += (_, results) => OnCompleted(results);
    }

    public ObservableCollection<HistoryProgramViewModel> Programs { get; } = [];

    [ObservableProperty] private bool _autoImport;
    [ObservableProperty] private string _pendingText = "";

    partial void OnAutoImportChanged(bool value)
    {
        if (_loading) return;
        var s = _settings.Load();
        s.HistoryAutoImport = value;
        _settings.Save(s);
    }

    /// <summary>Refreshes the folder lines and the count of plays that found no track. Call when the page is shown.</summary>
    public async Task ReloadAsync()
    {
        foreach (var p in Programs) p.Refresh();
        await UpdatePendingAsync();
    }

    private async Task RunAsync(string providerId)
    {
        try { await _service.RunAsync(providerId); }
        catch (Exception ex) { Programs.FirstOrDefault(p => p.ProviderId == providerId)?.Show(new HistoryImportResult(providerId, providerId, null, 0, 0, 0, 0, 0, 0, 0, [], ex.Message)); }
    }

    private void OnCompleted(IReadOnlyList<HistoryImportResult> results)
    {
        // May arrive on a background thread (the timer); the page's properties belong to the UI thread.
        void Apply()
        {
            foreach (var r in results) Programs.FirstOrDefault(p => p.ProviderId == r.ProviderId)?.Show(r);
            _ = UpdatePendingAsync();
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    private async Task UpdatePendingAsync()
    {
        var count = await _store.CountPendingAsync();
        PendingText = count == 0 ? "" : $"{count} play{(count == 1 ? "" : "s")} from your DJ software could not be matched to a track in the library.";
    }
}
