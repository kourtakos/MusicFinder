using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.ViewModels;

public enum IdentifyStep { Choose, Preview, Writing, Done }

/// <summary>One track waiting to be identified, and where its lookup stands.</summary>
public sealed partial class IdentifyQueueItem : ObservableObject
{
    public long TrackId { get; }
    public string Label { get; }
    public IdentificationResult? Result { get; set; }

    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private bool _resolved;

    public IdentifyQueueItem(TrackRowViewModel track)
    {
        TrackId = track.Id;
        Label = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} — {track.Title}";
    }
}

/// <summary>A candidate as the list shows it. The service's own score is shown by name; nothing is combined into a percentage.</summary>
public sealed class CandidateItem
{
    public IdentificationCandidate Candidate { get; }
    public CandidateItem(IdentificationCandidate candidate) => Candidate = candidate;

    public string Title => Candidate.Title;
    public string ArtistCredit => Candidate.ArtistCredit;
    public string ScoreText => Candidate.ScoreText;
    public string DurationText => Candidate.DurationMs is { } ms ? $"{(int)TimeSpan.FromMilliseconds(ms).TotalMinutes}:{TimeSpan.FromMilliseconds(ms).Seconds:00}" : "—";
    public string EvidenceText => string.Join(" · ", Candidate.Evidence);
}

/// <summary>A release the recording appeared on. Album, year and track number depend on this choice.</summary>
public sealed record ReleaseChoice(ReleaseSummary? Release, string Text);

/// <summary>One line of the "what would change" table: the user ticks the fields to apply.</summary>
public sealed partial class ProposalRow : ObservableObject
{
    public TagField Field { get; }
    public string FieldLabel { get; }
    public string Current { get; }
    public string Proposed { get; }
    public string Source { get; }
    [ObservableProperty] private bool _selected;

    public ProposalRow(FieldProposal p)
    {
        Field = p.Field;
        FieldLabel = FieldLabels.For(p.Field);
        Current = string.IsNullOrWhiteSpace(p.Current) ? "(empty)" : p.Current;
        Proposed = p.Proposed ?? "";
        Source = p.Source;
        _selected = p.Selected;
        Model = p;
    }

    public FieldProposal Model { get; }
    public FieldProposal ToProposal() => Model with { Selected = Selected };
}

/// <summary>The user-facing name of a tag field.</summary>
public static class FieldLabels
{
    public static string For(TagField field) => field switch
    {
        TagField.AlbumArtist => "Album artist",
        TagField.TrackNumber => "Track no.",
        TagField.MusicBrainzRecordingId => "MusicBrainz id",
        _ => field.ToString()
    };
}

/// <summary>One line of the preview table: what the file holds now and what it would hold after the write.</summary>
public sealed record PreviewLine(TagField Field, string FieldLabel, string? Before, string? After);

public sealed class MethodOption
{
    public IdentificationMethod Method { get; }
    public string Label { get; }
    public MethodOption(IdentificationMethod method, string label) { Method = method; Label = label; }
}

/// <summary>
/// Looks up the selected tracks online, lets the user choose a recording and release, shows what would change, and writes only
/// after a preview and confirmation, through the same safe tag-writing path as the tag editor.
/// </summary>
public sealed partial class IdentifyViewModel : ObservableObject
{
    private readonly IIdentificationService _service;
    private readonly ITagEditingService _editing;
    private readonly ITrackRepository _tracks;
    private readonly ISettingsStore _settings;
    private readonly string _backupDirectory;
    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _writeCts;
    private int _showVersion;
    private bool _suppress;
    private Task _pending = Task.CompletedTask;

    public ObservableCollection<IdentifyQueueItem> Queue { get; } = [];
    public ObservableCollection<CandidateItem> Candidates { get; } = [];
    public ObservableCollection<ReleaseChoice> Releases { get; } = [];
    public ObservableCollection<ProposalRow> Proposals { get; } = [];
    public ObservableCollection<PreviewLine> PreviewRows { get; } = [];
    public IReadOnlyList<MethodOption> Methods { get; } =
    [
        new(IdentificationMethod.Auto, "Best available"),
        new(IdentificationMethod.AcoustIdFingerprint, "Audio fingerprint"),
        new(IdentificationMethod.MusicBrainzTagSearch, "Search by tags"),
        new(IdentificationMethod.ExistingRecordingId, "Use the file's MusicBrainz id")
    ];

    [ObservableProperty] private IdentifyStep _step = IdentifyStep.Choose;
    [ObservableProperty] private IdentifyQueueItem? _current;
    [ObservableProperty] private CandidateItem? _selectedCandidate;
    [ObservableProperty] private ReleaseChoice? _selectedRelease;
    [ObservableProperty] private MethodOption _method;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isLookingUp;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _keepBackup;
    [ObservableProperty] private string _methodNote = "";

    public string Heading { get; }
    public bool IsChooseStep => Step == IdentifyStep.Choose;
    public bool IsPreviewStep => Step == IdentifyStep.Preview;
    public bool IsWritingStep => Step == IdentifyStep.Writing;
    public bool IsDoneStep => Step == IdentifyStep.Done;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasQueue => Queue.Count > 1;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasNext => Queue.Any(q => !q.Resolved && q != Current);
    public string WriteButtonText => "Write 1 file";
    public Task WhenIdle => _pending;
    public event EventHandler? CloseRequested;

    public IdentifyViewModel(IReadOnlyList<TrackRowViewModel> tracks, IIdentificationService service, ITagEditingService editing,
        ITrackRepository trackRepository, ISettingsStore settings, AppPaths paths)
    {
        _service = service;
        _editing = editing;
        _tracks = trackRepository;
        _settings = settings;
        _backupDirectory = Path.Combine(paths.DataDir, "TagBackups");
        _keepBackup = settings.Load().KeepTagBackups;
        _method = Methods[0];
        foreach (var t in tracks) Queue.Add(new IdentifyQueueItem(t));
        Heading = tracks.Count == 1 ? "Identify online" : $"Identify {tracks.Count} tracks online";
        _current = Queue.FirstOrDefault();
        UpdateMethodNote();
    }

    // ===== lookups =====

    /// <summary>Looks up every track in turn (the services allow about one request a second, so this is sequential on purpose).</summary>
    public async Task StartAsync()
    {
        var readiness = _service.GetReadiness();
        if (!readiness.CanTagSearch)
        {
            Message = readiness.Enabled && readiness.Consented
                ? "MusicBrainz asks every program to say who is calling. Add a contact address in Settings → Online identification."
                : "Online identification is off. Turn it on in Settings → Online identification; you will see exactly what is sent first.";
            foreach (var q in Queue) q.Status = "Not looked up";
            return;
        }
        await LookupAsync(Queue.ToList(), force: false);
    }

    private async Task LookupAsync(IReadOnlyList<IdentifyQueueItem> items, bool force)
    {
        _lookupCts?.Cancel();
        var cts = _lookupCts = new CancellationTokenSource();
        IsLookingUp = true;
        Message = null;
        try
        {
            var done = 0;
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested) break;
                item.Status = "Looking up…";
                ProgressText = items.Count > 1 ? $"{done} of {items.Count} looked up" : "Looking up…";
                var result = await _service.IdentifyAsync(new IdentificationRequest(item.TrackId, Method.Method, force), cts.Token);
                item.Result = result;
                item.Status = StatusFor(result);
                done++;
                if (result.Outcome == IdentificationOutcome.Cancelled) break;
                if (ReferenceEquals(item, Current)) await ShowCurrentAsync();
                else if (Current is null) Current = item;
            }
            ProgressText = items.Count > 1 ? $"{done} of {items.Count} looked up" : "";
            if (cts.IsCancellationRequested)
                foreach (var item in items.Where(i => i.Result is null || i.Status == "Looking up…")) item.Status = "Not looked up";
        }
        finally
        {
            IsLookingUp = false;
            if (ReferenceEquals(_lookupCts, cts)) _lookupCts = null;
            cts.Dispose();
        }
    }

    private static string StatusFor(IdentificationResult r) => r.Outcome switch
    {
        IdentificationOutcome.Found => r.Candidates.Count == 1 ? "1 candidate" : $"{r.Candidates.Count} candidates",
        IdentificationOutcome.NoMatch => "No match",
        IdentificationOutcome.Cancelled => "Cancelled",
        _ => "Problem"
    };

    [RelayCommand]
    private void CancelLookups() => _lookupCts?.Cancel();

    /// <summary>Asks again, ignoring remembered answers, with the method chosen now.</summary>
    [RelayCommand]
    private async Task SearchAgainAsync()
    {
        if (Current is null || IsLookingUp) return;
        await LookupAsync([Current], force: true);
    }

    partial void OnMethodChanged(MethodOption value) => UpdateMethodNote();

    private void UpdateMethodNote()
    {
        var r = _service.GetReadiness();
        MethodNote = Method.Method switch
        {
            IdentificationMethod.AcoustIdFingerprint when !r.HasApiKey => "Needs an AcoustID key (Settings).",
            IdentificationMethod.AcoustIdFingerprint when r.Fingerprinter is null => "Needs fpcalc.exe (Settings shows where to get it).",
            IdentificationMethod.AcoustIdFingerprint => "Sends a fingerprint of the first seconds of audio to AcoustID, then asks MusicBrainz about the recordings it names.",
            IdentificationMethod.MusicBrainzTagSearch => "Sends the title, artist and album text of the tags (or the file name when there is no title) to MusicBrainz.",
            IdentificationMethod.ExistingRecordingId => "Looks up the MusicBrainz id already stored in the file.",
            _ => r.CanFingerprint ? "Uses the audio fingerprint." : "Uses the id in the file if it has one, otherwise a search by tags."
        };
    }

    // ===== showing one track's result =====

    partial void OnCurrentChanged(IdentifyQueueItem? value) => Pend(ShowCurrentAsync());

    private async Task ShowCurrentAsync()
    {
        var version = ++_showVersion;
        _suppress = true;
        Candidates.Clear();
        Releases.Clear();
        Proposals.Clear();
        SelectedCandidate = null;
        SelectedRelease = null;
        _suppress = false;
        OnPropertyChanged(nameof(HasCandidates));

        var result = Current?.Result;
        Message = result is { Outcome: not IdentificationOutcome.Found } && !string.IsNullOrEmpty(result.Message) ? result.Message : null;
        if (result is null || result.Outcome != IdentificationOutcome.Found)
        {
            OnPropertyChanged(nameof(HasNext));
            return;
        }

        foreach (var c in result.Candidates) Candidates.Add(new CandidateItem(c));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasNext));
        if (version != _showVersion) return;
        SelectedCandidate = Candidates[0];   // the best candidate is only pre-selected: nothing is applied until the user confirms
        await Task.CompletedTask;
    }

    partial void OnSelectedCandidateChanged(CandidateItem? value)
    {
        if (_suppress) return;
        Releases.Clear();
        SelectedRelease = null;
        Proposals.Clear();
        if (value is null) return;

        Releases.Add(new ReleaseChoice(null, "No release (title, artist and id only)"));
        foreach (var r in value.Candidate.Releases
                     .OrderBy(r => r.Status == "Official" ? 0 : 1)
                     .ThenBy(r => r.PrimaryType == "Album" ? 0 : 1)
                     .ThenBy(r => r.SecondaryTypes.Count)
                     .ThenBy(r => r.Date ?? "9999", StringComparer.Ordinal))
            Releases.Add(new ReleaseChoice(r, DescribeRelease(r)));
        SelectedRelease = Releases.Count > 1 ? Releases[1] : Releases[0];
    }

    partial void OnSelectedReleaseChanged(ReleaseChoice? value)
    {
        if (_suppress || value is null) return;
        Pend(RebuildProposalsAsync(value));
    }

    private static string DescribeRelease(ReleaseSummary r)
    {
        var parts = new List<string> { r.Title };
        if (!string.IsNullOrEmpty(r.Date)) parts.Add(r.Date);
        if (!string.IsNullOrEmpty(r.Country)) parts.Add(r.Country);
        var type = string.Join(" + ", new[] { r.PrimaryType }.Concat(r.SecondaryTypes).Where(t => !string.IsNullOrEmpty(t)));
        if (type.Length > 0) parts.Add(type);
        if (!string.IsNullOrEmpty(r.Status) && r.Status != "Official") parts.Add(r.Status);
        return string.Join(" · ", parts);
    }

    private async Task RebuildProposalsAsync(ReleaseChoice choice)
    {
        var version = ++_showVersion;
        var item = Current;
        var candidate = SelectedCandidate?.Candidate;
        if (item is null || candidate is null) return;

        ReleaseDetails? details = null;
        if (choice.Release is not null)
        {
            try { details = await _service.GetReleaseDetailsAsync(choice.Release.ReleaseId, default); }
            catch (IdentificationHttpException ex)
            {
                Message = "Could not load that release: " + ex.Message;
            }
            if (details is null && Message is null) Message = "MusicBrainz has no details for that release.";
        }
        var track = await _tracks.GetAsync(item.TrackId);
        if (track is null || version != _showVersion) return;

        var proposals = IdentificationProposalBuilder.Build(track, candidate, details, _settings.Load().SuggestGenresFromMusicBrainz);
        Proposals.Clear();
        foreach (var p in proposals) Proposals.Add(new ProposalRow(p));
    }

    // ===== skipping, previewing, writing =====

    [RelayCommand]
    private void Skip()
    {
        if (Current is null) return;
        Current.Status = "Skipped";
        Current.Resolved = true;
        MoveToNext();
    }

    private void MoveToNext()
    {
        var next = Queue.FirstOrDefault(q => !q.Resolved && !ReferenceEquals(q, Current));
        if (next is not null) Current = next;
        OnPropertyChanged(nameof(HasNext));
    }

    private TagChangeSet BuildChanges() => IdentificationProposalBuilder.ToChangeSet(Proposals.Select(p => p.ToProposal()));

    [RelayCommand]
    private async Task PreviewAsync()
    {
        Message = null;
        if (Current is null) return;
        if (!Proposals.Any(p => p.Selected))
        {
            Message = "Tick at least one field to apply.";
            return;
        }
        var plan = await _editing.PreviewAsync([Current.TrackId], BuildChanges());
        if (plan.Errors.Count > 0)
        {
            Message = string.Join(" ", plan.Errors);
            return;
        }
        if (plan.Changes.Count == 0)
        {
            Message = "The file already has these values, so nothing would change.";
            return;
        }
        PreviewRows.Clear();
        foreach (var row in plan.Changes) PreviewRows.Add(new PreviewLine(row.Field, FieldLabels.For(row.Field), row.Before, row.After));
        PreviewSummary = $"{plan.Changes.Count} change(s) in this file. Nothing is written until you confirm.";
        Step = IdentifyStep.Preview;
    }

    [RelayCommand]
    private void Back()
    {
        Message = null;
        Step = IdentifyStep.Choose;
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (Step != IdentifyStep.Preview || Current is null) return;
        var item = Current;
        Step = IdentifyStep.Writing;
        _writeCts = new CancellationTokenSource();
        try
        {
            var options = new TagWriteOptions(KeepBackup, KeepBackup ? _backupDirectory : null);
            var changes = BuildChanges();
            var summary = await Task.Run(() => _editing.ApplyAsync([item.TrackId], changes, options, null, _writeCts.Token));
            var failed = summary.Files.FirstOrDefault(f => f.Outcome == TagEditOutcome.Failed);
            if (summary.Written > 0)
            {
                item.Status = "Applied";
                item.Resolved = true;
                ResultSummary = "Written. " + (failed is null ? "" : failed.Error);
            }
            else
            {
                ResultSummary = failed is not null ? "Not written: " + failed.Error + " The file was left untouched." : "Nothing needed to change.";
            }
        }
        catch (Exception ex)
        {
            ResultSummary = "The write stopped: " + ex.Message;
        }
        finally
        {
            _writeCts?.Dispose();
            _writeCts = null;
        }
        OnPropertyChanged(nameof(HasNext));
        Step = IdentifyStep.Done;
    }

    /// <summary>After a write: on to the next track that still has to be decided.</summary>
    [RelayCommand]
    private void Next()
    {
        Step = IdentifyStep.Choose;
        MoveToNext();
    }

    partial void OnKeepBackupChanged(bool value)
    {
        var s = _settings.Load();
        s.KeepTagBackups = value;
        _settings.Save(s);
    }

    partial void OnStepChanged(IdentifyStep value)
    {
        OnPropertyChanged(nameof(IsChooseStep));
        OnPropertyChanged(nameof(IsPreviewStep));
        OnPropertyChanged(nameof(IsWritingStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    [RelayCommand]
    private void Close()
    {
        if (Step == IdentifyStep.Writing) return;
        _lookupCts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }


    private void Pend(Task work) => _pending = _pending.ContinueWith(_ => work, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
}

