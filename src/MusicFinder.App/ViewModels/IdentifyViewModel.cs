using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.ViewModels;

/// <summary>Overview = the list of many tracks; Choose = one track in detail; then writing and the result.</summary>
public enum IdentifyStep { Overview, Choose, Writing, Done }

/// <summary>Everything decided for one track so far: recording, release, ticked fields and cover. Kept when the user moves between tracks.</summary>
public sealed class ItemPlan
{
    public required CandidateItem Candidate { get; init; }
    public ReleaseSummary? Release { get; init; }
    public required IReadOnlyList<ProposalRow> Proposals { get; init; }
    public bool TrackHasCover { get; init; }
    public CoverImage? Cover { get; init; }
    public ImageSource? CoverPicture { get; init; }
    public string CoverStatus { get; init; } = "";
    public bool UseCover { get; set; }
    public bool ReplaceCover { get; set; }
    public bool Confident { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>The change set this plan would write: the ticked fields and, if ticked, the cover.</summary>
    public TagChangeSet ToChangeSet()
    {
        var text = IdentificationProposalBuilder.ToChangeSet(Proposals.Select(p => p.ToProposal()));
        return UseCover && Cover is not null ? text with { Cover = Cover, ReplaceExistingCover = ReplaceCover } : text;
    }

    /// <summary>"Album artist, Year, Track no., Cover": what pressing Write would change, in words.</summary>
    public string Summary
    {
        get
        {
            var parts = Proposals.Where(p => p.Selected).Select(p => p.FieldLabel).ToList();
            if (UseCover && Cover is not null && (!TrackHasCover || ReplaceCover)) parts.Add(Loc.T("Cover"));
            return string.Join(", ", parts);
        }
    }

    public bool HasChanges => Summary.Length > 0;
}

/// <summary>One track in the list, and where its lookup and plan stand.</summary>
public sealed partial class IdentifyQueueItem : ObservableObject
{
    public long TrackId { get; }
    public string Label { get; }
    public IdentificationResult? Result { get; set; }

    [ObservableProperty] private string _status = Loc.T("Waiting");
    [ObservableProperty] private bool _resolved;
    /// <summary>Whether Write on the list includes this track.</summary>
    [ObservableProperty] private bool _include;
    [ObservableProperty] private string _matchText = "";
    [ObservableProperty] private string _releaseText = "";
    [ObservableProperty] private string _changesText = "";
    /// <summary>Why a track was not ticked for the user ("the title differs from your tag").</summary>
    [ObservableProperty] private string _attention = "";
    [ObservableProperty] private bool _canInclude;

    public ItemPlan? Plan { get; set; }

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

/// <summary>A release the recording appeared on. Album, year, track number and cover depend on this choice.</summary>
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
        Current = string.IsNullOrWhiteSpace(p.Current) ? Loc.T("(empty)") : p.Current;
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
        TagField.AlbumArtist => Loc.T("Album artist"),
        TagField.TrackNumber => Loc.T("Track no."),
        TagField.MusicBrainzRecordingId => Loc.T("MusicBrainz id"),
        _ => Loc.T(field.ToString())
    };
}

public sealed class MethodOption
{
    public IdentificationMethod Method { get; }
    public string Label { get; }
    public MethodOption(IdentificationMethod method, string label) { Method = method; Label = label; }
}

/// <summary>
/// Looks up the selected tracks online and shows what would change, in one screen: the table of current and proposed values
/// (or, for many tracks, one list with a line per track) is the preview, and a single Write button applies exactly what is
/// ticked, through the same safe tag-writing path as the tag editor. For many tracks, clear matches are chosen and ticked
/// automatically; anything doubtful is left unticked with the reason, for the user to decide.
/// </summary>
public sealed partial class IdentifyViewModel : ObservableObject
{
    private readonly IIdentificationService _service;
    private readonly ITagEditingService _editing;
    private readonly ITrackRepository _tracks;
    private readonly ICoverArtClient _coverArt;
    private readonly Dictionary<string, CoverImage?> _covers = new(StringComparer.OrdinalIgnoreCase);
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
    public ObservableCollection<string> Failures { get; } = [];
    public IReadOnlyList<MethodOption> Methods { get; } =
    [
        new(IdentificationMethod.Auto, Loc.T("Best available")),
        new(IdentificationMethod.AcoustIdFingerprint, Loc.T("Audio fingerprint")),
        new(IdentificationMethod.MusicBrainzTagSearch, Loc.T("Search by tags")),
        new(IdentificationMethod.ExistingRecordingId, Loc.T("Use the file's MusicBrainz id"))
    ];

    [ObservableProperty] private IdentifyStep _step;
    [ObservableProperty] private IdentifyQueueItem? _current;
    [ObservableProperty] private CandidateItem? _selectedCandidate;
    [ObservableProperty] private ReleaseChoice? _selectedRelease;
    [ObservableProperty] private MethodOption _method;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isLookingUp;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal = 1;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _keepBackup;
    [ObservableProperty] private string _methodNote = "";

    // The cover of the chosen release (from the Cover Art Archive). Fetched on request, never stored in the database.
    [ObservableProperty] private CoverImage? _cover;
    [ObservableProperty] private ImageSource? _coverPicture;
    [ObservableProperty] private string _coverStatus = "";
    [ObservableProperty] private bool _useCover;
    [ObservableProperty] private bool _replaceCover;
    [ObservableProperty] private bool _trackHasCover;

    public string Heading { get; }
    public bool IsMany => Queue.Count > 1;
    public bool IsOverviewStep => Step == IdentifyStep.Overview;
    public bool IsChooseStep => Step == IdentifyStep.Choose;
    public bool IsWritingStep => Step == IdentifyStep.Writing;
    public bool IsDoneStep => Step == IdentifyStep.Done;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasQueue => Queue.Count > 1;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasCover => Cover is not null;
    public bool HasFailures => Failures.Count > 0;
    /// <summary>Whether the cover section is offered at all: identification is on and covers are allowed in Settings.</summary>
    public bool CoverEnabled => _settings.Load().UseCoverArtArchive && _service.GetReadiness().CanTagSearch;
    public bool HasNext => Queue.Any(q => !q.Resolved && q != Current);
    public Task WhenIdle => _pending;
    public event EventHandler? CloseRequested;

    /// <summary>What Write would change for the track on screen, in words (the table above it shows the values).</summary>
    public string ChangeSummary => Current?.Plan is { HasChanges: true } p ? Loc.T("Will write: ") + p.Summary : Loc.T("Nothing is ticked to write.");

    /// <summary>The tracks Write applies to: the ticked ones of the list, or the one track on screen.</summary>
    private IReadOnlyList<IdentifyQueueItem> Targets() =>
        IsMany
            ? Queue.Where(q => q.Include && q.Plan is { HasChanges: true }).ToList()
            : Current?.Plan is { HasChanges: true } ? [Current] : [];

    public int WriteCount => Targets().Count;
    public string WriteButtonText => !IsMany ? Loc.T("Write changes") : WriteCount == 1 ? Loc.T("Write 1 file") : Loc.T("Write {0} files", WriteCount);
    public bool CanWrite => WriteCount > 0 && !IsLookingUp;
    public string OverviewSummary
    {
        get
        {
            var ready = Queue.Count(q => q.Include);
            var attention = Queue.Count(q => q.Plan is not null && !q.Include && !q.Resolved);
            var none = Queue.Count(q => q.Result is { } r && r.Outcome != IdentificationOutcome.Found);
            var parts = new List<string> { Loc.T("{0} of {1} ready to write", ready, Queue.Count) };
            if (attention > 0) parts.Add(attention == 1 ? Loc.T("1 needs your decision") : Loc.T("{0} need your decision", attention));
            if (none > 0) parts.Add(Loc.T("{0} with no match or a problem", none));
            return string.Join(" · ", parts);
        }
    }

    public IdentifyViewModel(IReadOnlyList<TrackRowViewModel> tracks, IIdentificationService service, ITagEditingService editing,
        ITrackRepository trackRepository, ICoverArtClient coverArt, ISettingsStore settings, AppPaths paths)
    {
        _service = service;
        _editing = editing;
        _tracks = trackRepository;
        _coverArt = coverArt;
        _settings = settings;
        _backupDirectory = Path.Combine(paths.DataDir, "TagBackups");
        _keepBackup = settings.Load().KeepTagBackups;
        _method = Methods[0];
        foreach (var t in tracks) Queue.Add(new IdentifyQueueItem(t));
        Heading = tracks.Count == 1 ? Loc.T("Identify online") : Loc.T("Identify {0} tracks online", tracks.Count);
        _step = tracks.Count > 1 ? IdentifyStep.Overview : IdentifyStep.Choose;
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
                ? Loc.T("MusicBrainz asks every program to say who is calling. Add a contact address in Settings → Online identification.")
                : Loc.T("Online identification is off. Turn it on in Settings → Online identification; you will see exactly what is sent first.");
            foreach (var q in Queue) q.Status = Loc.T("Not looked up");
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
                item.Status = Loc.T("Looking up…");
                ProgressText = items.Count > 1 ? Loc.T("{0} of {1} looked up", done, items.Count) : Loc.T("Looking up…");
                var result = await _service.IdentifyAsync(new IdentificationRequest(item.TrackId, Method.Method, force), cts.Token);
                item.Result = result;
                item.Status = StatusFor(result);
                done++;
                if (result.Outcome == IdentificationOutcome.Cancelled) break;

                if (IsMany)
                {
                    // Many tracks: choose and tick what is clear, so the list is ready to read. The detail view is filled only when asked for.
                    item.Status = item.Status + Loc.T(" · planning…");
                    await PlanItemAsync(item, cts.Token);
                    item.Status = StatusFor(result);
                    if (ReferenceEquals(item, Current) && Step == IdentifyStep.Choose) await ShowCurrentAsync();
                }
                else if (ReferenceEquals(item, Current)) await ShowCurrentAsync();
                else if (Current is null) Current = item;
            }
            ProgressText = items.Count > 1 ? Loc.T("{0} of {1} looked up", done, items.Count) : "";
            if (cts.IsCancellationRequested)
                foreach (var item in items.Where(i => i.Result is null || i.Status == Loc.T("Looking up…"))) item.Status = Loc.T("Not looked up");
        }
        finally
        {
            IsLookingUp = false;
            if (ReferenceEquals(_lookupCts, cts)) _lookupCts = null;
            cts.Dispose();
            RefreshSummaries();
        }
    }

    private static string StatusFor(IdentificationResult r) => r.Outcome switch
    {
        IdentificationOutcome.Found => r.Candidates.Count == 1 ? Loc.T("1 candidate") : Loc.T("{0} candidates", r.Candidates.Count),
        IdentificationOutcome.NoMatch => Loc.T("No match"),
        IdentificationOutcome.Cancelled => Loc.T("Cancelled"),
        _ => Loc.T("Problem")
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
            IdentificationMethod.AcoustIdFingerprint when !r.HasApiKey => Loc.T("Needs an AcoustID key (Settings)."),
            IdentificationMethod.AcoustIdFingerprint when r.Fingerprinter is null => Loc.T("Needs fpcalc.exe (Settings shows where to get it)."),
            IdentificationMethod.AcoustIdFingerprint => Loc.T("Sends a fingerprint of the first seconds of audio to AcoustID, then asks MusicBrainz about the recordings it names."),
            IdentificationMethod.MusicBrainzTagSearch => Loc.T("Sends the title, artist and album text of the tags (or the file name when there is no title) to MusicBrainz."),
            IdentificationMethod.ExistingRecordingId => Loc.T("Looks up the MusicBrainz id already stored in the file."),
            _ => r.CanFingerprint ? Loc.T("Uses the audio fingerprint.") : Loc.T("Uses the id in the file if it has one, otherwise a search by tags.")
        };
    }

    // ===== planning (many tracks): choose recording, release and cover without touching the detail view =====

    private async Task PlanItemAsync(IdentifyQueueItem item, CancellationToken ct)
    {
        item.Plan = null;
        item.Include = false;
        item.CanInclude = false;
        item.MatchText = "";
        item.ReleaseText = "";
        item.ChangesText = "";
        item.Attention = "";

        var result = item.Result;
        if (result is null || result.Outcome != IdentificationOutcome.Found || result.Candidates.Count == 0)
        {
            item.Attention = result?.Message ?? Loc.T("no result");
            return;
        }
        var track = await _tracks.GetAsync(item.TrackId, ct);
        if (track is null) { item.Attention = Loc.T("the track is no longer in the library"); return; }

        var decision = AutoChoice.Decide(track, result.Candidates);
        var candidate = await _service.EnrichAsync(decision.Candidate ?? result.Candidates[0], ct);   // genres, for hits that came from a search
        var release = AutoChoice.PickRelease(track, candidate);
        var details = release is null ? null : await LoadDetailsAsync(release.ReleaseId, ct);
        var proposals = IdentificationProposalBuilder.Build(track, candidate, details, _settings.Load().SuggestGenresFromMusicBrainz, _settings.Load().MusicBrainzGenreMode);
        var (cover, picture, status) = await LoadCoverForAsync(release, ct);

        var hasCover = !string.IsNullOrEmpty(track.ArtworkCacheKey);
        var plan = new ItemPlan
        {
            Candidate = new CandidateItem(candidate),
            Release = release,
            Proposals = proposals.Select(p => new ProposalRow(p)).ToList(),
            TrackHasCover = hasCover,
            Cover = cover,
            CoverPicture = picture,
            CoverStatus = status,
            UseCover = cover is not null && !hasCover && _settings.Load().EmbedCoverByDefault,
            Confident = decision.Confident,
            Reason = decision.Reason
        };
        AttachPlan(item, plan);
        item.Include = plan.Confident && plan.HasChanges;
        item.Attention = plan.Confident ? "" : Loc.T("Needs your decision: ") + plan.Reason;
    }

    /// <summary>Stores a plan on its item and keeps the list line (match, release, changes) in step with it.</summary>
    private void AttachPlan(IdentifyQueueItem item, ItemPlan plan)
    {
        item.Plan = plan;
        // A tick the user makes on a track is a decision to write it: the track joins the list of files to write.
        foreach (var row in plan.Proposals) row.PropertyChanged += (_, _) => { RefreshItem(item); if (plan.HasChanges) item.Include = true; };
        RefreshItem(item);
    }

    private void RefreshItem(IdentifyQueueItem item)
    {
        if (item.Plan is not { } plan) return;
        item.MatchText = $"{plan.Candidate.Title} — {plan.Candidate.ArtistCredit} ({plan.Candidate.ScoreText})";
        item.ReleaseText = plan.Release?.Title ?? Loc.T("(no release)");
        item.ChangesText = plan.HasChanges ? plan.Summary : Loc.T("nothing to change");
        item.CanInclude = plan.HasChanges;
        if (!plan.HasChanges) item.Include = false;
        RefreshSummaries();
    }

    /// <summary>The view calls this when a line of the list is ticked or unticked.</summary>
    public void NotifyIncludeChanged() => RefreshSummaries();

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(ChangeSummary));
        OnPropertyChanged(nameof(WriteCount));
        OnPropertyChanged(nameof(WriteButtonText));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(OverviewSummary));
    }

    private async Task<ReleaseDetails?> LoadDetailsAsync(string releaseId, CancellationToken ct)
    {
        try { return await _service.GetReleaseDetailsAsync(releaseId, ct); }
        catch (IdentificationHttpException) { return null; }
    }

    /// <summary>The release's front cover, cached for the window's lifetime. Returns what to show: the image, its picture and a status line.</summary>
    private async Task<(CoverImage? Cover, ImageSource? Picture, string Status)> LoadCoverForAsync(ReleaseSummary? release, CancellationToken ct)
    {
        if (release is null || !CoverEnabled) return (null, null, "");
        if (!_covers.TryGetValue(release.ReleaseId, out var cover))
        {
            try { cover = await _coverArt.GetFrontAsync(release.ReleaseId, ct); }
            catch (IdentificationHttpException) { return (null, null, Loc.T("Could not reach the Cover Art Archive.")); }
            _covers[release.ReleaseId] = cover;
        }
        if (cover is null) return (null, null, Loc.T("No cover is on file for this release."));
        var picture = Decode(cover.Data);
        return picture is null ? (null, null, Loc.T("The cover could not be displayed, so it is not offered.")) : (cover, picture, cover.Description);
    }

    // ===== showing one track's result =====

    partial void OnCurrentChanged(IdentifyQueueItem? value)
    {
        if (Step is IdentifyStep.Choose) Pend(ShowCurrentAsync());
        RefreshSummaries();
    }

    private async Task ShowCurrentAsync()
    {
        var version = ++_showVersion;
        _suppress = true;
        Candidates.Clear();
        Releases.Clear();
        Proposals.Clear();
        SelectedCandidate = null;
        SelectedRelease = null;
        SetCover(null, "");
        UseCover = false;
        ReplaceCover = false;
        TrackHasCover = false;
        _suppress = false;
        OnPropertyChanged(nameof(HasCandidates));

        var item = Current;
        var result = item?.Result;
        Message = result is { Outcome: not IdentificationOutcome.Found } && !string.IsNullOrEmpty(result.Message) ? result.Message : null;
        if (item is null || result is null || result.Outcome != IdentificationOutcome.Found)
        {
            OnPropertyChanged(nameof(HasNext));
            RefreshSummaries();
            return;
        }

        foreach (var c in result.Candidates) Candidates.Add(new CandidateItem(c));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(HasNext));
        if (version != _showVersion) return;

        if (item.Plan is { } plan)
        {
            RestorePlan(plan);
            return;
        }
        SelectedCandidate = Candidates[0];   // the best candidate is only pre-selected: nothing is applied until the user presses Write
        await Task.CompletedTask;
    }

    /// <summary>Shows a stored plan in the detail view exactly as it was left (same candidate, release, ticks and cover choice).</summary>
    private void RestorePlan(ItemPlan plan)
    {
        _suppress = true;
        var candidate = Candidates.FirstOrDefault(c => c.Candidate.RecordingId == plan.Candidate.Candidate.RecordingId) ?? Candidates[0];
        SelectedCandidate = candidate;
        FillReleases(candidate);
        SelectedRelease = Releases.FirstOrDefault(r => r.Release?.ReleaseId == plan.Release?.ReleaseId) ?? Releases[0];
        Proposals.Clear();
        foreach (var p in plan.Proposals) Proposals.Add(p);
        TrackHasCover = plan.TrackHasCover;
        SetCover(plan.Cover, plan.CoverStatus, plan.CoverPicture);
        UseCover = plan.UseCover;
        ReplaceCover = plan.ReplaceCover;
        _suppress = false;
        OnPropertyChanged(nameof(CoverEnabled));
        RefreshSummaries();
    }

    private void FillReleases(CandidateItem candidate)
    {
        Releases.Clear();
        Releases.Add(new ReleaseChoice(null, Loc.T("No release (title, artist and id only)")));
        foreach (var r in AutoChoice.OrderReleases(candidate.Candidate.Releases))
            Releases.Add(new ReleaseChoice(r, DescribeRelease(r)));
    }

    partial void OnSelectedCandidateChanged(CandidateItem? value)
    {
        if (_suppress) return;
        Releases.Clear();
        SelectedRelease = null;
        Proposals.Clear();
        if (value is null) return;
        FillReleases(value);
        Pend(ChooseDefaultReleaseAsync(value));
    }

    /// <summary>The release whose title is the album already in the file, otherwise the usual best one.</summary>
    private async Task ChooseDefaultReleaseAsync(CandidateItem candidate)
    {
        var track = Current is { } item ? await _tracks.GetAsync(item.TrackId) : null;
        var release = track is null ? AutoChoice.OrderReleases(candidate.Candidate.Releases).FirstOrDefault() : AutoChoice.PickRelease(track, candidate.Candidate);
        if (!ReferenceEquals(SelectedCandidate, candidate)) return;
        SelectedRelease = Releases.FirstOrDefault(r => r.Release?.ReleaseId == release?.ReleaseId) ?? Releases[0];
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

    /// <summary>The user chose a recording or a release: build the proposals and the cover for it, and make that the track's plan.</summary>
    private async Task RebuildProposalsAsync(ReleaseChoice choice)
    {
        var version = ++_showVersion;
        var item = Current;
        var candidate = SelectedCandidate;
        if (item is null || candidate is null) return;

        ReleaseDetails? details = null;
        if (choice.Release is not null)
        {
            try { details = await _service.GetReleaseDetailsAsync(choice.Release.ReleaseId, default); }
            catch (IdentificationHttpException ex)
            {
                Message = Loc.T("Could not load that release: ") + ex.Message;
            }
            if (details is null && Message is null) Message = Loc.T("MusicBrainz has no details for that release.");
        }
        var track = await _tracks.GetAsync(item.TrackId);
        if (track is null || version != _showVersion) return;

        var enriched = await _service.EnrichAsync(candidate.Candidate, default);
        if (version != _showVersion) return;
        var proposals = IdentificationProposalBuilder.Build(track, enriched, details, _settings.Load().SuggestGenresFromMusicBrainz, _settings.Load().MusicBrainzGenreMode);
        var rows = proposals.Select(p => new ProposalRow(p)).ToList();
        var hasCover = !string.IsNullOrEmpty(track.ArtworkCacheKey);

        _suppress = true;
        Proposals.Clear();
        foreach (var row in rows) Proposals.Add(row);
        TrackHasCover = hasCover;
        SetCover(null, choice.Release is null ? "" : Loc.T("Looking for the cover…"));
        UseCover = false;
        ReplaceCover = false;
        _suppress = false;
        OnPropertyChanged(nameof(CoverEnabled));

        var (cover, picture, status) = await LoadCoverForAsync(choice.Release, default);
        if (version != _showVersion) return;
        var decision = AutoChoice.Decide(track, item.Result?.Candidates ?? []);

        var plan = new ItemPlan
        {
            Candidate = ReferenceEquals(enriched, candidate.Candidate) ? candidate : new CandidateItem(enriched),
            Release = choice.Release,
            Proposals = rows,
            TrackHasCover = hasCover,
            Cover = cover,
            CoverPicture = picture,
            CoverStatus = status,
            UseCover = cover is not null && !hasCover && _settings.Load().EmbedCoverByDefault,
            Confident = decision.Confident && decision.Candidate?.RecordingId == candidate.Candidate.RecordingId,
            Reason = decision.Reason
        };

        _suppress = true;
        SetCover(plan.Cover, plan.CoverStatus, plan.CoverPicture);
        UseCover = plan.UseCover;
        _suppress = false;
        AttachPlan(item, plan);
        // The user looked at this track and chose: it is theirs to include, whatever the automatic rule thought.
        if (plan.HasChanges) item.Include = true;
    }

    private void SetCover(CoverImage? cover, string status, ImageSource? picture = null)
    {
        Cover = cover;
        CoverPicture = picture;
        CoverStatus = status;
        OnPropertyChanged(nameof(HasCover));
    }

    private static ImageSource? Decode(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 168;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or FileFormatException)
        {
            return null;
        }
    }

    partial void OnUseCoverChanged(bool value)
    {
        if (_suppress) return;
        if (Current?.Plan is { } plan) { plan.UseCover = value; RefreshItem(Current); if (plan.HasChanges) Current.Include = true; }
        RefreshSummaries();
    }

    partial void OnReplaceCoverChanged(bool value)
    {
        if (_suppress) return;
        if (value && !UseCover) UseCover = true;          // replacing implies embedding
        if (Current?.Plan is { } plan) { plan.ReplaceCover = value; RefreshItem(Current); if (plan.HasChanges) Current.Include = true; }
        RefreshSummaries();
    }

    // ===== the list =====

    [RelayCommand]
    private void IncludeAllReady()
    {
        foreach (var q in Queue.Where(q => q.Plan is { Confident: true, HasChanges: true } && !q.Resolved)) q.Include = true;
        RefreshSummaries();
    }

    [RelayCommand]
    private void IncludeNone()
    {
        foreach (var q in Queue) q.Include = false;
        RefreshSummaries();
    }

    partial void OnStepChanged(IdentifyStep value)
    {
        OnPropertyChanged(nameof(IsOverviewStep));
        OnPropertyChanged(nameof(IsChooseStep));
        OnPropertyChanged(nameof(IsWritingStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    /// <summary>Opens one track of the list in detail, to change the recording, release or ticks.</summary>
    [RelayCommand]
    private void Review(IdentifyQueueItem? item)
    {
        item ??= Current;
        if (item is null) return;
        Message = null;
        Step = IdentifyStep.Choose;
        if (ReferenceEquals(Current, item)) Pend(ShowCurrentAsync());
        else Current = item;          // OnCurrentChanged shows it
    }

    /// <summary>Back from the detail of one track to the list of all.</summary>
    [RelayCommand]
    private void BackToList()
    {
        Message = null;
        Step = IdentifyStep.Overview;
        RefreshSummaries();
    }

    [RelayCommand]
    private void Skip()
    {
        if (Current is null) return;
        Current.Status = Loc.T("Skipped");
        Current.Resolved = true;
        Current.Include = false;
        MoveToNext();
        RefreshSummaries();
    }

    private void MoveToNext()
    {
        var next = Queue.FirstOrDefault(q => !q.Resolved && !ReferenceEquals(q, Current));
        if (next is not null) Current = next;
        OnPropertyChanged(nameof(HasNext));
    }

    // ===== writing =====

    /// <summary>
    /// Writes what is ticked: for one track the table on screen, for many the ticked lines of the list. The table or list is the
    /// preview; nothing is written before this is pressed, and every file goes through the verified temporary-copy writer.
    /// </summary>
    [RelayCommand]
    private async Task WriteAsync()
    {
        if (Step is IdentifyStep.Writing or IdentifyStep.Done || IsLookingUp) return;
        Message = null;
        var targets = Targets();
        if (targets.Count == 0)
        {
            Message = IsMany ? Loc.T("Tick at least one track to write.")
                : Current?.Plan is { UseCover: true, TrackHasCover: true, ReplaceCover: false }
                    ? Loc.T("The file already has a cover, and the cover is all that is ticked. Tick \"Replace the cover the file has\" to swap it.")
                    : Loc.T("Tick at least one field to apply.");
            return;
        }

        if (!IsMany)
        {
            // One track: check now, so a problem or "nothing would change" is explained here instead of after a write.
            var plan = await _editing.PreviewAsync([targets[0].TrackId], targets[0].Plan!.ToChangeSet());
            if (plan.Errors.Count > 0) { Message = string.Join(" ", plan.Errors); return; }
            if (plan.Changes.Count == 0)
            {
                var p = targets[0].Plan!;
                Message = p.UseCover && p.TrackHasCover && !p.ReplaceCover
                    ? Loc.T("The file already has a cover and these values, so nothing would change. Tick \"Replace the cover the file has\" to swap the cover.")
                    : Loc.T("The file already has these values, so nothing would change.");
                return;
            }
        }

        Failures.Clear();
        OnPropertyChanged(nameof(HasFailures));
        ProgressDone = 0;
        ProgressTotal = Math.Max(1, targets.Count);
        ProgressText = "";
        Step = IdentifyStep.Writing;
        _writeCts = new CancellationTokenSource();
        var written = 0;
        var unchanged = 0;
        var cancelled = false;
        try
        {
            var options = new TagWriteOptions(KeepBackup, KeepBackup ? _backupDirectory : null);
            for (var i = 0; i < targets.Count; i++)
            {
                if (_writeCts.IsCancellationRequested) { cancelled = true; break; }
                var item = targets[i];
                ProgressText = Loc.T("Writing {0} of {1}: {2}", i + 1, targets.Count, item.Label);
                var changes = item.Plan!.ToChangeSet();
                TagEditSummary summary;
                try { summary = await Task.Run(() => _editing.ApplyAsync([item.TrackId], changes, options, null, _writeCts.Token)); }
                catch (ArgumentException ex) { Failures.Add($"{item.Label}: {ex.Message}"); continue; }
                catch (OperationCanceledException) { cancelled = true; break; }

                var file = summary.Files.FirstOrDefault();
                if (file?.Outcome == TagEditOutcome.Written)
                {
                    written++;
                    item.Status = Loc.T("Written");
                    item.Resolved = true;
                    item.Include = false;
                    item.Plan = null;            // the file changed: a new look at it starts from what it holds now
                    item.ChangesText = "";
                    item.CanInclude = false;
                    if (file.Warning is not null) Failures.Add($"{item.Label}: {file.Warning}");
                }
                else if (file?.Outcome == TagEditOutcome.Unchanged) { unchanged++; item.Status = Loc.T("Already up to date"); item.Resolved = true; item.Include = false; }
                else Failures.Add($"{item.Label}: {file?.Error ?? Loc.T("not written")} {Loc.T("The file was left untouched.")}");
                ProgressDone = i + 1;
            }
        }
        catch (Exception ex)
        {
            Failures.Add(Loc.T("The write stopped: ") + ex.Message);
        }
        finally
        {
            _writeCts?.Dispose();
            _writeCts = null;
        }

        var parts = new List<string> { written == 1 ? Loc.T("1 file written") : Loc.T("{0} files written", written) };
        if (unchanged > 0) parts.Add(Loc.T("{0} already up to date", unchanged));
        if (Failures.Count > 0) parts.Add(Failures.Count == 1 ? Loc.T("1 problem: that file was left untouched") : Loc.T("{0} problems: those files were left untouched", Failures.Count));
        if (cancelled) parts.Add(Loc.T("stopped before the remaining files"));
        ResultSummary = string.Join(" · ", parts) + ".";
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasNext));
        RefreshSummaries();
        Step = IdentifyStep.Done;
    }

    [RelayCommand]
    private void CancelWriting() => _writeCts?.Cancel();

    /// <summary>After a write: back to the list (many tracks) or to the track on screen (one).</summary>
    [RelayCommand]
    private void Continue()
    {
        Step = IsMany ? IdentifyStep.Overview : IdentifyStep.Choose;
        if (!IsMany) Pend(ShowCurrentAsync());
        RefreshSummaries();
    }

    partial void OnKeepBackupChanged(bool value)
    {
        var s = _settings.Load();
        s.KeepTagBackups = value;
        _settings.Save(s);
    }

    partial void OnIsLookingUpChanged(bool value) => RefreshSummaries();

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
