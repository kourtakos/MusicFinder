using MusicFinder.Application.Identification;

namespace MusicFinder.UnitTests;

public class AcoustIdKeyFormatTests
{
    [Theory]
    [InlineData("abcd1234")]
    [InlineData("  AbCd1234  ")]
    [InlineData("\"abcd1234\"")]
    [InlineData("“abcd1234”")]
    [InlineData("'abcd1234'")]
    [InlineData("abcd1234\r\n")]
    public void AShortLetterAndDigitCode_IsAccepted_AndCleaned(string pasted)
    {
        var check = AcoustIdKeyFormat.Validate(pasted);

        Assert.True(check.IsValid);
        Assert.Equal(pasted.Trim().Trim('"', '\'', '“', '”').Trim(), check.Key);
        Assert.Null(check.Problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingPasted_AsksForTheKey(string? pasted)
    {
        var check = AcoustIdKeyFormat.Validate(pasted);

        Assert.False(check.IsValid);
        Assert.Contains("Paste", check.Problem);
    }

    [Theory]
    [InlineData("https://acoustid.org/new-application")]
    [InlineData("acoustid.org/my-applications")]
    [InlineData("www.acoustid.org")]
    public void AWebAddress_IsRecognisedAsTheCommonMistake(string pasted)
    {
        var check = AcoustIdKeyFormat.Validate(pasted);

        Assert.False(check.IsValid);
        Assert.Contains("web address", check.Problem);
    }

    [Theory]
    [InlineData("my key abc12345")]
    [InlineData("abcd 1234")]
    [InlineData("abcd\t1234")]
    public void Spaces_AreRejected(string pasted)
    {
        Assert.False(AcoustIdKeyFormat.Validate(pasted).IsValid);
        Assert.Contains("no spaces", AcoustIdKeyFormat.Validate(pasted).Problem);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a")]
    public void TooShort_IsRejected(string pasted) => Assert.Contains("too short", AcoustIdKeyFormat.Validate(pasted).Problem);

    [Fact]
    public void TooLong_IsRejected_AndTheBoundariesAreInclusive()
    {
        Assert.True(AcoustIdKeyFormat.Validate(new string('a', AcoustIdKeyFormat.MaxLength)).IsValid);
        Assert.False(AcoustIdKeyFormat.Validate(new string('a', AcoustIdKeyFormat.MaxLength + 1)).IsValid);
        Assert.True(AcoustIdKeyFormat.Validate(new string('a', AcoustIdKeyFormat.MinLength)).IsValid);
    }

    [Theory]
    [InlineData("abcd-1234")]
    [InlineData("abcd_1234")]
    [InlineData("abcdé123")]
    [InlineData("αβγδ1234")]
    [InlineData("abcd1234!")]
    public void OtherCharacters_AreRejected(string pasted) => Assert.Contains("letters and digits", AcoustIdKeyFormat.Validate(pasted).Problem);
}
