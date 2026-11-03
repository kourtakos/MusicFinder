namespace MusicFinder.Application.Display;

/// <summary>The size of the whole interface as a factor: 1.0 is normal, 1.5 is half as big again. Steps of ten percent, kept between 80 and 200 percent.</summary>
public static class ZoomLevel
{
    public const double Min = 0.8;
    public const double Max = 2.0;
    public const double Default = 1.0;
    public const double Step = 0.1;

    /// <summary>A usable factor from any number, including a damaged value in the settings file.</summary>
    public static double Clamp(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? Default : Math.Round(Math.Clamp(value, Min, Max), 2);

    /// <summary>One step bigger (direction +1) or smaller (-1). A value between steps first snaps to the grid, so 1.05 goes to 1.1, not 1.15.</summary>
    public static double Next(double current, int direction)
    {
        var snapped = Math.Round(Clamp(current) / Step) * Step;
        return Clamp(snapped + Math.Sign(direction) * Step);
    }

    /// <summary>The window content is designed for at least 1000 x 650 (CLAUDE.md), so the zoom may only go as far as a screen of this size leaves that much room.</summary>
    public const double ContentMinWidth = 1000;
    public const double ContentMinHeight = 650;

    /// <summary>The biggest zoom a work area of this size (in device-independent pixels) can show without cutting the layout. Never below 100%.</summary>
    public static double MaxForArea(double width, double height)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return Default;
        var fit = Math.Min(width / ContentMinWidth, height / ContentMinHeight);
        return Math.Max(Default, Math.Min(Max, Math.Floor(fit * 10 + 1e-9) / 10));
    }

    public static int ToPercent(double value) => (int)Math.Round(Clamp(value) * 100);

    public static double FromPercent(int percent) => Clamp(percent / 100.0);
}
