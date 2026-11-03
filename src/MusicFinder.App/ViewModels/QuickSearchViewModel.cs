using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFinder.App.Services.Dragging;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>
/// The pop-up search that opens over any program. It searches the whole library (folder scope is ignored), shows a short list and lets
/// the user drag tracks straight into their DJ software, so the main window never has to come to the front.
/// Typing here counts as search activity for the play heuristic, exactly like typing in the library.
/// </summary>
public sealed partial class QuickSearchViewModel : ObservableObject
{
    public const int PageSize = 40;
    private const int DebounceMs = 120;

    private readonly ISearchService _search;
    private readonly ISessionService _session;
    private readonly TrackDragService _drag;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;
    private int _generation;

    public Services.Artwork.IArtworkService Artwork { get; }
    public ObservableCollection<TrackRowViewModel> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _statusText = "Type to search your library";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private bool _hasMore;

    /// <summary>Raised when the window should hide (Esc, the close button).</summary>
    public event EventHandler? CloseRequested;
    /// <summary>Raised with the current query when the user wants the full library view.</summary>
    public event EventHandler<string>? OpenInLibraryRequested;

    /// <summary>The same discreet player as the main window; a double-click on a result previews it.</summary>
    public PreviewViewModel Preview { get; }

    public QuickSearchViewModel(ISearchService search, ISessionService session, TrackDragService drag, Services.Artwork.IArtworkService artwork, PreviewViewModel preview)
    {
        Preview = preview;
        _search = search;
        _session = session;
        _drag = drag;
        Artwork = artwork;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        // Plays change in the background (the 20 s timer): keep the badges on the visible rows current.
        _session.PlayStateChanged += (_, ids) => OnUi(() => RefreshRowsAsync(ids));
        _session.SessionChanged += (_, _) => OnUi(() => RefreshRowsAsync(null));
    }

    private void OnUi(Func<Task> work)
    {
        if (SynchronizationContext.Current == _ui) _ = work();
        else _ui.Post(_ => _ = work(), null);
    }

    public IReadOnlyList<TrackRowViewModel> SelectedItems { get; private set; } = [];

    public void SetSelectedItems(IReadOnlyList<TrackRowViewModel> items)
    {
        if (items.Count > 0 && !items.Select(i => i.Id).ToHashSet().SetEquals(SelectedItems.Select(i => i.Id)))
            _session.NotifyActivity(UserActivity.Selection);
        SelectedItems = items;
    }

    partial void OnQueryChanged(string value)
    {
        _session.NotifyActivity(UserActivity.Search);
        _ = RunSearchAsync(DebounceMs);
    }

    /// <summary>Re-runs the current query now (used when the window is shown again, since the library may have changed).</summary>
    public Task RefreshAsync() => RunSearchAsync(0);

    private async Task RunSearchAsync(int delayMs)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var generation = ++_generation;

        try
        {
            if (string.IsNullOrWhiteSpace(Query))
            {
                Results.Clear();
                HasMore = false;
                Warning = null;
                StatusText = "Type to search your library";
                return;
            }

            if (delayMs > 0) await Task.Delay(delayMs, ct);

            var query = new SearchQuery
            {
                Text = Query,
                GlobalScope = true,
                PageSize = PageSize,
                SessionId = _session.Active?.Id,
                Sort = SortField.Relevance
            };
            var page = await Task.Run(() => _search.SearchAsync(query, ct), ct);
            if (ct.IsCancellationRequested || generation != _generation) return;

            Results.Clear();
            foreach (var item in page.Items) Results.Add(new TrackRowViewModel(item));
            HasMore = page.HasMore;
            Warning = page.Warnings.Count > 0 ? string.Join(" ", page.Warnings) : null;
            StatusText = page.Items.Count == 0
                ? "No matches"
                : page.HasMore ? $"Showing the first {page.Items.Count}. Press Enter to see all of them in the library."
                               : $"{page.Items.Count} {(page.Items.Count == 1 ? "match" : "matches")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Warning = "Search failed: " + ex.Message;
        }
    }

    private async Task RefreshRowsAsync(IReadOnlyList<long>? ids)
    {
        try
        {
            if (Results.Count == 0) return;
            var byId = Results.ToDictionary(r => r.Id);
            var wanted = (ids is null ? byId.Keys : ids.Where(byId.ContainsKey)).ToList();
            if (wanted.Count == 0) return;

            var sessionId = _session.Active?.Id;
            var fresh = await Task.Run(() => _search.GetRowsAsync(wanted, sessionId, CancellationToken.None));
            foreach (var row in fresh)
                if (byId.TryGetValue(row.Id, out var vm)) vm.Update(row);
        }
        catch (Exception ex) { Warning = "Could not refresh: " + ex.Message; }
    }

    /// <summary>Drags the selected tracks to another application as real files (same rules as the library grid).</summary>
    public DragOutcome? DragOut(object source)
    {
        var selection = SelectedItems.Select(i => new DragCandidate(i.Id, i.FullPath, i.IsMissing)).ToList();
        var result = _drag.Drag(selection, source);
        if (result.Note is not null) Warning = result.Note;
        _ = ReportAsync(result.Recording);
        return result.Outcome;
    }

    private async Task ReportAsync(Task recording)
    {
        try { await recording; }
        catch (Exception ex) { Warning = "The drag could not be recorded: " + ex.Message; }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenInLibrary() => OpenInLibraryRequested?.Invoke(this, Query);
}
