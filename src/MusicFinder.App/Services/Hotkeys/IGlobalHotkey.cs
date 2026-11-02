using MusicFinder.Application.Hotkeys;

namespace MusicFinder.App.Services.Hotkeys;

/// <summary>Result of asking Windows for a global shortcut. <see cref="Error"/> is plain language for the user.</summary>
public sealed record HotkeyRegistration(bool Success, string? Error = null)
{
    public static HotkeyRegistration Ok { get; } = new(true);
}

/// <summary>A system-wide keyboard shortcut that works while another program has the focus.</summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>Raised on the UI thread each time the registered shortcut is pressed.</summary>
    event EventHandler? Pressed;

    /// <summary>The shortcut currently registered, or null.</summary>
    HotkeyGesture? Current { get; }

    /// <summary>
    /// Registers <paramref name="gesture"/> and, only if that worked, releases the previous one. A failure (the shortcut is taken
    /// by another program, or Windows refuses it) leaves the previous shortcut working.
    /// </summary>
    HotkeyRegistration Register(HotkeyGesture gesture);

    void Unregister();
}
