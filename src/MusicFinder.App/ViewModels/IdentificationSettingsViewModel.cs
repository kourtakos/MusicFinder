using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;

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
        _embedCoverByDefault = s.EmbedCoverByDefault;
        _genreMode = GenreModes.First(m => m.Mode == s.MusicBrainzGenreMode);
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
    [ObservableProperty] private bool _embedCoverByDefault;
    [ObservableProperty] private GenreModeOption _genreMode;

    public static IReadOnlyList<GenreModeOption> GenreModes { get; } =
    [
        new(GenreApplyMode.Add, Loc.T("Add them to the genres the file already has (ticked)")),
        new(GenreApplyMode.Fill, Loc.T("Only fill a file that has no genre")),
        new(GenreApplyMode.Replace, Loc.T("Replace the file's genres (ticked)"))
    ];
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
    partial void OnGenreModeChanged(GenreModeOption value) { if (!_loading) Save(s => s.MusicBrainzGenreMode = value.Mode); }
    partial void OnEmbedCoverByDefaultChanged(bool value) { if (!_loading) Save(s => s.EmbedCoverByDefault = value); }

    [RelayCommand]
    private void BrowseFpcalc()
    {
        var path = _dialogs.PickFile(Loc.T("Choose fpcalc.exe"), "fpcalc|fpcalc.exe|Programs|*.exe");
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
        FingerprinterStatus = r.Fingerprinter is { } f ? Loc.T("Found: {0} ({1})", f.Path, f.Source) : Loc.T("fpcalc.exe not found. Audio fingerprints are unavailable; searching by tags still works.");
        ReadinessText = !r.Enabled || !r.Consented ? Loc.T("Online identification is off. Nothing is sent anywhere.")
            : !r.HasContact ? Loc.T("Add a contact address: MusicBrainz requires it.")
            : r.CanFingerprint ? Loc.T("Ready: audio fingerprints and searching by tags.")
            : r.HasApiKey ? Loc.T("Ready for searching by tags. Audio fingerprints need fpcalc.exe.")
            : Loc.T("Ready for searching by tags. Audio fingerprints need an AcoustID key and fpcalc.exe.");
        _ = UpdateCacheTextAsync();
    }

    private async Task UpdateCacheTextAsync()
    {
        try
        {
            var n = await _cache.CountAsync(default);
            CacheText = n == 0 ? Loc.T("Nothing is remembered.") : Loc.T("{0:N0} remembered answers on this computer.", n);
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

/// <summary>One choice of how MusicBrainz genres meet the genres a file already has.</summary>
public sealed record GenreModeOption(GenreApplyMode Mode, string Label);
