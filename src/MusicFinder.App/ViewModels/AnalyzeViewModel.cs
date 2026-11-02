using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.ViewModels;

public enum AnalyzeStep { Review, Writing, Done }

/// <summary>One track: what the audio analysis estimated, what its tags hold, and what Write would do.</summary>
public sealed partial class AnalyzeItem : ObservableObject
{
    public long TrackId { get; }
    public string Label { get; }
    public Track? Track { get; set; }
    public TrackAnalysis? Analysis { get; set; }

    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private AnalysisFieldProposal? _bpm;
    [ObservableProperty] private AnalysisFieldProposal? _key;
    [ObservableProperty] private bool _writeBpm;
    [ObservableProperty] private bool _writeKey;
    [ObservableProperty] private TempoChoice _tempo = TempoChoice.Estimate;

    public AnalyzeItem(long trackId, string label)
    {
        TrackId = trackId;
        Label = label;
    }

    public bool WillWriteBpm => WriteBpm && Bpm is { CanApply: true };
    public bool WillWriteKey => WriteKey && Key is { CanApply: true };
    public bool WillWrite => WillWriteBpm || WillWriteKey;

    public string BpmNow => Track?.Bpm is > 0 ? Math.Round(Track.Bpm.Value).ToString(CultureInfo.InvariantCulture) : "—";
    public string KeyNow => string.IsNullOrWhiteSpace(Track?.MusicalKey) ? "—" : Track!.MusicalKey!.Trim();
    public string BpmEstimateText => Bpm is null ? (Analysis is null ? "" : "no estimate") : $"≈{Bpm.Proposed} ({LevelText(Bpm.Level)})";
    public string KeyEstimateText => Key is null ? (Analysis is null ? "" : "no estimate") : $"≈{Key.Proposed} ({LevelText(Key.Level)})";
    public string BpmNote => Bpm?.Note ?? "";
    public string KeyNote => Key?.Note ?? "";
    public bool CanWriteBpm => Bpm is { CanApply: true };
    public bool CanWriteKey => Key is { CanApply: true };
    public bool HasBpm => Bpm is not null;
    public bool HasKey => Key is not null;
    public bool TempoChanged => Tempo != TempoChoice.Estimate;

    private static string LevelText(Confidence level) => level switch
    {
        Confidence.High => "high", Confidence.Medium => "medium", Confidence.Low => "low", _ => "none"
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
    partial void OnWriteBpmChanged(bool value) => OnPropertyChanged(nameof(WillWrite));
    partial void OnWriteKeyChanged(bool value) => OnPropertyChanged(nameof(WillWrite));
}

/// <summary>
/// Listens to the selected tracks (in the background, at low priority) and lists what it estimated: tempo and key, each with a confidence.
/// An estimate is a guess from the audio, never a fact: it is shown apart from the tags, only the empty ones are ticked for you and only
/// when the analysis was confident, existing values are kept unless you allow replacing them, and nothing is written until you press Write.
/// </summary>
public sealed partial class AnalyzeViewModel : ObservableObject
{
    private readonly IReadOnlyList<long> _ids;
    private readonly IAudioAnalysisService _service;
    private readonly AnalysisRunner _runner;
    private readonly ITagEditingService _editing;
    private readonly ITrackRepository _tracks;
    private readonly ISettingsStore _settings;
    private readonly string _backupDirectory;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _writeCts;
    private bool _rebuilding;

    public ObservableCollection<AnalyzeItem> Items { get; } = [];
    public ObservableCollection<string> Failures { get; } = [];

    [ObservableProperty] private AnalyzeStep _step = AnalyzeStep.Review;
    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal = 1;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _keepBackup;
    /// <summary>Allow replacing a BPM or key that the file already has. Off unless the user turns it on (or Settings starts it on).</summary>
    [ObservableProperty] private bool _allowOverwrite;

    public string Heading { get; }
    public bool IsReviewStep => Step == AnalyzeStep.Review;
    public bool IsWritingStep => Step == AnalyzeStep.Writing;
    public bool IsDoneStep => Step == AnalyzeStep.Done;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasFailures => Failures.Count > 0;
    public event EventHandler? CloseRequested;

    public IReadOnlyList<AnalyzeItem> Targets => Items.Where(i => i.WillWrite).ToList();
    public int WriteCount => Targets.Count;
    public bool CanWrite => WriteCount > 0 && !IsAnalysing;
    public string WriteButtonText => WriteCount == 1 ? "Write 1 file" : $"Write {WriteCount} files";

    /// <summary>"Will write 12 BPM and 9 keys to 14 files": what the button does, in words.</summary>
    public string WriteSummary
    {
        get
        {
            var targets = Targets;
            if (targets.Count == 0) return "Nothing is ticked to write.";
            var bpm = targets.Count(i => i.WillWriteBpm);
            var key = targets.Count(i => i.WillWriteKey);
            var parts = new List<string>();
            if (bpm > 0) parts.Add(bpm == 1 ? "1 BPM" : $"{bpm} BPM values");
            if (key > 0) parts.Add(key == 1 ? "1 key" : $"{key} keys");
            return $"Will write {string.Join(" and ", parts)} to {(targets.Count == 1 ? "1 file" : targets.Count + " files")}.";
        }
    }

    public AnalyzeViewModel(IReadOnlyList<long> trackIds, IAudioAnalysisService service, AnalysisRunner runner, ITagEditingService editing,
        ITrackRepository tracks, ISettingsStore settings, AppPaths paths)
    {
        _ids = trackIds;
        _service = service;
        _runner = runner;
        _editing = editing;
        _tracks = tracks;
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _backupDirectory = Path.Combine(paths.DataDir, "TagBackups");
        var s = settings.Load();
        _keepBackup = s.KeepTagBackups;
        _allowOverwrite = s.AnalysisAllowOverwriteDefault;
        Heading = trackIds.Count == 1 ? "Analyze BPM and key" : $"Analyze BPM and key of {trackIds.Count} tracks";
    }

    // ===== analysing =====

    /// <summary>Lists the tracks and analyses them in the background; returns when every one has been looked at (or Stop was pressed).</summary>
    public async Task StartAsync(bool force = false)
    {
        if (IsAnalysing) return;
        Message = null;
        if (Items.Count == 0)
        {
            foreach (var id in _ids)
            {
                var track = await _tracks.GetAsync(id);
                var label = track is null ? $"(track {id})" : string.IsNullOrWhiteSpace(track.Artist) ? Display(track) : $"{track.Artist} — {Display(track)}";
                Items.Add(new AnalyzeItem(id, label) { Track = track });
            }
        }
        else
        {
            foreach (var item in Items) { item.Analysis = null; item.Bpm = null; item.Key = null; item.Status = "Waiting"; item.Refresh(); }
        }

        var cts = _analysisCts = new CancellationTokenSource();
        IsAnalysing = true;
        ProgressDone = 0;
        ProgressTotal = Math.Max(1, _ids.Count);
        ProgressText = $"0 of {_ids.Count} analysed";
        foreach (var item in Items) item.Status = "Waiting";
        var progress = new Progress<AnalysisProgress>(OnProgress);
        try
        {
            var concurrency = Math.Clamp(_settings.Load().AnalysisConcurrency, 1, AnalysisRunner.MaxConcurrency);
            await _runner.RunAsync(_ids, force, concurrency, progress, cts.Token);
            await Task.Yield();                                  // let the last progress reports (posted to the UI) arrive
        }
        finally
        {
            IsAnalysing = false;
            foreach (var item in Items.Where(i => i.Status == "Waiting")) item.Status = "Not analysed";
            if (ReferenceEquals(_analysisCts, cts)) _analysisCts = null;
            cts.Dispose();
            Summarise();
        }
    }

    private static string Display(Track t) => string.IsNullOrWhiteSpace(t.Title) ? t.FileName : t.Title!;

    private void OnProgress(AnalysisProgress p)
    {
        var item = Items.FirstOrDefault(i => i.TrackId == p.TrackId);
        if (item is null) return;
        ProgressDone = p.Done;
        ProgressText = $"{p.Done} of {p.Total} analysed";

        var result = p.Result;
        item.Analysis = result.Analysis;
        item.Status = result.Outcome switch
        {
            AnalysisOutcome.Analyzed or AnalysisOutcome.FromCache when result.Analysis is { Bpm: null, Key: null } => "No estimate",
            AnalysisOutcome.Analyzed => "Analysed",
            AnalysisOutcome.FromCache => "Analysed before",
            AnalysisOutcome.TooShort => "Too short",
            AnalysisOutcome.DecodeFailed => "Cannot be read",
            AnalysisOutcome.Cancelled => "Stopped",
            _ => "Problem"
        };
        if (result.Analysis is null && !string.IsNullOrEmpty(result.Message)) item.Status += ": " + result.Message;
        else if (result.Analysis is { Message: { Length: > 0 } msg }) item.Status += ": " + msg;
        BuildProposals(item);
        Summarise();
    }

    [RelayCommand]
    private void Stop() => _analysisCts?.Cancel();

    /// <summary>Forgets the remembered answers for these tracks and analyses them again.</summary>
    [RelayCommand]
    private async Task AnalyzeAgainAsync()
    {
        if (IsAnalysing || Step != AnalyzeStep.Review) return;
        await StartAsync(force: true);
    }

    // ===== proposals =====

    private void BuildProposals(AnalyzeItem item)
    {
        if (item.Analysis is null || item.Track is null)
        {
            item.Bpm = null;
            item.Key = null;
            item.WriteBpm = item.WriteKey = false;
            item.Refresh();
            return;
        }
        var notation = _settings.Load().AnalysisKeyNotation;
        var proposals = AnalysisProposalBuilder.Build(item.Track, item.Analysis, notation, AllowOverwrite, item.Tempo);
        item.Bpm = proposals.Bpm;
        item.Key = proposals.Key;
        item.WriteBpm = proposals.Bpm?.Selected ?? false;
        item.WriteKey = proposals.Key?.Selected ?? false;
        item.Refresh();
    }

    partial void OnAllowOverwriteChanged(bool value)
    {
        foreach (var item in Items) BuildProposals(item);
        Summarise();
    }

    /// <summary>Switches a track's tempo to half (or back): the other octave is a common mistake of tempo estimation, so it is one click away.</summary>
    [RelayCommand]
    private void HalfTempo(AnalyzeItem? item) => ToggleTempo(item, TempoChoice.Half);

    [RelayCommand]
    private void DoubleTempo(AnalyzeItem? item) => ToggleTempo(item, TempoChoice.Double);

    private void ToggleTempo(AnalyzeItem? item, TempoChoice choice)
    {
        if (item is null) return;
        item.Tempo = item.Tempo == choice ? TempoChoice.Estimate : choice;
        BuildProposals(item);
        Summarise();
    }

    /// <summary>Ticks every tempo that can be written (including the less confident ones: the user is deciding for all of them).</summary>
    [RelayCommand]
    private void TickAllTempos()
    {
        foreach (var item in Items) item.WriteBpm = item.Bpm is { CanApply: true };
        Summarise();
    }

    /// <summary>Ticks every key that can be written. Keys are never ticked automatically: this is the user's decision for all of them.</summary>
    [RelayCommand]
    private void TickAllKeys()
    {
        foreach (var item in Items) item.WriteKey = item.Key is { CanApply: true };
        Summarise();
    }

    [RelayCommand]
    private void UntickAll()
    {
        foreach (var item in Items) { item.WriteBpm = false; item.WriteKey = false; }
        Summarise();
    }

    /// <summary>The view calls this when a checkbox of the list changes.</summary>
    public void NotifyTicksChanged() => Summarise();

    private void Summarise()
    {
        OnPropertyChanged(nameof(WriteCount));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteButtonText));
        OnPropertyChanged(nameof(WriteSummary));
    }

    partial void OnIsAnalysingChanged(bool value) => Summarise();

    // ===== writing =====

    /// <summary>Writes the ticked BPM and key values. The list and the line "Will write ..." are the preview; every file goes through the verified temporary-copy writer.</summary>
    [RelayCommand]
    private async Task WriteAsync()
    {
        if (Step != AnalyzeStep.Review || IsAnalysing) return;
        Message = null;
        var targets = Targets;
        if (targets.Count == 0) { Message = "Tick at least one BPM or key to write."; return; }

        Failures.Clear();
        OnPropertyChanged(nameof(HasFailures));
        ProgressDone = 0;
        ProgressTotal = targets.Count;
        Step = AnalyzeStep.Writing;
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
                ProgressText = $"Writing {i + 1} of {targets.Count}: {item.Label}";
                var chosen = new List<AnalysisFieldProposal>();
                if (item.WillWriteBpm) chosen.Add(item.Bpm! with { Selected = true });
                if (item.WillWriteKey) chosen.Add(item.Key! with { Selected = true });
                var changes = AnalysisProposalBuilder.ToChangeSet(chosen);

                TagEditSummary summary;
                try { summary = await Task.Run(() => _editing.ApplyAsync([item.TrackId], changes, options, null, _writeCts.Token)); }
                catch (ArgumentException ex) { Failures.Add($"{item.Label}: {ex.Message}"); continue; }
                catch (OperationCanceledException) { cancelled = true; break; }

                var file = summary.Files.FirstOrDefault();
                if (file?.Outcome == TagEditOutcome.Written)
                {
                    written++;
                    await FollowTheFileAsync(item);
                    item.Status = "Written";
                    if (file.Warning is not null) Failures.Add($"{item.Label}: {file.Warning}");
                }
                else if (file?.Outcome == TagEditOutcome.Unchanged) { unchanged++; item.Status = "Already up to date"; }
                else Failures.Add($"{item.Label}: {file?.Error ?? "not written"} The file was left untouched.");
                ProgressDone = i + 1;
            }
        }
        catch (Exception ex)
        {
            Failures.Add("The write stopped: " + ex.Message);
        }
        finally
        {
            _writeCts?.Dispose();
            _writeCts = null;
        }

        var parts = new List<string> { written == 1 ? "1 file written" : $"{written} files written" };
        if (unchanged > 0) parts.Add($"{unchanged} already up to date");
        if (Failures.Count > 0) parts.Add($"{Failures.Count} problem(s): those files were left untouched");
        if (cancelled) parts.Add("stopped before the remaining files");
        ResultSummary = string.Join(" · ", parts) + ".";
        OnPropertyChanged(nameof(HasFailures));
        Summarise();
        Step = AnalyzeStep.Done;
    }

    /// <summary>After a write the file has a new size and time but the same audio: keep its analysis, and show the new tags.</summary>
    private async Task FollowTheFileAsync(AnalyzeItem item)
    {
        var fresh = await _tracks.GetAsync(item.TrackId);
        if (fresh is null) return;
        await _service.RebindAfterWriteAsync(item.TrackId, fresh.FileSize, fresh.ModifiedUtc, default);
        item.Track = fresh;
        BuildProposals(item);                                  // the tags now hold the estimate: nothing left to propose
    }

    [RelayCommand]
    private void CancelWriting() => _writeCts?.Cancel();

    [RelayCommand]
    private void Continue()
    {
        Step = AnalyzeStep.Review;
        Summarise();
    }

    partial void OnStepChanged(AnalyzeStep value)
    {
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(IsWritingStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    partial void OnKeepBackupChanged(bool value)
    {
        var s = _settings.Load();
        s.KeepTagBackups = value;
        _settings.Save(s);
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    [RelayCommand]
    private void Close()
    {
        if (Step == AnalyzeStep.Writing) return;
        _analysisCts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
