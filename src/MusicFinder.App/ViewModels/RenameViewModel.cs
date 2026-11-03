using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Localization;
using MusicFinder.App.Services;
using MusicFinder.Application.Organize;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One line of the preview: what a file is called now, what it would be called, and why not when it is skipped.</summary>
public sealed class RenamePreviewRowViewModel
{
    public string OldName { get; }
    public string NewName { get; }
    public string FolderText { get; }
    public string StatusText { get; }
    public string? Reason { get; }
    public bool WillRename { get; }
    public bool IsProblem { get; }

    public RenamePreviewRowViewModel(PlanItem item, string? relativeTo = null)
    {
        OldName = relativeTo is null ? Path.GetFileName(item.OldPath) : Path.GetRelativePath(relativeTo, item.OldPath);
        FolderText = Path.GetDirectoryName(item.OldPath) ?? "";
        WillRename = item.WillMove;
        IsProblem = item.Status == PlanStatus.Skipped;
        NewName = item.Status == PlanStatus.Skipped ? "" : relativeTo is null ? Path.GetFileName(item.NewPath) : Path.GetRelativePath(relativeTo, item.NewPath);
        var extras = item.Sidecars.Count > 0 ? " + " + string.Join(", ", item.Sidecars.Select(s => Path.GetExtension(s.NewPath))) : "";
        StatusText = item.Status switch
        {
            PlanStatus.Rename => Loc.T("Rename") + extras,
            PlanStatus.CaseOnly => Loc.T("Change case") + extras,
            PlanStatus.Unchanged => Loc.T("Already named so"),
            _ => Loc.T("Skipped"),
        };
        Reason = item.Reason;
    }
}

public sealed class RenameBatchViewModel
{
    public BatchInfo Info { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool CanUndo => Info.CanUndo;

    public RenameBatchViewModel(BatchInfo info)
    {
        Info = info;
        Title = info.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + "  ·  " + (info.Template ?? "");
        var parts = new List<string> { Loc.T("{0} renamed", info.Done) };
        if (info.Undone > 0) parts.Add(Loc.T("{0} undone", info.Undone));
        if (info.Failed > 0) parts.Add(Loc.T("{0} failed", info.Failed));
        if (info.Skipped > 0) parts.Add(Loc.T("{0} not done", info.Skipped));
        Detail = string.Join(", ", parts);
    }
}

public sealed class RenameViewModelFactory(
    IRenameSourceReader sources, IFileProbe probe, IFileOperationService operations, IUserDialogs dialogs, ISettingsStore settings)
{
    public RenameViewModel Create(IReadOnlyList<long> trackIds) => new(trackIds, sources, probe, operations, dialogs, settings);
}

/// <summary>
/// Batch rename in place. The preview table is the confirmation: it shows every file, what happens to it and why a file is
/// skipped, and the single button says how many files it will rename. Files are renamed, never overwritten; every rename is journaled
/// so a whole batch can be undone.
/// </summary>
public sealed partial class RenameViewModel : ObservableObject
{
    public const string WarningTitle = "Before you rename files";
    public const string DjSoftwareWarning =
        "Traktor and VirtualDJ remember your files by their path. If you rename or move a file that is in their library, they will no longer find it: " +
        "cue points, beat grids and playlist entries for it are lost on their side until you relocate the file there.\n\n" +
        "MusicFinder can save a list of old and new paths for every rename, and it can undo a whole rename. It cannot change anything inside those programs.\n\n" +
        "Go ahead?";

    private readonly IReadOnlyList<long> _trackIds;
    private readonly IRenameSourceReader _sources;
    private readonly IFileProbe _probe;
    private readonly IFileOperationService _operations;
    private readonly IUserDialogs _dialogs;
    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _runCts;
    private IReadOnlyList<PlanItem> _plan = [];

    public static IReadOnlyList<string> Presets { get; } =
        ["{Artist} - {Title}", "{Track:00} {Artist} - {Title}", "{Artist} - {Title} [{Key} {Bpm}]", "{Artist} - {Album} - {Track:00} - {Title}"];

    public static IReadOnlyList<string> OrganizePresets { get; } =
        ["{Genre}/{Artist}/{Artist} - {Title}", "{Genre}/{Artist}/{Album}/{Track:00} {Title}", "{Artist}/{Artist} - {Title}", "{Artist}/{Album}/{Artist} - {Title}"];

    public IReadOnlyList<string> ActivePresets => MoveIntoFolders ? OrganizePresets : Presets;

    public ObservableCollection<RenamePreviewRowViewModel> Rows { get; } = [];
    public ObservableCollection<RenameBatchViewModel> Batches { get; } = [];

    [ObservableProperty] private string _template;
    [ObservableProperty] private bool _usePlaceholders;
    [ObservableProperty] private bool _includeSidecars = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActivePresets), nameof(RenameButtonText), nameof(HeadingText))] private bool _moveIntoFolders;
    [ObservableProperty] private string _templateError = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRename))] private int _renameCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRename))] private bool _isBusy;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private RenameBatchViewModel? _selectedBatch;
    [ObservableProperty] private bool _hasTracks;

    public bool CanRename => RenameCount > 0 && !IsBusy;
    public string HeadingText => MoveIntoFolders ? Loc.T("Move files into folders") : Loc.T("Rename files");
    public string RenameButtonText => MoveIntoFolders
        ? RenameCount switch { 0 => Loc.T("Move"), 1 => Loc.T("Move 1 file"), _ => Loc.T("Move {0:N0} files", RenameCount) }
        : RenameCount switch { 0 => Loc.T("Rename"), 1 => Loc.T("Rename 1 file"), _ => Loc.T("Rename {0:N0} files", RenameCount) };
    public string ReminderText => Loc.T(
        "Traktor and VirtualDJ find files by their path. A file you rename or move that is in their library, playlists or crates is reported missing there, " +
        "and its cue points, beat grids and playlist entries are lost until you relocate it inside those programs. MusicFinder cannot change their databases. " +
        "Save the old and new paths as a list after the rename, and use Undo here to put everything back.");

    public event EventHandler? CloseRequested;
    /// <summary>Raised after files were renamed or restored, so the Library can reload what it shows.</summary>
    public event EventHandler? FilesChanged;

    public RenameViewModel(IReadOnlyList<long> trackIds, IRenameSourceReader sources, IFileProbe probe,
        IFileOperationService operations, IUserDialogs dialogs, ISettingsStore settings)
    {
        _trackIds = trackIds;
        _sources = sources;
        _probe = probe;
        _operations = operations;
        _dialogs = dialogs;
        _settings = settings;
        HasTracks = trackIds.Count > 0;
        var saved = settings.Load();
        _template = string.IsNullOrWhiteSpace(saved.RenameTemplate) ? Presets[0] : saved.RenameTemplate!;
        _usePlaceholders = saved.RenameUsePlaceholders;
        SummaryText = HasTracks ? Loc.T("Working out the new names…") : Loc.T("Select tracks in the Library and choose Rename files to preview a rename.");
    }

    public async Task LoadAsync()
    {
        await ReloadBatchesAsync();
        await RefreshPreviewAsync();
    }

    partial void OnTemplateChanged(string value) => _ = RefreshPreviewAsync();
    partial void OnUsePlaceholdersChanged(bool value) => _ = RefreshPreviewAsync();
    partial void OnIncludeSidecarsChanged(bool value) => _ = RefreshPreviewAsync();

    partial void OnMoveIntoFoldersChanged(bool value)
    {
        var s = _settings.Load();
        var next = value ? s.OrganizeTemplate : s.RenameTemplate;
        Template = string.IsNullOrWhiteSpace(next) || next.Contains('/') != value ? ActivePresets[0] : next!;
        _ = RefreshPreviewAsync();
    }
    partial void OnRenameCountChanged(int value) => OnPropertyChanged(nameof(RenameButtonText));

    [RelayCommand]
    private void UsePreset(string? preset)
    {
        if (!string.IsNullOrWhiteSpace(preset)) Template = preset;
    }

    /// <summary>Recomputes the preview. Reads only the library data and asks the disk whether names are taken; nothing is changed.</summary>
    public async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        if (!HasTracks) return;

        var parsed = FileNameTemplate.Parse(Template);
        TemplateError = parsed.Error ?? "";
        var options = new RenameOptions(Template, UsePlaceholders, IncludeSidecars, MoveIntoFolders);
        try
        {
            var roots = new Dictionary<long, string?>();
            var plan = await Task.Run(async () =>
            {
                var sources = await _sources.GetAsync(_trackIds, cts.Token);
                foreach (var s in sources) roots[s.TrackId] = s.RootPath;
                return FileOperationPlanner.PlanRename(sources, options, _probe);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            _plan = plan;
            Rows.Clear();
            foreach (var item in plan.OrderBy(p => p.Status == PlanStatus.Skipped ? 0 : p.WillMove ? 1 : 2)) Rows.Add(new RenamePreviewRowViewModel(item, MoveIntoFolders && roots.TryGetValue(item.TrackId, out var root) ? root : null));
            RenameCount = plan.Count(p => p.WillMove);
            var skipped = plan.Count(p => p.Status == PlanStatus.Skipped);
            var same = plan.Count(p => p.Status == PlanStatus.Unchanged);
            var willText = MoveIntoFolders
                ? (RenameCount == 1 ? Loc.T("Will move 1 file") : Loc.T("Will move {0:N0} files", RenameCount))
                : (RenameCount == 1 ? Loc.T("Will rename 1 file") : Loc.T("Will rename {0:N0} files", RenameCount));
            SummaryText = parsed.Error is not null
                ? Loc.T("Fix the template to see the new names.")
                : willText
                  + (same > 0 ? ", " + Loc.T("{0:N0} already have this name", same) : "")
                  + (skipped > 0 ? ", " + Loc.T("{0:N0} skipped (see the reasons below)", skipped) : "") + ".";
        }
        catch (OperationCanceledException) { /* a newer preview replaced this one */ }
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        var pending = _plan.Where(p => p.WillMove).ToList();
        if (pending.Count == 0) return;

        var settings = _settings.Load();
        if (!settings.RenameWarningAccepted)
        {
            if (!_dialogs.Confirm(Loc.T(WarningTitle), Loc.T(DjSoftwareWarning))) return;
            settings.RenameWarningAccepted = true;
            _settings.Save(settings);
        }
        settings = _settings.Load();
        if (MoveIntoFolders) settings.OrganizeTemplate = Template; else settings.RenameTemplate = Template;
        settings.RenameUsePlaceholders = UsePlaceholders;
        _settings.Save(settings);

        IsBusy = true;
        RenameCommand.NotifyCanExecuteChanged();
        _runCts = new CancellationTokenSource();
        ProgressMax = pending.Count;
        ProgressValue = 0;
        StatusText = Loc.T("Renaming…");
        try
        {
            var progress = new Progress<FileOperationProgress>(p => { ProgressValue = p.Done; });
            var result = await Task.Run(() => _operations.RenameAsync(pending, Template, progress, _runCts.Token, MoveIntoFolders ? "Organize" : "Rename"));
            StatusText = Describe(result, MoveIntoFolders);
            FilesChanged?.Invoke(this, EventArgs.Empty);
            await ReloadBatchesAsync();
            if (result.Failures.Count > 0)
                _dialogs.ShowInfo(Loc.T("Some files were not renamed"), string.Join("\n", result.Failures.Take(12).Select(f => Path.GetFileName(f.Path) + ": " + f.Reason))
                    + (result.Failures.Count > 12 ? "\n" + Loc.T("…and {0} more.", result.Failures.Count - 12) : ""));
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
            RenameCommand.NotifyCanExecuteChanged();
        }
        await RefreshPreviewAsync();
    }

    private static string Describe(BatchResult r, bool moved)
    {
        if (r.Refused is not null) return r.Refused;
        var text = moved
            ? (r.Renamed == 1 ? Loc.T("Moved 1 file") : Loc.T("Moved {0:N0} files", r.Renamed))
            : (r.Renamed == 1 ? Loc.T("Renamed 1 file") : Loc.T("Renamed {0:N0} files", r.Renamed));
        if (r.SidecarsRenamed > 0) text += " " + (r.SidecarsRenamed == 1 ? Loc.T("and 1 lyrics/cue file") : Loc.T("and {0:N0} lyrics/cue files", r.SidecarsRenamed));
        if (r.Failures.Count > 0) text += "; " + Loc.T("{0:N0} could not be renamed", r.Failures.Count);
        if (r.Cancelled) text += "; " + Loc.T("stopped before the rest");
        return text + ".";
    }

    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private async Task UndoAsync(RenameBatchViewModel? batch)
    {
        if (batch is null || !batch.CanUndo || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _operations.UndoAsync(batch.Info.Id, null, CancellationToken.None));
            StatusText = (result.Restored == 1 ? Loc.T("Restored 1 file") : Loc.T("Restored {0:N0} files", result.Restored))
                         + (result.Refused.Count > 0 ? "; " + Loc.T("{0:N0} could not be restored", result.Refused.Count) : "") + ".";
            if (result.Refused.Count > 0)
                _dialogs.ShowInfo(Loc.T("Some files were not restored"), string.Join("\n", result.Refused.Take(12).Select(f => Path.GetFileName(f.Path) + ": " + f.Reason)));
            FilesChanged?.Invoke(this, EventArgs.Empty);
            await ReloadBatchesAsync();
        }
        finally { IsBusy = false; }
        await RefreshPreviewAsync();
    }

    [RelayCommand]
    private async Task SavePathMapAsync(RenameBatchViewModel? batch)
    {
        if (batch is null) return;
        var map = await _operations.GetPathMapAsync(batch.Info.Id, CancellationToken.None);
        if (map.Count == 0) { _dialogs.ShowInfo(Loc.T("Path list"), Loc.T("This rename has nothing left to list (everything was undone or failed).")); return; }
        var target = _dialogs.PickSaveFile(Loc.T("Save old and new paths"), Loc.T("CSV file") + "|*.csv", "renamed-files.csv");
        if (target is null) return;
        try
        {
            PathMapCsv.Write(target, map);
            StatusText = Loc.T("Saved {0:N0} old and new paths to {1}.", map.Count, Path.GetFileName(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(Loc.T("Path list"), Loc.T("The list could not be saved: {0}", ex.Message));
        }
    }

    private async Task ReloadBatchesAsync()
    {
        var batches = await _operations.GetBatchesAsync(15, CancellationToken.None);
        Batches.Clear();
        foreach (var b in batches) Batches.Add(new RenameBatchViewModel(b));
    }

    [RelayCommand]
    private void Close()
    {
        _previewCts?.Cancel();
        _runCts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
