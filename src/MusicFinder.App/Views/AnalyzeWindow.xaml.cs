using System.ComponentModel;
using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class AnalyzeWindow : Window
{
    public AnalyzeWindow(AnalyzeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await viewModel.StartAsync();
    }

    /// <summary>Ticking a BPM or key changes how many files Write will touch; tell the view model so the button and the summary follow.</summary>
    private void Tick_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnalyzeViewModel vm) vm.NotifyTicksChanged();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>Closing mid-write would leave the user unsure what happened; they must finish first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is AnalyzeViewModel { IsWritingStep: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
