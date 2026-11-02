using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class RestoreWindow : Window
{
    public RestoreWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }
}
