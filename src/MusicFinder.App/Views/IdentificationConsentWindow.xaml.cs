using System.Windows;
using System.Windows.Controls;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class IdentificationConsentWindow : Window
{
    private readonly IdentificationConsentViewModel _vm;

    public IdentificationConsentWindow(IdentificationConsentViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
        KeyBox.Password = viewModel.ApiKey;   // a PasswordBox cannot be bound
        viewModel.CloseRequested += (_, _) => Close();
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.ApiKey = ((PasswordBox)sender).Password;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }
}
