namespace MusicFinder.Application.Hotkeys;

/// <summary>Modifier bits, the same values Windows' RegisterHotKey uses (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// A global keyboard shortcut such as <c>Ctrl+Alt+Space</c>. Pure: parsing, validation, canonical text and the virtual-key code.
/// A global hotkey is taken from every other program, so a modifier other than Shift is required (a bare letter or Shift+letter would
/// break typing everywhere), and a few combinations that Windows itself owns are refused up front with a clear reason.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public const string DefaultText = "Ctrl+Alt+Space";
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "Space");

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spacebar"] = "Space", ["return"] = "Enter", ["del"] = "Delete", ["ins"] = "Insert",
        ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["pgdown"] = "PageDown", ["prior"] = "PageUp", ["next"] = "PageDown"
    };

    /// <summary>The Windows virtual-key code of <see cref="Key"/>.</summary>
    public int VirtualKey => KeyCode(Key) ?? throw new InvalidOperationException($"Unknown key '{Key}'.");

    /// <summary>Canonical text, e.g. "Ctrl+Alt+Space": modifiers in a fixed order so equal shortcuts compare equal.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>True when <paramref name="name"/> can be the non-modifier key of a shortcut (letters, digits, F1-F24, a few named keys).</summary>
    public static bool IsSupportedKey(string name) => KeyCode(name) is not null;

    private static int? KeyCode(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c; // the virtual-key codes of letters and digits are their ASCII codes
            return null;
        }
        if (name.Length is 2 or 3 && name[0] is 'F' or 'f' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return 0x70 + n - 1;
        return NamedKeys.TryGetValue(name, out var code) ? code : null;
    }

    /// <summary>Parses "Ctrl+Alt+Space" (case-insensitive, any modifier order, common aliases). On failure <paramref name="error"/> says why in plain language.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = Default;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "Press the keys you want to use."; return false; }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) { error = "The shortcut is incomplete."; return false; }

            var modifier = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" or "super" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None
            };

            if (modifier != HotkeyModifiers.None)
            {
                if (modifiers.HasFlag(modifier)) { error = $"{raw} appears twice."; return false; }
                modifiers |= modifier;
                continue;
            }

            if (key is not null) { error = "Use only one non-modifier key."; return false; }
            if (raw.Equals("esc", StringComparison.OrdinalIgnoreCase) || raw.Equals("escape", StringComparison.OrdinalIgnoreCase))
            {
                error = "Esc closes the quick search, so it cannot open it.";
                return false;
            }
            key = Aliases.TryGetValue(raw, out var alias) ? alias : raw;
            if (!IsSupportedKey(key))
            {
                error = $"'{raw}' is not a supported key. Use a letter, a digit, F1–F24, Space, Tab, Enter, or an arrow/navigation key.";
                return false;
            }
        }

        if (key is null) { error = "Add a key after the modifiers (for example Ctrl+Alt+Space)."; return false; }
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            error = "Use Ctrl, Alt or Win in the shortcut. A shortcut with only Shift (or no modifier) would break typing in every program.";
            return false;
        }

        var canonicalKey = NamedKeys.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key.ToUpperInvariant();
        var candidate = new HotkeyGesture(modifiers, canonicalKey);
        var reserved = ReservedReason(candidate);
        if (reserved is not null) { error = reserved; return false; }

        gesture = candidate;
        return true;
    }

    private static string? ReservedReason(HotkeyGesture g)
    {
        var m = g.Modifiers;
        if (m == (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt) && g.Key == "Delete") return "Ctrl+Alt+Delete belongs to Windows.";
        if (m == HotkeyModifiers.Alt && g.Key == "Tab") return "Alt+Tab belongs to Windows.";
        if (m == HotkeyModifiers.Alt && g.Key == "F4") return "Alt+F4 closes windows.";
        if (m == HotkeyModifiers.Ctrl && g.Key is "C" or "V" or "X" or "Z") return $"Ctrl+{g.Key} is used for editing in every program.";
        return null;
    }
}
