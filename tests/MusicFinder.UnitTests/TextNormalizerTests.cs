using MusicFinder.Application.Services;

namespace MusicFinder.UnitTests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Αλεξίου", "αλεξιου")]
    [InlineData("Χάρις Αλεξίου", "χαρισ αλεξιου")]
    [InlineData("Παπάζογλου", "παπαζογλου")]
    [InlineData("ΜΑΖΩΝΑΚΗΣ", "μαζωνακησ")]
    [InlineData("Beyoncé – Déjà Vu", "beyonce deja vu")]
    [InlineData("  Chic   -  Le Freak ", "chic le freak")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_FoldsCaseDiacriticsAndSigma(string? input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void FinalSigma_EqualsMedialSigma() =>
        Assert.Equal(TextNormalizer.Normalize("Μαζωνάκης"), TextNormalizer.Normalize("ΜΑΖΩΝΑΚΗΣ"));

    [Fact]
    public void BuildSearchText_SkipsEmptyParts() =>
        Assert.Equal("chic le freak", TextNormalizer.BuildSearchText("Chic", null, " ", "Le Freak"));
}
