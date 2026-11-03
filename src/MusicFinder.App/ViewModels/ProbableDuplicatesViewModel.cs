using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

public sealed class SongMemberViewModel
{
    public long TrackId { get; }
    public string FullPath { get; }
    public string FileName { get; }
    public string FolderText { get; }
    public string VersionText { get; }
    public string FormatText { get; }
    public string PlaysText { get; }
    public bool IsSuggested { get; }

    public SongMemberViewModel(SongMember m, bool isSuggested)
    {
        TrackId = m.TrackId;
        FullPath = m.FullPath;
        FileName = Path.GetFileName(m.FullPath);
        FolderText = Path.GetDirectoryName(m.FullPath) ?? "";
        VersionText = m.VersionLabel;
        var ext = m.Extension.TrimStart('.').ToUpperInvariant();
        FormatText = m.BitrateKbps is > 0 ? $"{ext} · {m.BitrateKbps} kbps" : ext;
        PlaysText = m.PlayCount switch { 0 => Loc.T("Never played"), 1 => Loc.T("1 play"), _ => Loc.T("{0} plays", m.PlayCount) };
        IsSuggested = isSuggested;
    }
}

public sealed class SongGroupViewModel
{
    public string Key { get; }
    public string Heading { get; }
    public string DetailText { get; }
    public string HintText { get; }
    public string SearchQuery { get; }
    public IReadOnlyList<SongMemberViewModel> Members { get; }

    public SongGroupViewModel(SongGroup g)
    {
        Key = g.Key;
        Heading = g.Heading;
        SearchQuery = g.SearchQuery;
        Members = g.Members.Select(m => new SongMemberViewModel(m, g.Kind == SongGroupKind.SameRecording && m.TrackId == g.SuggestedTrackId)).ToList();
        if (g.Kind == SongGroupKind.SameRecording)
        {
            DetailText = Loc.T("{0} files · probably the same recording", g.Members.Count);
            HintText = Loc.T("Highlighted: the file that looks best to keep ({0}). This is a suggestion from the tags and file facts, not a check of the audio.", g.SuggestionReason);
        }
        else
        {
            DetailText = Loc.T("{0} files · different versions of one song", g.Members.Count);
            HintText = Loc.T("These are not duplicates: they differ in version (remix, edit, live…) or length. Nothing here is a reason to remove a file.");
        }
    }
}

/// <summary>
/// Review of probable duplicates and of songs that exist in several versions. It reads library data only and has no delete, move or
/// rename: the lists are suspicions to review, and "Not a duplicate" hides a group for good.
/// </summary>
public sealed partial class ProbableDuplicatesViewModel : ObservableObject
{
    private readonly IProbableDuplicateService _service;
    private readonly IUserDialogs _dialogs;
    private readonly Action<string> _showInLibrary;

    public ObservableCollection<SongGroupViewModel> SameRecording { get; } = [];
    public ObservableCollection<SongGroupViewModel> OtherVersions { get; } = [];

    [ObservableProperty] private string _summaryText = Loc.T("Looking…");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasSame;
    [ObservableProperty] private bool _hasOther;

    public event EventHandler? CloseRequested;

    public ProbableDuplicatesViewModel(IProbableDuplicateService service, IUserDialogs dialogs, Action<string> showInLibrary)
    {
        _service = service;
        _dialogs = dialogs;
        _showInLibrary = showInLibrary;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var result = await Task.Run(() => _service.FindAsync(CancellationToken.None));
            SameRecording.Clear();
            foreach (var g in result.SameRecording) SameRecording.Add(new SongGroupViewModel(g));
            OtherVersions.Clear();
            foreach (var g in result.OtherVersions) OtherVersions.Add(new SongGroupViewModel(g));
            Refresh();
        }
        finally { IsLoading = false; }
    }

    private void Refresh()
    {
        HasSame = SameRecording.Count > 0;
        HasOther = OtherVersions.Count > 0;
        SummaryText = HasSame || HasOther
            ? Loc.T("{0:N0} groups of probable duplicates, {1:N0} songs in more than one version.", SameRecording.Count, OtherVersions.Count)
            : Loc.T("Nothing found. Probable duplicates need the same artist and title tags, so tracks without them are not compared.");
    }

    [RelayCommand]
    private async Task DismissAsync(SongGroupViewModel? group)
    {
        if (group is null) return;
        await _service.DismissAsync(group.Key, CancellationToken.None);
        SameRecording.Remove(group);
        OtherVersions.Remove(group);
        Refresh();
    }

    [RelayCommand]
    private void Reveal(SongMemberViewModel? member)
    {
        if (member is not null) _dialogs.RevealInExplorer(member.FullPath);
    }

    [RelayCommand]
    private void ShowGroupInLibrary(SongGroupViewModel? group)
    {
        if (group is null) return;
        _showInLibrary(group.SearchQuery);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
