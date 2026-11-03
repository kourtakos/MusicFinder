using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.ViewModels;

/// <summary>One editable field. Untouched fields are never written; typing (or ticking the box) selects a field for change.</summary>
public sealed partial class TagFieldEditor : ObservableObject
{
    private bool _initializing = true;

    public TagField Field { get; }
    public string Label { get; }
    public string Hint { get; }
    /// <summary>Distinct current values across the selected tracks (null entries = empty).</summary>
    public IReadOnlyList<string?> Originals { get; }
    public bool IsMixed => Originals.Count > 1;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _apply;
    [ObservableProperty] private string? _error;

    public TagFieldEditor(TagField field, string label, string hint, IReadOnlyList<string?> originals)
    {
        Field = field;
        Label = label;
        Hint = hint;
        Originals = originals;
        _text = originals.Count == 1 ? originals[0] ?? "" : ""; // a mixed field starts blank rather than showing one arbitrary value
        _initializing = false;
    }

    public string MixedText => IsMixed ? Loc.T("{0} different values", Originals.Count) : "";

    /// <summary>Shown inside an empty box so every state is explicit, including "this will clear the field".</summary>
    public string Placeholder =>
        Apply && string.IsNullOrWhiteSpace(Text) ? Loc.T("(will be cleared)") :
        IsMixed ? Loc.T("(multiple values — type to replace)") :
        Loc.T("(empty)");

    partial void OnTextChanged(string value)
    {
        if (_initializing) return;
        Apply = true; // typing is the user's intent to change this field
        Revalidate();
        OnPropertyChanged(nameof(Placeholder));
    }

    partial void OnApplyChanged(bool value)
    {
        Revalidate();
        OnPropertyChanged(nameof(Placeholder));
    }

    private void Revalidate()
    {
        TagChangeValidator.Canonicalize(Field, Text, out var error);
        Error = Apply ? error : null;
    }
}

public enum TagEditorStep { Edit, Preview, Writing, Done }

public sealed partial class TagEditorViewModel : ObservableObject
{
    private readonly ITagEditingService _service;
    private readonly IUserDialogs _dialogs;
    private readonly ISettingsStore _settings;
    private readonly string _backupDirectory;
    private readonly IReadOnlyList<long> _trackIds;
    private CancellationTokenSource? _writeCts;

    public string Heading { get; }
    public string SubHeading { get; }
    public ObservableCollection<TagFieldEditor> Fields { get; } = [];
    public ObservableCollection<TagDiffRow> PreviewRows { get; } = [];
    public ObservableCollection<TagEditFileResult> Failures { get; } = [];

    [ObservableProperty] private TagEditorStep _step = TagEditorStep.Edit;
    [ObservableProperty] private bool _keepBackup;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _previewSummary = "";
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal = 1;
    [ObservableProperty] private string _progressFile = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private bool _anyWritten;

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public bool IsEditStep => Step == TagEditorStep.Edit;
    public bool IsPreviewStep => Step == TagEditorStep.Preview;
    public bool IsWritingStep => Step == TagEditorStep.Writing;
    public bool IsDoneStep => Step == TagEditorStep.Done;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasFailures => Failures.Count > 0;
    public int TracksToWrite { get; private set; }
    public string WriteButtonText => TracksToWrite == 1 ? Loc.T("Write 1 file") : Loc.T("Write {0} files", TracksToWrite);

    public TagEditorViewModel(IReadOnlyList<TrackRowViewModel> tracks, ITagEditingService service, IUserDialogs dialogs,
        ISettingsStore settings, AppPaths paths)
    {
        _service = service;
        _dialogs = dialogs;
        _settings = settings;
        _backupDirectory = Path.Combine(paths.DataDir, "TagBackups");
        _trackIds = tracks.Select(t => t.Id).ToList();
        _keepBackup = settings.Load().KeepTagBackups;

        Heading = tracks.Count == 1 ? Loc.T("Edit tags") : Loc.T("Edit tags of {0} tracks", tracks.Count);
        SubHeading = tracks.Count == 1
            ? tracks[0].FullPath
            : Loc.T("Only the fields you change are written. Fields where the tracks differ are left alone unless you type a new value.");
    }

    /// <summary>Loads the current values (and detects mixed fields). Call once before showing the dialog.</summary>
    public async Task LoadAsync()
    {
        var values = await _service.GetFieldValuesAsync(_trackIds);
        Fields.Clear();
        foreach (var (field, label, hint) in new[]
                 {
                     (TagField.Title, Loc.T("Title"), ""),
                     (TagField.Artist, Loc.T("Artist"), ""),
                     (TagField.AlbumArtist, Loc.T("Album artist"), ""),
                     (TagField.Album, Loc.T("Album"), ""),
                     (TagField.Genre, Loc.T("Genre"), Loc.T("Separate several genres with ;")),
                     (TagField.Year, Loc.T("Year"), "1900 – 2100"),
                     (TagField.TrackNumber, Loc.T("Track no."), Loc.T("e.g. 3 or 3/12")),
                     (TagField.Bpm, "BPM", "40 – 300"),
                     (TagField.Key, Loc.T("Key"), Loc.T("e.g. 8A or Am"))
                 })
            Fields.Add(new TagFieldEditor(field, label, hint, values[field]));
    }

    partial void OnStepChanged(TagEditorStep value)
    {
        OnPropertyChanged(nameof(IsEditStep));
        OnPropertyChanged(nameof(IsPreviewStep));
        OnPropertyChanged(nameof(IsWritingStep));
        OnPropertyChanged(nameof(IsDoneStep));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnKeepBackupChanged(bool value)
    {
        var s = _settings.Load();
        s.KeepTagBackups = value;
        _settings.Save(s);
    }

    private TagChangeSet BuildChanges() =>
        new(Fields.Where(f => f.Apply).ToDictionary(f => f.Field, f => (string?)f.Text));

    [RelayCommand]
    private async Task PreviewAsync()
    {
        Message = null;
        var selected = Fields.Where(f => f.Apply).ToList();
        if (selected.Count == 0)
        {
            Message = Loc.T("Nothing to change yet. Type a new value into a field first.");
            return;
        }
        if (selected.Any(f => f.Error is not null))
        {
            Message = Loc.T("Fix the highlighted fields first.");
            return;
        }

        var plan = await _service.PreviewAsync(_trackIds, BuildChanges());
        if (plan.Errors.Count > 0)
        {
            Message = string.Join(" ", plan.Errors);
            return;
        }
        if (plan.Changes.Count == 0)
        {
            Message = Loc.T("Every selected file already has these values, so nothing would change.");
            return;
        }

        PreviewRows.Clear();
        foreach (var row in plan.Changes) PreviewRows.Add(row);
        TracksToWrite = plan.TracksWithChanges;
        OnPropertyChanged(nameof(TracksToWrite));
        OnPropertyChanged(nameof(WriteButtonText));
        var changesText = plan.Changes.Count == 1 ? Loc.T("1 change") : Loc.T("{0} changes", plan.Changes.Count);
        var filesText = plan.TracksWithChanges == 1 ? Loc.T("1 file") : Loc.T("{0} files", plan.TracksWithChanges);
        PreviewSummary = plan.TracksUnchanged == 0
            ? Loc.T("{0} in {1}. Nothing is written until you confirm.", changesText, filesText)
            : plan.TracksUnchanged == 1
                ? Loc.T("{0} in {1}; 1 file already matches and will not be touched.", changesText, filesText)
                : Loc.T("{0} in {1}; {2} files already match and will not be touched.", changesText, filesText, plan.TracksUnchanged);
        Step = TagEditorStep.Preview;
    }

    [RelayCommand]
    private void Back()
    {
        Message = null;
        Step = TagEditorStep.Edit;
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (Step != TagEditorStep.Preview) return;
        ProgressDone = 0;
        ProgressTotal = Math.Max(1, _trackIds.Count);
        ProgressFile = "";
        Step = TagEditorStep.Writing;

        _writeCts = new CancellationTokenSource();
        var progress = new Progress<TagEditProgress>(p =>
        {
            ProgressDone = p.Done;
            ProgressTotal = Math.Max(1, p.Total);
            ProgressFile = p.CurrentFile;
        });

        try
        {
            var options = new TagWriteOptions(KeepBackup, KeepBackup ? _backupDirectory : null);
            var summary = await Task.Run(() => _service.ApplyAsync(_trackIds, BuildChanges(), options, progress, _writeCts.Token));
            ShowResult(summary);
        }
        catch (Exception ex)
        {
            Failures.Clear();
            ResultSummary = Loc.T("The edit stopped: {0}", ex.Message);
            AnyWritten = false;
            Step = TagEditorStep.Done;
        }
        finally
        {
            _writeCts?.Dispose();
            _writeCts = null;
        }
    }

    private void ShowResult(TagEditSummary summary)
    {
        Failures.Clear();
        foreach (var f in summary.Files.Where(f => f.Outcome == TagEditOutcome.Failed || f.Warning is not null))
            Failures.Add(f);
        OnPropertyChanged(nameof(HasFailures));

        AnyWritten = summary.Written > 0;
        var parts = new List<string> { Loc.T("{0} file(s) written", summary.Written) };
        if (summary.Unchanged > 0) parts.Add(Loc.T("{0} already matched", summary.Unchanged));
        if (summary.Failed > 0) parts.Add(Loc.T("{0} failed — those files were left untouched", summary.Failed));
        if (summary.Cancelled) parts.Add(Loc.T("cancelled before the remaining files"));
        ResultSummary = string.Join(" · ", parts);
        Step = TagEditorStep.Done;
    }

    [RelayCommand]
    private void CancelWriting() => _writeCts?.Cancel();

    [RelayCommand]
    private void RevealBackups()
    {
        if (Directory.Exists(_backupDirectory)) _dialogs.RevealInExplorer(_backupDirectory);
    }

    [RelayCommand]
    private void Close()
    {
        if (Step == TagEditorStep.Writing) return; // finish or cancel first
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
