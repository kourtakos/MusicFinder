using MusicFinder.Application.Identification;

namespace MusicFinder.UnitTests;

public class FpcalcVersionTests
{
    [Theory]
    [InlineData("fpcalc version 1.6.2", "1.6.2")]
    [InlineData("fpcalc version 1.5.1\r\n", "1.5.1")]
    [InlineData("fpcalc 1.6", "1.6")]
    [InlineData("  Chromaprint fpcalc 1.6.2 (built with FFmpeg 6.1)\n", "1.6.2")]
    public void TheVersionNumber_IsTakenFromTheOutput(string output, string expected) => Assert.Equal(expected, FpcalcVersion.Parse(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fpcalc version unknown")]
    [InlineData("usage: fpcalc [OPTIONS] FILE")]
    public void NoNumber_GivesNull(string? output) => Assert.Null(FpcalcVersion.Parse(output));

    [Fact]
    public void AnIpAddressOrLongDottedNumber_IsNotAVersion() => Assert.Null(FpcalcVersion.Parse("connect 10.0.0.1.5"));
}
