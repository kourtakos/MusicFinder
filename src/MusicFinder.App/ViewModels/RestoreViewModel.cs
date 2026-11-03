using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Application.Backup;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One library folder in the backup, and where it is on this PC.</summary>
public sealed partial class RestoreMappingViewModel : ObservableObject
{
    private readonly IUserDialogs _dialogs;

    public BackupRoot Root { get; }
    public string OldPath => Root.Path;
    public string TracksText { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FoundText), nameof(IsChanged))] private string _newPath;

    public RestoreMappingViewModel(BackupRoot root, IUserDialogs dialogs)
    {
        Root = root;
        _dialogs = dialogs;
        _newPath = root.Path;
        TracksText = root.TrackCount == 1 ? Loc.T("1 track") : Loc.T("{0:N0} tracks", root.TrackCount);
    }

    public bool IsChanged => !string.Equals(PathRemapper.Normalize(NewPath), PathRemapper.Normalize(OldPath), StringComparison.OrdinalIgnoreCase);
    public string FoundText => Directory.Exists(NewPath) ? Loc.T("Found on this PC") : Loc.T("Not found on this PC");

    [RelayCommand]
    private void Browse()
    {
        var picked = _dialogs.PickFolder();
        if (!string.IsNullOrWhiteSpace(picked)) NewPath = picked;
    }
}

/// <summary>
/// Restoring a backup: what it contains, what it would replace, where each music folder is now. Confirming makes a safety backup of the
/// current library and prepares the restore; the library itself is swapped when MusicFinder starts the next time.
/// </summary>
public sealed partial class RestoreViewModel : ObservableObject
{
    public static string NotTheMusicText => Loc.T("A backup holds your index, tags, genres, history and settings. It does not contain your music files.");

    private readonly string _archivePath;
    private readonly ILibraryRestoreService _service;
    private readonly IUserDialogs _dialogs;
    private readonly IAppRestarter _restarter;

    public RestorePreview Preview { get; }
    public bool IsUndo { get; }
    public ObservableCollection<RestoreMappingViewModel> Mappings { get; } = [];
    public IReadOnlyList<string> CountLines { get; }
    public string Heading => IsUndo ? Loc.T("Go back to the library from before the last restore") : Loc.T("Restore a backup");
    public string CreatedText { get; }
    public string SchemaText { get; }
    public bool IsRefused => Preview.Schema == SchemaVerdict.TooNew;
    public string ImpactText =>
        Loc.T("Your current library (index, tags, genres and play history) will be replaced by the one in this backup. A full safety backup of it is made first and kept in the Backups folder, and the old database file is kept next to the new one. Your music files are not touched.");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRestore))] private bool _confirmed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRestore))] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRestore))] private bool _isStaged;
    [ObservableProperty] private string _statusText = "";

    public bool CanRestore => Confirmed && !IsBusy && !IsStaged && !IsRefused;

    public event EventHandler? CloseRequested;

    public RestoreViewModel(string archivePath, RestorePreview preview, ILibraryRestoreService service, IUserDialogs dialogs, IAppRestarter restarter, bool isUndo = false)
    {
        _archivePath = archivePath;
        Preview = preview;
        _service = service;
        _dialogs = dialogs;
        _restarter = restarter;
        IsUndo = isUndo;
        foreach (var r in preview.Roots) Mappings.Add(new RestoreMappingViewModel(r.Root, dialogs));
        CreatedText = Loc.T("Made {0} by MusicFinder {1}", preview.Manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), preview.Manifest.AppVersion);
        CountLines = preview.Manifest.Counts.OrderBy(c => c.Key).Select(c =>
            Loc.T("{0}: {1:N0} in the backup, {2} now", Label(c.Key), c.Value, preview.CurrentCounts.TryGetValue(c.Key, out var now) ? now.ToString("N0", CultureInfo.CurrentCulture) : Loc.T("none"))).ToList();
        SchemaText = preview.Schema switch
        {
            SchemaVerdict.TooNew => Loc.T("This backup was made by a newer MusicFinder and cannot be restored here. Update MusicFinder first."),
            SchemaVerdict.WillBeUpgraded => Loc.T("This backup is from an older version. It is brought up to date while it is prepared."),
            _ => "",
        };
        if (IsRefused) StatusText = SchemaText;
    }

    private static string Label(string table) => table switch
    {
        "Tracks" => Loc.T("Tracks"), "Folders" => Loc.T("Folders"), "Genres" => Loc.T("Genres"), "GenreAliases" => Loc.T("Genre aliases"), "CustomTags" => Loc.T("DJ tags"),
        "DjSessions" => Loc.T("DJ sessions"), "PlayHistory" => Loc.T("Play history entries"), "TrackEvents" => Loc.T("Track events"),
        "IdentificationCache" => Loc.T("Identification lookups"), "FileOperations" => Loc.T("Rename and move records"), _ => table,
    };

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        StatusText = Loc.T("Making a safety backup of your current library and preparing the restore…");
        try
        {
            var mappings = Mappings.Where(m => m.IsChanged).Select(m => new PathMapping(m.OldPath, m.NewPath)).ToList();
            var staged = await _service.StageAsync(new RestoreRequest(_archivePath, mappings), CancellationToken.None);
            IsStaged = true;
            StatusText = staged.TracksRemapped > 0
                ? Loc.T("Ready. Your current library was saved to {0}. Restart MusicFinder to finish the restore ({1:N0} track paths will point to the new folders).",
                    Path.GetFileName(staged.SafetyBackupPath), staged.TracksRemapped)
                : Loc.T("Ready. Your current library was saved to {0}. Restart MusicFinder to finish the restore.", Path.GetFileName(staged.SafetyBackupPath));
        }
        catch (RestoreRefusedException ex) { StatusText = ex.Message; }
        catch (BackupFormatException ex) { StatusText = ex.Message; }
        catch (Exception ex) { StatusText = Loc.T("The restore could not be prepared and nothing was changed: {0}", ex.Message); }
        finally
        {
            IsBusy = false;
            RestoreCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RestartNow() => _restarter.Restart();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
