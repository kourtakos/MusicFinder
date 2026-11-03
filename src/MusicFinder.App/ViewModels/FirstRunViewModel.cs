using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.App.ViewModels;

/// <summary>One screen of the welcome tour.</summary>
public sealed record TourStep(string Title, string Body);

/// <summary>Something in the Downloads folder that looks like fpcalc, offered with one click.</summary>
public sealed record DownloadCandidate(string Name, string Path, bool IsZip)
{
    public string Label => IsZip ? $"Use the zip {Name}" : $"Use {Name}";
}

/// <summary>Where "Downloads" is. A seam so tests do not look in the real folder.</summary>
public interface IDownloadsFolder
{
    string? Path { get; }
}

public sealed class UserDownloadsFolder : IDownloadsFolder
{
    public string? Path => FpcalcDownloads.DefaultFolder();
}

/// <summary>
/// The welcome tour and the song-recognition setup, one window shown the first time MusicFinder starts and again from Settings. It never blocks
/// the library behind it. Song recognition needs two things MusicFinder cannot bundle: Chromaprint's <c>fpcalc.exe</c> and a free AcoustID
/// application key; this guides the user to both and checks them. Nothing is sent anywhere except by the explicit "Test the key" button.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    public const string ChromaprintPage = "https://acoustid.org/chromaprint";
    public const string KeyPage = "https://acoustid.org/new-application";

    private readonly ISettingsStore _store;
    private readonly IUserDialogs _dialogs;
    private readonly FpcalcLocator _locator;
    private readonly FpcalcInstaller _installer;
    private readonly AcoustIdKeyChecker _checker;
    private readonly IDownloadsFolder _downloads;
    private bool _loading;

    public FirstRunViewModel(ISettingsStore store, IUserDialogs dialogs, IdentificationSettingsViewModel identification, FpcalcLocator locator,
        FpcalcInstaller installer, AcoustIdKeyChecker checker, IDownloadsFolder downloads)
    {
        _store = store;
        _dialogs = dialogs;
        Identification = identification;
        _locator = locator;
        _installer = installer;
        _checker = checker;
        _downloads = downloads;
        Refresh();
    }

    /// <summary>The same settings the Settings page shows (contact address, switch, status), so the two always agree.</summary>
    public IdentificationSettingsViewModel Identification { get; }

    public event EventHandler? CloseRequested;

    // The screens say only what exists in the app today; each line names the real button, menu item or shortcut.
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Add your music",
            "Press the + button in the Folders panel (top left) and pick a folder. MusicFinder reads your files in the background and you can search while it works. " +
            "Your music is never changed unless you confirm, with a preview, an edit or a rename."),
        new("Search",
            "Type an artist, a title or part of a file name in the search box. Greek and Greeklish both work (χαρις finds Χάρις), even when the keyboard was on the wrong layout. " +
            "The buttons next to the box narrow the list by Genre, BPM, Decade, your own Tags and Played. Ctrl+Alt+Space opens a small search window on top of any program."),
        new("Listen",
            "Double-click a track to hear it in the bar at the top; Space pauses. A preview is only a listen and does not count as played. " +
            "Choose which sound card it uses in Settings so it never reaches the main speakers by accident."),
        new("Find BPM and key",
            "Select tracks, right-click and choose Analyze BPM and key (Ctrl+B). The results are estimates, shown first and apart from your tags. " +
            "Nothing is written to a file until you press Write."),
        new("Find similar tracks",
            "Right-click a track and choose Find similar. MusicFinder lists tracks whose genre, tempo and key mix well with it and says why each one matched. " +
            "It compares tags, not how the music sounds, so tracks without BPM or key cannot be compared."),
        new("Use it with VirtualDJ or Traktor",
            "Drag tracks from the list into your DJ program. The Session tab shows what you dragged and what was probably played. " +
            "In Settings, DJ history lets MusicFinder read VirtualDJ's own history so a probable play becomes a confirmed one.")
    ];

    // ===== pages: 0 welcome, 1..N the tour, last = song recognition =====

    public int LastPage => Steps.Count + 1;
    public int PageCount => Steps.Count + 2;

    [ObservableProperty] private int _pageIndex;

    public bool IsWelcome => PageIndex == 0;
    public bool IsRecognition => PageIndex == LastPage;
    public bool IsTourStep => PageIndex > 0 && PageIndex < LastPage;
    /// <summary>The tour screen being shown; an empty one on the other pages, so the bindings always have something to read.</summary>
    public TourStep CurrentStep => IsTourStep ? Steps[PageIndex - 1] : new TourStep("", "");
    public bool CanGoBack => PageIndex > 0;
    public string NextText => PageIndex == LastPage ? "Done" : "Next";
    public string PageText => $"{PageIndex + 1} of {PageCount}";

    partial void OnPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsRecognition));
        OnPropertyChanged(nameof(IsTourStep));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>Starts again from the welcome page, or straight at the song-recognition setup.</summary>
    public void Open(bool toRecognition)
    {
        Refresh();
        PageIndex = toRecognition ? LastPage : 0;
    }

    [RelayCommand]
    private void Next()
    {
        if (PageIndex >= LastPage) Close();
        else PageIndex++;
    }

    [RelayCommand]
    private void Back()
    {
        if (PageIndex > 0) PageIndex--;
    }

    [RelayCommand]
    private void GoToRecognition() => PageIndex = LastPage;

    /// <summary>Done, Skip and the window's close button all end up here: the tour is not shown again by itself.</summary>
    [RelayCommand]
    private void Close()
    {
        MarkCompleted();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remembers that the tour was seen, so it does not open by itself again.</summary>
    public void MarkCompleted()
    {
        var s = _store.Load();
        if (s.FirstRunCompleted) return;
        s.FirstRunCompleted = true;
        _store.Save(s);
    }

    // ===== fpcalc =====

    [ObservableProperty] private bool _fpcalcReady;
    [ObservableProperty] private string _fpcalcStatus = "";
    [ObservableProperty] private string _fpcalcMessage = "";
    [ObservableProperty] private bool _fpcalcMessageIsError;
    [ObservableProperty] private bool _isImporting;

    public ObservableCollection<DownloadCandidate> Candidates { get; } = [];
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Looks again for fpcalc and for a download that could be used. Cheap; called when the window opens and after each change.</summary>
    public void Refresh()
    {
        _loading = true;
        KeyText = _store.Load().AcoustIdApiKey ?? "";
        KeyProblem = "";
        KeyIsValid = AcoustIdKeyFormat.Validate(KeyText).IsValid;
        _loading = false;
        RefreshFpcalc();
    }

    private void RefreshFpcalc()
    {
        var found = _locator.Locate();
        FpcalcReady = found is not null;
        FpcalcStatus = found is null ? "Not found yet." : $"Found: {found.Path}";
        if (found is not null) _ = ShowVersionAsync(found.Path);

        Candidates.Clear();
        if (found is null)
            foreach (var c in FpcalcDownloads.Find(_downloads.Path).Take(3))
                Candidates.Add(new DownloadCandidate(System.IO.Path.GetFileName(c.Path), c.Path, c.Kind == FpcalcCandidateKind.Zip));
        OnPropertyChanged(nameof(HasCandidates));
        Identification.RefreshStatus();
    }

    private async Task ShowVersionAsync(string exe)
    {
        try
        {
            var version = await _installer.ReadVersionAsync(exe, default);
            if (version is not null && FpcalcReady && FpcalcStatus.StartsWith("Found: ", StringComparison.Ordinal) && !FpcalcStatus.Contains("(version"))
                FpcalcStatus += $" (version {version})";
        }
        catch (Exception) { /* the path alone is enough */ }
    }

    [RelayCommand]
    private void OpenFpcalcPage() => _dialogs.OpenUrl(ChromaprintPage);

    [RelayCommand]
    private async Task BrowseFpcalcAsync()
    {
        var path = _dialogs.PickFile("Choose fpcalc.exe or the zip you downloaded", "fpcalc.exe or its zip|fpcalc.exe;*.zip|All files|*.*");
        if (path is not null) await ImportAsync(path);
    }

    [RelayCommand]
    private Task UseCandidateAsync(DownloadCandidate? candidate) => candidate is null ? Task.CompletedTask : ImportAsync(candidate.Path);

    /// <summary>Saves fpcalc from an .exe or a zip into MusicFinder's own tools folder (also used when a file is dropped on the window).</summary>
    public async Task ImportAsync(string path)
    {
        IsImporting = true;
        try
        {
            var result = await _installer.ImportAsync(path, default);
            FpcalcMessageIsError = !result.Ok;
            FpcalcMessage = result.Ok ? $"fpcalc {result.Version} is ready. MusicFinder keeps its own copy; your download was not changed." : result.Error!;
            RefreshFpcalc();
        }
        finally { IsImporting = false; }
    }

    // ===== AcoustID key =====

    [ObservableProperty] private string _keyText = "";
    [ObservableProperty] private string _keyProblem = "";
    [ObservableProperty] private bool _keyIsValid;
    [ObservableProperty] private string _keyResult = "";
    [ObservableProperty] private bool _keyResultOk;
    [ObservableProperty] private bool _isTesting;

    public bool HasKeyProblem => KeyProblem.Length > 0;
    public bool HasKeyResult => KeyResult.Length > 0;

    partial void OnKeyProblemChanged(string value) => OnPropertyChanged(nameof(HasKeyProblem));
    partial void OnKeyResultChanged(string value) => OnPropertyChanged(nameof(HasKeyResult));

    partial void OnKeyTextChanged(string value)
    {
        if (_loading) return;
        var check = AcoustIdKeyFormat.Validate(value);
        KeyIsValid = check.IsValid;
        KeyProblem = string.IsNullOrWhiteSpace(value) ? "" : check.Problem ?? "";
        KeyResult = "";
        // A good key is kept at once, like every Settings field. A half-typed or wrong one never replaces what is saved.
        if (check.IsValid) Identification.ApiKey = check.Key;
        else if (string.IsNullOrWhiteSpace(value)) Identification.ApiKey = "";
    }

    [RelayCommand]
    private void OpenKeyPage() => _dialogs.OpenUrl(KeyPage);

    /// <summary>One real lookup, only after the user picked a song and saw exactly what is sent.</summary>
    [RelayCommand]
    private async Task TestKeyAsync()
    {
        var check = AcoustIdKeyFormat.Validate(KeyText);
        if (!check.IsValid) { SetKeyResult(false, check.Problem!); return; }
        if (!FpcalcReady) { SetKeyResult(false, "Add fpcalc first (step 1): the test looks up a real song, and that needs a fingerprint."); return; }

        var file = _dialogs.PickFile("Choose any song for the test", "Music|*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.wav;*.aiff|All files|*.*");
        if (file is null) return;
        var name = System.IO.Path.GetFileName(file);
        var agreed = _dialogs.Confirm("Test the key",
            $"MusicFinder will make one lookup at api.acoustid.org.\n\nSent: an acoustic fingerprint of \"{name}\" (a short code made from the sound, not the audio itself), the song's length and your key.\n" +
            "Not sent: the file, its name or folder, or anything else about you.\n\nContinue?");
        if (!agreed) return;

        IsTesting = true;
        try
        {
            var result = await _checker.CheckAsync(check.Key, file, default);
            SetKeyResult(result.Outcome == KeyCheckOutcome.Accepted, result.Message);
        }
        finally { IsTesting = false; }
    }

    private void SetKeyResult(bool ok, string message)
    {
        KeyResultOk = ok;
        KeyResult = message;
    }
}
