using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One export in the Session tab, with a plain-language reason for its status.</summary>
public sealed class SessionEntryItem
{
    public SessionEntry Entry { get; }

    public SessionEntryItem(SessionEntry entry) => Entry = entry;

    public long TrackId => Entry.TrackId;
    public string Title => Entry.Title;
    public string Artist => Entry.Artist ?? "";
    public SessionPlayStatus Status => Entry.Status;
    public string Evidence => Entry.Evidence;

    /// <summary>Stored in UTC, shown in local time.</summary>
    public string TimeText => Entry.ExportedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string StatusText => Entry.Status switch
    {
        SessionPlayStatus.Confirmed => Loc.T("Confirmed"),
        SessionPlayStatus.Possible => Loc.T("Possibly played"),
        _ => Loc.T("Exported")
    };

    /// <summary>"Copy to Traktor". The target is a hint taken from the window under the cursor at the drop.</summary>
    public string TargetText => Entry.Target is { Length: > 0 } t ? $"{Loc.T(Entry.Effect)} → {t}" : Loc.T(Entry.Effect);
}

/// <summary>
/// The live DJ session: what was dragged out, what MusicFinder infers about it and why. Nothing here claims more than
/// the evidence supports: "Exported" is a drop that happened, "Possibly played" is a quiet period, "Confirmed" is the user's
/// own decision (or, from M4, a DJ application's history).
/// </summary>
public sealed partial class SessionViewModel : ObservableObject
{
    public static readonly string[] SoftwareChoices = [Loc.T("Not specified"), "Traktor", "VirtualDJ", Loc.T("Other")];

    private readonly ISessionService _session;
    private readonly SynchronizationContext _ui;
    private int _generation;

    public ObservableCollection<SessionEntryItem> Entries { get; } = [];

    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _title = Loc.T("No live session");
    [ObservableProperty] private string _subtitle = Loc.T("Start a session, or just drag a track to your DJ software and one starts automatically.");
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editSoftware = SoftwareChoices[0];
    [ObservableProperty] private int _exportedCount;
    [ObservableProperty] private int _possibleCount;
    [ObservableProperty] private int _confirmedCount;
    [ObservableProperty] private string? _error;

    public SessionViewModel(ISessionService session)
    {
        _session = session;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        // Raised from any thread (the 20 s timer fires on the thread pool).
        _session.SessionChanged += (_, _) => OnUi(ReloadAsync);
        _session.PlayStateChanged += (_, _) => OnUi(ReloadAsync);
    }

    private void OnUi(Func<Task> work)
    {
        if (SynchronizationContext.Current == _ui) _ = work();
        else _ui.Post(_ => _ = work(), null);
    }

    public bool CanUndo => _session.CanUndo;
    public bool HasEntries => Entries.Count > 0;

    /// <summary>"Traktor · 3 exported · 1 possible · 1 played", or "No session" for the status bar.</summary>
    public string StatusBarText => !IsLive
        ? Loc.T("No session")
        : SoftwareOf(_session.Active?.DjSoftware) is { } s
            ? Loc.T("Session · {0} · {1} exported · {2} possible · {3} played", s, ExportedCount, PossibleCount, ConfirmedCount)
            : Loc.T("Session · {0} exported · {1} possible · {2} played", ExportedCount, PossibleCount, ConfirmedCount);

    public string LiveBadgeText => SoftwareOf(_session.Active?.DjSoftware) is { } s ? Loc.T("LIVE · {0}", s) : Loc.T("LIVE");

    private static string? SoftwareOf(string? software) => string.IsNullOrWhiteSpace(software) ? null : software;

    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var active = _session.Active;
            IReadOnlyList<SessionEntry> entries = active is null ? [] : await Task.Run(() => _session.GetEntriesAsync(active.Id, CancellationToken.None));
            if (generation != _generation) return; // a newer reload is already running

            IsLive = active is not null;
            Title = active?.Name ?? Loc.T("No live session");
            Subtitle = active is null
                ? Loc.T("Start a session, or just drag a track to your DJ software and one starts automatically.")
                : active.AutoStarted
                    ? Loc.T("Started {0:HH:mm} (started automatically by your first drag)", active.StartedUtc.ToLocalTime())
                    : Loc.T("Started {0:HH:mm}", active.StartedUtc.ToLocalTime());
            if (active is not null)
            {
                EditName = active.Name ?? "";
                EditSoftware = SoftwareOf(active.DjSoftware) is { } s && SoftwareChoices.Contains(s) ? s : SoftwareChoices[0];
            }

            Entries.Clear();
            foreach (var entry in entries) Entries.Add(new SessionEntryItem(entry));
            ExportedCount = entries.Count(e => e.Status == SessionPlayStatus.Exported);
            PossibleCount = entries.Count(e => e.Status == SessionPlayStatus.Possible);
            ConfirmedCount = entries.Count(e => e.Status == SessionPlayStatus.Confirmed);
            Error = null;

            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(StatusBarText));
            OnPropertyChanged(nameof(LiveBadgeText));
        }
        catch (Exception ex) { Error = Loc.T("Could not load the session: {0}", ex.Message); }
    }

    private string? ChosenSoftware => EditSoftware == SoftwareChoices[0] ? null : EditSoftware;

    [RelayCommand]
    private async Task Start()
    {
        try { await _session.StartAsync(ChosenSoftware, CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not start the session: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task Stop()
    {
        try { await _session.StopAsync(CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not stop the session: {0}", ex.Message); }
    }

    /// <summary>Saves the name and DJ software of the live session.</summary>
    [RelayCommand]
    private async Task SaveDetails()
    {
        if (!IsLive) return;
        try { await _session.RenameAsync(string.IsNullOrWhiteSpace(EditName) ? null : EditName.Trim(), ChosenSoftware, CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not save: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task MarkPlayed(SessionEntryItem? item)
    {
        if (item is null) return;
        try { await _session.MarkPlayedAsync(item.TrackId, item.Entry.AnchorEventId, CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not mark as played: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task MarkNotPlayed(SessionEntryItem? item)
    {
        if (item is null) return;
        try { await _session.MarkNotPlayedAsync(item.TrackId, item.Entry.AnchorEventId, CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not mark as not played: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task Undo()
    {
        try { await _session.UndoAsync(CancellationToken.None); }
        catch (Exception ex) { Error = Loc.T("Could not undo: {0}", ex.Message); }
        OnPropertyChanged(nameof(CanUndo));
    }
}
