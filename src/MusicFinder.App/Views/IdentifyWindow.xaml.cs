using System.ComponentModel;
using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class IdentifyWindow : Window
{
    public IdentifyWindow(IdentifyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await viewModel.StartAsync();
    }

    /// <summary>Ticking a line of the list changes how many files Write will touch; tell the view model so the button text follows.</summary>
    private void Include_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is IdentifyViewModel vm) vm.NotifyIncludeChanged();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>Closing mid-write would leave the user unsure what happened; they must finish first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is IdentifyViewModel { IsWritingStep: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
