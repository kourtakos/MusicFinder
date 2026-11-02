using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

public sealed class DuplicateMemberViewModel
{
    public long TrackId { get; }
    public string FullPath { get; }
    public string FolderText { get; }
    public string FileName { get; }
    public string FormatText { get; }
    public string PlaysText { get; }
    public bool IsSuggested { get; }

    public DuplicateMemberViewModel(DuplicateMember m, bool isSuggested)
    {
        TrackId = m.TrackId;
        FullPath = m.FullPath;
        FileName = Path.GetFileName(m.FullPath);
        FolderText = Path.GetDirectoryName(m.FullPath) ?? "";
        var ext = m.Extension.TrimStart('.').ToUpperInvariant();
        FormatText = m.BitrateKbps is > 0 ? $"{ext} · {m.BitrateKbps} kbps" : ext;
        PlaysText = m.PlayCount switch { 0 => "Never played", 1 => "1 play", _ => $"{m.PlayCount} plays" };
        IsSuggested = isSuggested;
    }
}

public sealed class DuplicateGroupViewModel
{
    public string Hash { get; }
    public string Heading { get; }
    public string DetailText { get; }
    public string KeepText { get; }
    public IReadOnlyList<DuplicateMemberViewModel> Members { get; }

    public DuplicateGroupViewModel(DuplicateGroup g)
    {
        Hash = g.Hash;
        var first = g.Members[0];
        Heading = string.IsNullOrWhiteSpace(first.Title)
            ? Path.GetFileNameWithoutExtension(first.FullPath)
            : string.IsNullOrWhiteSpace(first.Artist) ? first.Title! : $"{first.Artist} - {first.Title}";
        DetailText = $"{g.Members.Count} identical files · {DuplicatesViewModel.FormatSize(g.FileSize)} each · " +
                     $"{DuplicatesViewModel.FormatSize(g.ExtraBytes)} in the extra copies";
        KeepText = "Suggested to keep: the highlighted one. " + string.Join(". ", g.KeepReasons) + ".";
        Members = g.Members.Select(m => new DuplicateMemberViewModel(m, m.TrackId == g.SuggestedKeepTrackId)).ToList();
    }
}

/// <summary>
/// Review of byte-identical files. It only finds and shows them: there is no delete, move or rename here. "Suggested keep" is
/// a hint with reasons. Each file can be revealed in Explorer or listed in the Library, and a group that is not a real
/// duplicate for you can be dismissed (it stays dismissed on later scans).
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly IDuplicateScanService _service;
    private readonly IUserDialogs _dialogs;
    private readonly Action<string> _showInLibrary;
    private CancellationTokenSource? _cts;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanScan))] private bool _isScanning;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _hasGroups;

    public bool CanScan => !IsScanning;

    public event EventHandler? CloseRequested;

    public DuplicatesViewModel(IDuplicateScanService service, IUserDialogs dialogs, Action<string> showInLibrary)
    {
        _service = service;
        _dialogs = dialogs;
        _showInLibrary = showInLibrary;
    }

    /// <summary>Shows what earlier scans already found; opens no file.</summary>
    public async Task LoadAsync()
    {
        await ReloadGroupsAsync();
        StatusText = HasGroups
            ? "These are results of earlier scans. Scan again to include new files."
            : "Press Scan to look for files with identical content. Files are compared by their bytes, not by name or tags.";
    }

    private async Task ReloadGroupsAsync()
    {
        var groups = await _service.GetGroupsAsync(CancellationToken.None);
        Groups.Clear();
        foreach (var g in groups) Groups.Add(new DuplicateGroupViewModel(g));
        HasGroups = Groups.Count > 0;
        var files = groups.Sum(g => g.Members.Count);
        var extra = groups.Sum(g => g.ExtraBytes);
        SummaryText = HasGroups
            ? $"{groups.Count:N0} groups, {files:N0} files, {FormatSize(extra)} taken by the extra copies."
            : "";
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "Looking for files that could be identical…";
        ProgressValue = 0;
        try
        {
            var progress = new Progress<DuplicateScanProgress>(p =>
            {
                ProgressMax = Math.Max(1, p.Total);
                ProgressValue = p.Done;
                StatusText = p.Total == 0
                    ? "Nothing new to check."
                    : $"Checked {p.Done:N0} of {p.Total:N0} files ({FormatSize(p.BytesHashed)} read)" +
                      (p.Unreadable > 0 ? $", {p.Unreadable:N0} could not be read" : "") + ".";
            });
            var summary = await Task.Run(() => _service.ScanAsync(progress, _cts.Token));
            await ReloadGroupsAsync();
            StatusText = (summary.Cancelled ? "Stopped. What was checked is remembered, so the next scan continues from here. " : "Done. ") +
                         $"{summary.Candidates:N0} files share their size with another file; {summary.Hashed:N0} checked now, " +
                         $"{summary.AlreadyHashed:N0} already known" +
                         (summary.Unreadable > 0 ? $", {summary.Unreadable:N0} could not be read (moved, changed or locked)." : ".");
        }
        catch (Exception ex)
        {
            StatusText = "The scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task DismissAsync(DuplicateGroupViewModel? group)
    {
        if (group is null) return;
        await _service.DismissAsync(group.Hash, CancellationToken.None);
        Groups.Remove(group);
        HasGroups = Groups.Count > 0;
        SummaryText = HasGroups ? SummaryText : "";
    }

    [RelayCommand]
    private void Reveal(DuplicateMemberViewModel? member)
    {
        if (member is not null) _dialogs.RevealInExplorer(member.FullPath);
    }

    [RelayCommand]
    private void ShowGroupInLibrary(DuplicateGroupViewModel? group)
    {
        if (group is null) return;
        _showInLibrary("dup:" + group.Hash[..16]);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowAllInLibrary()
    {
        _showInLibrary("dup:exact");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        if (IsScanning) _cts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };
}
