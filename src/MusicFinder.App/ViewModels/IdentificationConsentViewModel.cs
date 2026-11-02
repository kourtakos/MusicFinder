using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.ViewModels;

/// <summary>The words that say, exactly, what online identification sends. Shown before the first use and again in Settings.</summary>
public static class IdentificationText
{
    public const string WhatIsSent =
        "AcoustID receives an acoustic fingerprint of the first 120 seconds of the audio (not the audio itself), the length of the file in seconds " +
        "and your AcoustID application key.\n\n" +
        "MusicBrainz receives the MusicBrainz ids already in your files, or the title, artist and album text of your tags when you search by tags, " +
        "and a User-Agent header with the contact address below, as its rules require.\n\n" +
        "When you look at a release, the Cover Art Archive (coverartarchive.org, which hands the picture over from archive.org) receives that release's MusicBrainz id so it can send its front cover; you can turn this off in Settings.\n\n" +
        "Nothing else is sent: no file names, no folder paths, no play history, no statistics. Answers are remembered on this computer so the same question is not asked twice.";

    public const string FreeForNonCommercialUse =
        "The AcoustID service is free for non-commercial use only (commercial use needs a separate agreement with AcoustID).";

    public const string Attribution =
        "Metadata from MusicBrainz. Audio identification by the AcoustID web service.";

    public const string KeyHelp =
        "A free key for your own copy of MusicFinder: register it at acoustid.org/new-application. Without a key you can still search by tags. " +
        "Audio fingerprints also need Chromaprint's fpcalc.exe.";
}

/// <summary>Asks the user to agree to what is sent, and for the contact address MusicBrainz requires. Nothing is saved unless they accept.</summary>
public sealed partial class IdentificationConsentViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly TimeProvider _time;

    public IdentificationConsentViewModel(ISettingsStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
        var s = store.Load();
        _contact = s.MusicBrainzContact ?? "";
        _apiKey = s.AcoustIdApiKey ?? "";
    }

    public string Heading => "Turn on online identification?";
    public string WhatIsSent => IdentificationText.WhatIsSent;
    public string NonCommercial => IdentificationText.FreeForNonCommercialUse;
    public string KeyHelp => IdentificationText.KeyHelp;

    [ObservableProperty] private string _contact;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string? _error;

    public bool Accepted { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Accept()
    {
        if (UserAgentBuilder.Build(new AppInfo("MusicFinder", "0"), Contact) is null)
        {
            Error = "Please enter a contact address (an e-mail or web address) that MusicBrainz can reach you at. Its rules require one.";
            return;
        }
        var s = _store.Load();
        s.IdentificationEnabled = true;
        s.IdentificationConsentUtc = _time.GetUtcNow().UtcDateTime;
        s.MusicBrainzContact = Contact.Trim();
        s.AcoustIdApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        _store.Save(s);
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Decline() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
