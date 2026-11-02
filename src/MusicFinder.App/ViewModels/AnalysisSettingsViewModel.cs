using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One way of writing a key into a tag.</summary>
public sealed record KeyNotationOption(KeyNotation Notation, string Label);

/// <summary>The "BPM and key analysis" section of Settings. Everything is saved the moment it changes.</summary>
public sealed partial class AnalysisSettingsViewModel : ObservableObject
{
    public const int MinSeconds = 30;
    public const int MaxSeconds = 240;

    private readonly ISettingsStore _store;
    private readonly IAudioAnalysisService _service;
    private bool _loading;

    public static IReadOnlyList<KeyNotationOption> Notations { get; } =
    [
        new(KeyNotation.Camelot, "Camelot, like 8A (default)"),
        new(KeyNotation.OpenKey, "Open Key, like 1m"),
        new(KeyNotation.NoteName, "Note name, like Am")
    ];

    public AnalysisSettingsViewModel(ISettingsStore store, IAudioAnalysisService service)
    {
        _store = store;
        _service = service;
        _loading = true;
        var s = store.Load();
        _selectedNotation = Notations.First(n => n.Notation == s.AnalysisKeyNotation);
        _seconds = Math.Clamp(s.AnalysisSeconds, MinSeconds, MaxSeconds);
        _concurrency = Math.Clamp(s.AnalysisConcurrency, 1, 2);
        _allowOverwriteDefault = s.AnalysisAllowOverwriteDefault;
        _loading = false;
        _ = RefreshCacheTextAsync();
    }

    [ObservableProperty] private KeyNotationOption _selectedNotation;
    [ObservableProperty] private int _seconds;
    [ObservableProperty] private int _concurrency;
    [ObservableProperty] private bool _allowOverwriteDefault;
    [ObservableProperty] private string _cacheText = "";

    partial void OnSelectedNotationChanged(KeyNotationOption value) { if (!_loading) Save(s => s.AnalysisKeyNotation = value.Notation); }

    partial void OnSecondsChanged(int value)
    {
        if (_loading) return;
        var clamped = Math.Clamp(value, MinSeconds, MaxSeconds);
        Save(s => s.AnalysisSeconds = clamped);
        if (clamped != value) Seconds = clamped;                         // show what was really saved
    }

    partial void OnConcurrencyChanged(int value)
    {
        if (_loading) return;
        var clamped = Math.Clamp(value, 1, 2);
        Save(s => s.AnalysisConcurrency = clamped);
        if (clamped != value) Concurrency = clamped;
    }

    partial void OnAllowOverwriteDefaultChanged(bool value) { if (!_loading) Save(s => s.AnalysisAllowOverwriteDefault = value); }

    [RelayCommand]
    private async Task ClearEstimatesAsync()
    {
        await _service.ClearAsync(default);
        await RefreshCacheTextAsync();
    }

    public async Task RefreshCacheTextAsync()
    {
        try
        {
            var n = await _service.CountAsync(default);
            CacheText = n == 0 ? "No estimates are remembered." : $"{n:N0} tracks have remembered estimates.";
        }
        catch (Exception) { CacheText = ""; }
    }

    private void Save(Action<Domain.Models.AppSettings> change)
    {
        var s = _store.Load();
        change(s);
        _store.Save(s);
    }
}
