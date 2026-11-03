using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class FirstRunWindow : Window
{
    private readonly FirstRunViewModel _vm;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    /// <summary>The window's own close button counts as "done with the tour", like Skip.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) _vm.MarkCompleted();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _vm.IsRecognition ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (_vm.IsRecognition && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            _ = _vm.ImportAsync(files[0]);
        e.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }
}
