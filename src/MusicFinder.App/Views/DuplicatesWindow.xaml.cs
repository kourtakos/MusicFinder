using System.ComponentModel;
using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class DuplicatesWindow : Window
{
    public DuplicatesWindow(DuplicatesViewModel viewModel)
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

    /// <summary>Closing the window stops a running scan; what was hashed so far is kept.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is DuplicatesViewModel { IsScanning: true } vm) vm.StopCommand.Execute(null);
        base.OnClosing(e);
    }
}
