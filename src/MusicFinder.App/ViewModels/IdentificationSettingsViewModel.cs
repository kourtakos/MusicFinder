using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.ViewModels;

/// <summary>The "Online identification" section of Settings. Everything is saved the moment it changes.</summary>
public sealed partial class IdentificationSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IUserDialogs _dialogs;
    private readonly IIdentificationService _service;
    private readonly IIdentificationCache _cache;
    private readonly TimeProvider _time;
    private bool _loading;

    public IdentificationSettingsViewModel(ISettingsStore store, IUserDialogs dialogs, IIdentificationService service,
        IIdentificationCache cache, TimeProvider time)
    {
        _store = store;
        _dialogs = dialogs;
        _service = service;
        _cache = cache;
        _time = time;
        _loading = true;
        var s = store.Load();
        _enabled = s.IdentificationEnabled && s.IdentificationConsentUtc is not null;
        _contact = s.MusicBrainzContact ?? "";
        _apiKey = s.AcoustIdApiKey ?? "";
        _fpcalcPath = s.FpcalcPath ?? "";
        _suggestGenres = s.SuggestGenresFromMusicBrainz;
        _useCoverArt = s.UseCoverArtArchive;
        _loading = false;
        RefreshStatus();
    }

    public string WhatIsSent => IdentificationText.WhatIsSent;
    public string NonCommercial => IdentificationText.FreeForNonCommercialUse;
    public string KeyHelp => IdentificationText.KeyHelp;
    public string Attribution => IdentificationText.Attribution;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _contact;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string _fpcalcPath;
    [ObservableProperty] private bool _suggestGenres;
    [ObservableProperty] private bool _useCoverArt;
    [ObservableProperty] private string _fingerprinterStatus = "";
    [ObservableProperty] private string _readinessText = "";
    [ObservableProperty] private string _cacheText = "";

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        if (value && _store.Load().IdentificationConsentUtc is null)
        {
            // First time: the user must see exactly what is sent. Declining leaves everything off.
            var consent = new IdentificationConsentViewModel(_store, _time);
            _dialogs.ShowIdentificationConsent(consent);
            if (!consent.Accepted)
            {
                _loading = true;
                Enabled = false;
                _loading = false;
                return;
            }
            Reload();   // the consent dialog saved the contact and the key
            return;
        }
        Save(s => s.IdentificationEnabled = value);
        RefreshStatus();
    }

    partial void OnContactChanged(string value) { if (!_loading) { Save(s => s.MusicBrainzContact = NullIfBlank(value)); RefreshStatus(); } }
    partial void OnApiKeyChanged(string value) { if (!_loading) { Save(s => s.AcoustIdApiKey = NullIfBlank(value)); RefreshStatus(); } }
    partial void OnFpcalcPathChanged(string value) { if (!_loading) { Save(s => s.FpcalcPath = NullIfBlank(value)); RefreshStatus(); } }
    partial void OnSuggestGenresChanged(bool value) { if (!_loading) Save(s => s.SuggestGenresFromMusicBrainz = value); }
    partial void OnUseCoverArtChanged(bool value) { if (!_loading) Save(s => s.UseCoverArtArchive = value); }

    [RelayCommand]
    private void BrowseFpcalc()
    {
        var path = _dialogs.PickFile("Choose fpcalc.exe", "fpcalc|fpcalc.exe|Programs|*.exe");
        if (path is not null) FpcalcPath = path;
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _service.ClearCacheAsync(default);
        RefreshStatus();
    }

    /// <summary>Re-reads what the settings file holds (after the consent dialog saved it) and updates the status lines.</summary>
    public void Reload()
    {
        _loading = true;
        var s = _store.Load();
        Enabled = s.IdentificationEnabled && s.IdentificationConsentUtc is not null;
        Contact = s.MusicBrainzContact ?? "";
        ApiKey = s.AcoustIdApiKey ?? "";
        _loading = false;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var r = _service.GetReadiness();
        FingerprinterStatus = r.Fingerprinter is { } f ? $"Found: {f.Path} ({f.Source})" : "fpcalc.exe not found. Audio fingerprints are unavailable; searching by tags still works.";
        ReadinessText = !r.Enabled || !r.Consented ? "Online identification is off. Nothing is sent anywhere."
            : !r.HasContact ? "Add a contact address: MusicBrainz requires it."
            : r.CanFingerprint ? "Ready: audio fingerprints and searching by tags."
            : r.HasApiKey ? "Ready for searching by tags. Audio fingerprints need fpcalc.exe."
            : "Ready for searching by tags. Audio fingerprints need an AcoustID key and fpcalc.exe.";
        _ = UpdateCacheTextAsync();
    }

    private async Task UpdateCacheTextAsync()
    {
        try
        {
            var n = await _cache.CountAsync(default);
            CacheText = n == 0 ? "Nothing is remembered." : $"{n:N0} remembered answers on this computer.";
        }
        catch (Exception) { CacheText = ""; }
    }

    private void Save(Action<Domain.Models.AppSettings> change)
    {
        var s = _store.Load();
        change(s);
        _store.Save(s);
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
