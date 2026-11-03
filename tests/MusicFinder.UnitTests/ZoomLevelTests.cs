using MusicFinder.Application.Display;

namespace MusicFinder.UnitTests;

public class ZoomLevelTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.8)]
    [InlineData(0.0, 0.8)]
    [InlineData(-3, 0.8)]
    [InlineData(5, 2.0)]
    [InlineData(1.234, 1.23)]
    public void AnyNumber_BecomesAUsableFactor(double input, double expected) => Assert.Equal(expected, ZoomLevel.Clamp(input));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ADamagedValue_FallsBackToNormal(double input) => Assert.Equal(1.0, ZoomLevel.Clamp(input));

    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(1.5, 1, 1.6)]
    [InlineData(1.05, 1, 1.1)]       // between steps: snap first, then move one step
    [InlineData(1.05, -1, 0.9)]
    [InlineData(2.0, 1, 2.0)]        // already at the top
    [InlineData(0.8, -1, 0.8)]       // already at the bottom
    [InlineData(1.9, 5, 2.0)]        // only the direction counts, not its size
    public void OneStep_MovesTenPercentAndStaysInRange(double current, int direction, double expected) =>
        Assert.Equal(expected, ZoomLevel.Next(current, direction));

    [Fact]
    public void WalkingUpAndDown_NeverDriftsOffTheGrid()
    {
        var level = ZoomLevel.Default;
        for (var i = 0; i < 40; i++) level = ZoomLevel.Next(level, 1);
        Assert.Equal(2.0, level);
        for (var i = 0; i < 40; i++) level = ZoomLevel.Next(level, -1);
        Assert.Equal(0.8, level);
        for (var i = 0; i < 2; i++) level = ZoomLevel.Next(level, 1);
        Assert.Equal(1.0, level);         // 0.8 -> 0.9 -> 1.0 exactly, with no 0.9999999
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(2.0, 200)]
    [InlineData(9, 200)]
    public void Percent_ConvertsBothWays(double factor, int percent)
    {
        Assert.Equal(percent, ZoomLevel.ToPercent(factor));
        Assert.Equal(ZoomLevel.Clamp(factor), ZoomLevel.FromPercent(percent));
    }
}

public class ZoomLevelScreenTests
{
    [Theory]
    [InlineData(1920, 1040, 1.6)]     // a 1080p screen minus the taskbar: height decides
    [InlineData(2560, 1400, 2.0)]     // roomy: the general limit
    [InlineData(3840, 2100, 2.0)]
    [InlineData(1366, 728, 1.1)]
    [InlineData(1280, 680, 1.0)]      // 1.04 fits, but steps are whole tens
    [InlineData(800, 600, 1.0)]       // never below normal, even when the screen is smaller than the design
    public void TheBiggestZoom_LeavesTheContentItsDesignedMinimum(double width, double height, double expected) =>
        Assert.Equal(expected, ZoomLevel.MaxForArea(width, height));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 1000)]
    [InlineData(double.NaN, 1000)]
    public void ANonsenseArea_GivesNormal(double width, double height) => Assert.Equal(1.0, ZoomLevel.MaxForArea(width, height));
}
