using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MusicFinder.App.Services;
using MusicFinder.App.Services.Hotkeys;
using MusicFinder.App.ViewModels;
using MusicFinder.Application.Hotkeys;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.App.Views;

/// <summary>
/// The always-on-top search pop-up. It is created once and hidden, not closed, so it opens instantly and keeps its query and position.
/// It does not hide when it loses focus: dropping a track into a player activates the player, and the pop-up must still be there for the next one.
/// </summary>
public partial class QuickSearchWindow : Window, IQuickSearchPresenter
{
    private readonly QuickSearchViewModel _vm;
    private readonly ISettingsStore _settings;
    private readonly TrackDragGesture _dragGesture;
    private bool _allowClose;

    public QuickSearchWindow(QuickSearchViewModel viewModel, ISettingsStore settings)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        _settings = settings;
        _dragGesture = new TrackDragGesture(ResultList, typeof(ListBoxItem), () => ResultList.SelectedItems, () => _vm.DragOut(ResultList));
    }

    public bool IsShown => IsVisible;

    /// <summary>Shows the window where the user left it (or near the top of the screen they are working on) and puts the cursor in the search box.</summary>
    public void ShowAndFocus()
    {
        if (!IsVisible)
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var saved = _settings.Load();
            var fallback = Screens.UnderCursor(dpi) ?? new ScreenArea(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var (left, top) = WindowPlacement.Resolve(saved.QuickSearchLeft, saved.QuickSearchTop, Width, Screens.All(dpi), fallback);
            Left = left;
            Top = top;
            Show();
            _ = _vm.RefreshAsync(); // the library may have changed since it was last open
        }

        Activate();
        Topmost = false; // a hotkey press may not be allowed to take the foreground; toggling Topmost reliably lifts the window
        Topmost = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Hides the window and remembers where it was.</summary>
    public void HideAndRemember()
    {
        if (!IsVisible) return;
        try
        {
            var s = _settings.Load();
            s.QuickSearchLeft = Left;
            s.QuickSearchTop = Top;
            _settings.Save(s);
        }
        catch (IOException) { /* losing the position is harmless */ }
        Hide();
    }

    void IQuickSearchPresenter.Show() => ShowAndFocus();
    void IQuickSearchPresenter.Hide() => HideAndRemember();

    /// <summary>Lets the window really close (application exit).</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true; // Alt+F4 or the taskbar must not destroy the pop-up; it just goes away until the next hotkey
            HideAndRemember();
            return;
        }
        base.OnClosing(e);
    }

    private void TitleStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelectedItems(ResultList.SelectedItems.OfType<TrackRowViewModel>().ToList());

    /// <summary>Double-click on a result plays a preview (it does not open the library or start a drag).</summary>
    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ResultList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is TrackRowViewModel row)
        {
            _vm.Preview.PlayRow(row);
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _vm.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.OpenInLibraryCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when ResultList.Items.Count > 0:
                if (ResultList.SelectedIndex < 0) ResultList.SelectedIndex = 0;
                (ResultList.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, ResultList.SelectedIndex)) as UIElement)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void ResultList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Escape) { _vm.CloseCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { _vm.OpenInLibraryCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Up && ResultList.SelectedIndex <= 0) { SearchBox.Focus(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && ResultList.SelectedItems.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, ResultList.SelectedItems.OfType<TrackRowViewModel>().Select(t => t.FullPath)));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete) e.Handled = true; // MusicFinder never deletes music files
    }
}
