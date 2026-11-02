namespace MusicFinder.Application.Hotkeys;

/// <summary>A screen's usable area in device-independent units.</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Decides where the quick-search window opens: where the user left it if that is still on a screen, otherwise a sensible default.</summary>
public static class WindowPlacement
{
    /// <summary>How much of the window's title strip must be on a screen so it can still be grabbed and dragged back.</summary>
    private const double GrabWidth = 120, GrabHeight = 36;

    /// <summary>Near the top of the screen, horizontally centred: close to where a DJ looks, away from most players' controls.</summary>
    public static (double Left, double Top) Default(ScreenArea screen, double windowWidth) =>
        (screen.Left + Math.Max(0, (screen.Width - windowWidth) / 2), screen.Top + screen.Height * 0.14);

    public static (double Left, double Top) Resolve(double? savedLeft, double? savedTop, double windowWidth,
        IReadOnlyList<ScreenArea> screens, ScreenArea fallback)
    {
        if (savedLeft is { } left && savedTop is { } top && !double.IsNaN(left) && !double.IsNaN(top))
        {
            // A monitor may have been unplugged since the position was saved: keep it only if its title strip is on a current screen.
            foreach (var s in screens)
            {
                var visibleW = Math.Min(left + windowWidth, s.Right) - Math.Max(left, s.Left);
                var visibleH = Math.Min(top + GrabHeight, s.Bottom) - Math.Max(top, s.Top);
                if (visibleW >= GrabWidth && visibleH >= GrabHeight / 2) return (left, top);
            }
        }
        return Default(fallback, windowWidth);
    }
}
