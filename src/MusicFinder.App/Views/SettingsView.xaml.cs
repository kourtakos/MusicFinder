using System.Windows.Controls;
using System.Windows.Input;
using MusicFinder.App.Services.Hotkeys;
using MusicFinder.App.ViewModels;

namespace MusicFinder.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>The shortcut box records the keys that are pressed instead of letting the user type text.</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var actual = e.Key == Key.System ? e.SystemKey : e.Key;

        // Plain Tab / Esc must still move focus away; otherwise the box would trap the keyboard.
        if (modifiers == ModifierKeys.None && actual is Key.Tab or Key.Escape) return;

        e.Handled = true;
        if (DataContext is not SettingsViewModel vm) return;

        var text = HotkeyCapture.ToText(e.Key, e.SystemKey, modifiers);
        if (text is not null) vm.TryChangeHotkey(text);
    }
}
