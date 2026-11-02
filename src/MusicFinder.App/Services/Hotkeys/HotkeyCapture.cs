using System.Windows.Input;

namespace MusicFinder.App.Services.Hotkeys;

/// <summary>Turns the keys the user presses in the shortcut box into the text <c>HotkeyGesture.TryParse</c> understands.</summary>
public static class HotkeyCapture
{
    /// <summary>
    /// "Ctrl+Alt+Space" for the pressed key and modifiers; null while only modifiers are held (the user is still building the shortcut).
    /// A key the shortcut grammar does not know is passed through by name so the parser can say exactly what is wrong.
    /// </summary>
    public static string? ToText(Key key, Key systemKey, ModifierKeys modifiers)
    {
        // With Alt held WPF reports Key.System and the real key in SystemKey.
        var actual = key == Key.System ? systemKey : key;
        if (IsModifierKey(actual) || actual == Key.None || actual == Key.DeadCharProcessed) return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(actual));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        _ => key.ToString()
    };
}
