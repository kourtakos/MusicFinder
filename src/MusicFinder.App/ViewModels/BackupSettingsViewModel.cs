using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;
using MusicFinder.Infrastructure.Backup;

namespace MusicFinder.App.ViewModels;

/// <summary>The "Backup and restore" part of Settings.</summary>
public sealed partial class BackupSettingsViewModel : ObservableObject
{
    private readonly ILibraryBackupService _backup;
    private readonly ILibraryRestoreService _restore;
    private readonly IUserDialogs _dialogs;
    private readonly IAppRestarter _restarter;
    private readonly AppPaths _paths;

    public string NotTheMusicText => RestoreViewModel.NotTheMusicText;

    [ObservableProperty] private bool _includeSecrets;
    [ObservableProperty] private bool _includeArtwork;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAct))] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPending))] private bool _pending;
    [ObservableProperty] private bool _canUndoRestore;

    public bool CanAct => !IsBusy;
    public bool HasPending => Pending;

    public BackupSettingsViewModel(ILibraryBackupService backup, ILibraryRestoreService restore, IUserDialogs dialogs, IAppRestarter restarter, AppPaths paths)
    {
        _backup = backup;
        _restore = restore;
        _dialogs = dialogs;
        _restarter = restarter;
        _paths = paths;
        RefreshState();
    }

    public void RefreshState()
    {
        Pending = _restore.HasPendingRestore;
        CanUndoRestore = LastSafetyBackup() is not null;
    }

    private string? LastSafetyBackup()
    {
        try
        {
            if (!File.Exists(_paths.RestoreHistoryPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_paths.RestoreHistoryPath));
            var path = doc.RootElement.TryGetProperty("SafetyBackup", out var p) ? p.GetString() : null;
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task BackupNowAsync()
    {
        var suggested = $"MusicFinder-{DateTime.Now:yyyy-MM-dd}{LibraryBackupService.FileExtension}";
        var target = _dialogs.PickSaveFile("Save a backup of the library", "MusicFinder backup|*" + LibraryBackupService.FileExtension, suggested);
        if (target is null) return;

        IsBusy = true;
        StatusText = "Making the backup…";
        try
        {
            var result = await _backup.BackupAsync(new BackupRequest(target, IncludeSecrets, IncludeArtwork), CancellationToken.None);
            StatusText = $"Backup saved and verified: {Path.GetFileName(result.Path)} ({result.Bytes / 1024.0 / 1024.0:0.0} MB, {result.Manifest.Counts.GetValueOrDefault("Tracks"):N0} tracks). " +
                         NotTheMusicText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusText = "The backup failed and nothing was saved: " + ex.Message;
        }
        finally { IsBusy = false; BackupNowCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestoreAsync()
    {
        var file = _dialogs.PickFile("Choose a backup to restore", "MusicFinder backup|*.zip");
        if (file is null) return;
        await OpenRestoreAsync(file, isUndo: false);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task UndoLastRestoreAsync()
    {
        var safety = LastSafetyBackup();
        if (safety is null) { StatusText = "There is no earlier library to go back to."; return; }
        await OpenRestoreAsync(safety, isUndo: true);
    }

    private async Task OpenRestoreAsync(string archive, bool isUndo)
    {
        IsBusy = true;
        StatusText = "Checking the backup…";
        try
        {
            var preview = await _restore.PreviewAsync(archive, CancellationToken.None);
            StatusText = "";
            _dialogs.ShowRestore(new RestoreViewModel(archive, preview, _restore, _dialogs, _restarter, isUndo));
            RefreshState();
        }
        catch (BackupFormatException ex) { StatusText = "This backup cannot be used: " + ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = "The backup could not be read: " + ex.Message; }
        finally { IsBusy = false; RestoreCommand.NotifyCanExecuteChanged(); UndoLastRestoreCommand.NotifyCanExecuteChanged(); BackupNowCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand]
    private void CancelPending()
    {
        _restore.CancelPendingRestore();
        RefreshState();
        StatusText = "The prepared restore was cancelled.";
    }

    [RelayCommand]
    private void RestartNow() => _restarter.Restart();

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_paths.BackupsDir);
        _dialogs.RevealInExplorer(_paths.BackupsDir);
    }
}
