using System.Runtime.InteropServices;
using MusicFinder.Application.Hotkeys;

namespace MusicFinder.App.Services;

/// <summary>Monitor geometry in device-independent units, for placing the quick-search window on the screen the user is working on.</summary>
public static class Screens
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>All monitors' work areas (taskbar excluded).</summary>
    public static IReadOnlyList<ScreenArea> All(double dpiScale)
    {
        var list = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            if (WorkAreaOf(monitor) is { } area) list.Add(Scale(area, dpiScale));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The work area of the monitor the mouse cursor is on, or null if it cannot be determined.</summary>
    public static ScreenArea? UnderCursor(double dpiScale)
    {
        if (!GetCursorPos(out var p)) return null;
        var monitor = MonitorFromPoint(p, MonitorDefaultToNearest);
        return WorkAreaOf(monitor) is { } area ? Scale(area, dpiScale) : null;
    }

    private static ScreenArea Scale(Rect r, double scale) =>
        new(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);

    private static Rect? WorkAreaOf(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.Work : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
}
