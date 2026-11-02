using System.Windows.Controls;
using System.Windows.Input;

namespace MusicFinder.App.Views;

public partial class TagsView : UserControl
{
    public TagsView() => InitializeComponent();

    /// <summary>A rename box commits on Enter as well as on losing focus (the binding is LostFocus-based).</summary>
    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
