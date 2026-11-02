using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Enums;

namespace MusicFinder.App.Views;

/// <summary>View-only logic: selection bridging, incremental loading on scroll, keyboard shortcuts, focus.</summary>
public partial class LibraryView : UserControl
{
    private static readonly Dictionary<string, SortField> SortFields = new()
    {
        ["Title"] = SortField.Title,
        ["Artist"] = SortField.Artist,
        ["Bpm"] = SortField.Bpm,
        ["Key"] = SortField.Key,
        ["Duration"] = SortField.Duration,
        ["LastPlayed"] = SortField.LastPlayed,
        ["PlayCount"] = SortField.PlayCount
    };

    private LibraryViewModel? _vm;

    public LibraryView()
    {
        InitializeComponent();
        EnsureDragGesture();
        DataContextChanged += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach();
    }

    private void Attach()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        Detach();
        _vm = DataContext as LibraryViewModel;
        if (_vm is null) return;
        _vm.FocusSearchRequested += OnFocusSearchRequested;
        _vm.Search.PropertyChanged += OnSearchPropertyChanged;
        UpdateSortArrows();
        UpdateSimilarColumns();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.FocusSearchRequested -= OnFocusSearchRequested;
        _vm.Search.PropertyChanged -= OnSearchPropertyChanged;
        _vm = null;
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        // The view may have just been created by a tab switch; focus once layout is ready.
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.SortField) or nameof(SearchViewModel.SortDirection))
            UpdateSortArrows();
        if (e.PropertyName == nameof(SearchViewModel.IsSimilarMode)) UpdateSimilarColumns();
    }

    /// <summary>The Match and Why columns exist only while the grid lists similar tracks.</summary>
    private void UpdateSimilarColumns()
    {
        var visibility = _vm?.Search.IsSimilarMode == true ? Visibility.Visible : Visibility.Collapsed;
        MatchColumn.Visibility = visibility;
        WhyColumn.Visibility = visibility;
    }

    private void UpdateSortArrows()
    {
        if (_vm is null) return;
        foreach (var column in TrackGrid.Columns)
        {
            column.SortDirection = SortFields.TryGetValue(column.SortMemberPath ?? "", out var field) && field == _vm.Search.SortField
                ? (_vm.Search.SortDirection == SortDirection.Asc ? ListSortDirection.Ascending : ListSortDirection.Descending)
                : null;
        }
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Search.SetSelectedItems(TrackGrid.SelectedItems.OfType<TrackRowViewModel>().ToList());
    }

    private void TrackGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // sorting happens in the database, not in the grid
        if (_vm is not null && SortFields.TryGetValue(e.Column.SortMemberPath ?? "", out var field))
            _vm.Search.SortByCommand.Execute(field);
    }

    private void TrackGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || e.VerticalChange <= 0) return;
        // Load the next page when within two viewports of the end.
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2 * e.ViewportHeight)
            _ = _vm.Search.LoadMoreAsync();
    }

    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        if (ctrl && e.Key == Key.C) { _vm.Search.CopyPathsCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { _vm.Search.RevealInExplorerCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F2) { _vm.EditTagsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F6) { _vm.RenameFilesCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.I) { _vm.IdentifyCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.B) { _vm.AnalyzeCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete) e.Handled = true; // MusicFinder never deletes music files
    }

    // ===== Dragging tracks out to other applications =====

    private TrackDragGesture? _dragGesture;

    private void EnsureDragGesture()
    {
        _dragGesture ??= new TrackDragGesture(TrackGrid, typeof(DataGridRow), () => TrackGrid.SelectedItems,
            () => _vm?.Search.DragOut(TrackGrid));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && _vm is not null)
        {
            _vm.Search.ClearQueryCommand.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
