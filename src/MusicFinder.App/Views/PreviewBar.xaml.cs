using System.Windows.Controls;
using System.Windows.Input;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

/// <summary>The discreet player strip at the top of the main window. View-only logic: a click on the thin bar seeks.</summary>
public partial class PreviewBar : UserControl
{
    public PreviewBar() => InitializeComponent();

    private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PreviewViewModel vm || SeekBar.ActualWidth <= 0) return;
        vm.SeekToFraction(e.GetPosition(SeekBar).X / SeekBar.ActualWidth);
        e.Handled = true;
    }
}
