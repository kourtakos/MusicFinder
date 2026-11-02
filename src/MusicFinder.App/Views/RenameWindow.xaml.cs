using System.ComponentModel;
using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class RenameWindow : Window
{
    public RenameWindow(RenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>A running batch is stopped between files when the window is closed; every file already renamed stays journaled and can be undone.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is RenameViewModel { IsBusy: true } vm) vm.StopCommand.Execute(null);
        base.OnClosing(e);
    }
}
