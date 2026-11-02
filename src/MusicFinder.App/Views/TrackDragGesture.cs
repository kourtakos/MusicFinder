using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicFinder.App.Views;

/// <summary>
/// The mouse gesture that turns a press-and-move on a result row into a drag-out of the selected tracks. Shared by the library grid
/// and the quick-search list so both behave identically, including the multi-selection rule: pressing on a row that is already part
/// of a multi-selection must not collapse the selection (or several tracks could never be dragged); a plain click collapses it on release.
/// </summary>
public sealed class TrackDragGesture
{
    private readonly UIElement _host;
    private readonly Type _rowType;
    private readonly Func<IList> _selectedItems;
    private readonly Action _startDrag;
    private Point? _origin;
    private DependencyObject? _pressedRow;

    /// <param name="host">The DataGrid or ListBox that holds the rows.</param>
    /// <param name="rowType">DataGridRow or ListBoxItem.</param>
    /// <param name="selectedItems">The host's live selection list.</param>
    /// <param name="startDrag">Starts the modal OS drag (called once per gesture).</param>
    public TrackDragGesture(UIElement host, Type rowType, Func<IList> selectedItems, Action startDrag)
    {
        _host = host;
        _rowType = rowType;
        _selectedItems = selectedItems;
        _startDrag = startDrag;
        host.PreviewMouseLeftButtonDown += OnDown;
        host.PreviewMouseLeftButtonUp += OnUp;
        host.PreviewMouseMove += OnMove;
    }

    private DependencyObject? FindRow(DependencyObject? from)
    {
        for (var d = from; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (_rowType.IsInstanceOfType(d)) return d;
        return null;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _origin = null;
        _pressedRow = null;
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row is null) return; // header, scrollbar or empty space: not a drag

        _origin = e.GetPosition(_host);

        var plain = Keyboard.Modifiers == ModifierKeys.None;
        if (plain && Selector.GetIsSelected(row) && _selectedItems().Count > 1)
        {
            _pressedRow = row;
            e.Handled = true;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var row = _pressedRow;
        _origin = null;
        _pressedRow = null;
        if (row is null) return;

        _selectedItems().Clear();
        Selector.SetIsSelected(row, true);
        (row as UIElement)?.Focus();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_origin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(_host);
        if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _origin = null;
        _pressedRow = null;
        if (_selectedItems().Count == 0) return;

        _startDrag(); // modal OS drag loop; returns when the button is released or Esc is pressed
    }
}
