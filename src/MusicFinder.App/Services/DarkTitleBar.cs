using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MusicFinder.App.Services;

/// <summary>Asks Windows 10 20H1+/11 for a dark title bar so dialogs match the theme.</summary>
public static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var enable = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref enable, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
