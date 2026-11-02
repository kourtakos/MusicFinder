using System.Text;
using MusicFinder.Application.Health;

namespace MusicFinder.UnitTests.Health;

public class MojibakeDetectorTests
{
    private static string Damage(string text, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage).GetString(Encoding.UTF8.GetBytes(text));
    }

    [Theory]
    [InlineData("Χάρις Αλεξίου")]
    [InlineData("Παπάζογλου")]
    [InlineData("Σ' αγαπώ")]
    [InlineData("Μαζωνάκης – Γιώργος")]
    [InlineData("Über den Wolken")]
    [InlineData("Café del Mar")]
    [InlineData("Ñandú")]
    [InlineData("Łódź")]
    [InlineData("İstanbul")]
    [InlineData("Beyoncé feat. Jay-Z")]
    [InlineData("Mötley Crüe")]
    [InlineData("Plain English title")]
    [InlineData("What? Why?? ???")]
    [InlineData("")]
    public void RealText_IsNeverFlagged(string text)
    {
        Assert.False(MojibakeDetector.IsDamaged(text));
        Assert.Null(MojibakeDetector.Check(text).Repaired);
    }

    [Theory]
    [InlineData("Χάρις Αλεξίου", 28591)]
    [InlineData("Χάρις Αλεξίου", 1252)]
    [InlineData("Χάρις Αλεξίου", 1253)]
    [InlineData("Παπάζογλου", 28591)]
    [InlineData("Γιώργος Μαζωνάκης", 1252)]
    [InlineData("Café del Mar", 28591)]
    [InlineData("Beyoncé", 1252)]
    [InlineData("Mötley Crüe", 28591)]
    [InlineData("Łódź", 1252)]
    public void Utf8ReadAsASingleByteCodePage_IsFlagged_AndTheRepairIsTheOriginal(string original, int codePage)
    {
        var damaged = Damage(original, codePage);
        Assert.NotEqual(original, damaged);

        var (isDamaged, repaired) = MojibakeDetector.Check(damaged);

        Assert.True(isDamaged, damaged);
        Assert.Equal(original, repaired);
    }

    [Fact]
    public void TheReplacementCharacter_IsFlagged_WithoutARepair()
    {
        var (damaged, repaired) = MojibakeDetector.Check("Caf� del Mar");

        Assert.True(damaged);
        Assert.Null(repaired);
    }

    [Fact]
    public void Null_IsNotDamaged() => Assert.False(MojibakeDetector.IsDamaged(null));
}
