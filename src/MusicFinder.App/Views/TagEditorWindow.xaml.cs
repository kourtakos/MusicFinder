using System.ComponentModel;
using System.Windows;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class TagEditorWindow : Window
{
    public TagEditorWindow(TagEditorViewModel viewModel)
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

    /// <summary>Closing mid-write would leave the user unsure what happened; they must finish or stop it first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is TagEditorViewModel { IsWritingStep: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
