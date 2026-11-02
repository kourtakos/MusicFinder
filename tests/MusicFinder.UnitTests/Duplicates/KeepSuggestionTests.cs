using MusicFinder.Application.Duplicates;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Duplicates;

public class KeepSuggestionTests
{
    private static DuplicateMember M(long id, string path, int plays = 0) =>
        new(id, path, ".mp3", 1000, "A", "T", 320, "MPEG", plays, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void TheCopyWithPlayHistory_IsSuggested_AndTheReasonSaysWhy()
    {
        var (id, reasons) = KeepSuggestion.Pick([M(1, @"C:\Music\a.mp3"), M(2, @"C:\Music\Old\a.mp3", plays: 3)]);

        Assert.Equal(2, id);
        Assert.Contains(reasons, r => r.Contains("most plays (3)"));
    }

    [Theory]
    [InlineData(@"C:\Music\Song (1).mp3")]
    [InlineData(@"C:\Music\Song - Copy.mp3")]
    [InlineData(@"C:\Music\Song copy.mp3")]
    [InlineData(@"C:\Music\Copy of Song.mp3")]
    [InlineData(@"C:\Music\Song_copy 2.mp3")]
    public void ANameThatLooksLikeACopy_IsNotTheOneKept(string copyName)
    {
        Assert.True(KeepSuggestion.LooksLikeACopy(copyName));

        var (id, reasons) = KeepSuggestion.Pick([M(1, copyName), M(2, @"C:\Music\Song.mp3")]);

        Assert.Equal(2, id);
        Assert.Contains(reasons, r => r.Contains("named like copies"));
    }

    [Theory]
    [InlineData(@"C:\Music\Song.mp3")]
    [InlineData(@"C:\Music\Χάρις Αλεξίου - Σ' αγαπώ.mp3")]
    [InlineData(@"C:\Music\Copycat.mp3")]
    [InlineData(@"C:\Music\Song (Radio Edit).mp3")]
    [InlineData(@"C:\Music\Photocopy Machine.mp3")]
    public void OrdinaryNames_DoNotLookLikeCopies(string name) => Assert.False(KeepSuggestion.LooksLikeACopy(name));

    [Fact]
    public void WithNothingToTellThemApart_TheShallowerPathWins_ThenThePathOrder()
    {
        Assert.Equal(2, KeepSuggestion.Pick([M(1, @"C:\Music\Deep\Er\a.mp3"), M(2, @"C:\Music\a.mp3")]).TrackId);
        Assert.Equal(1, KeepSuggestion.Pick([M(2, @"C:\Music\b.mp3"), M(1, @"C:\Music\a.mp3")]).TrackId);
    }

    [Fact]
    public void Identical_CopiesGetAnHonestReason()
    {
        var (_, reasons) = KeepSuggestion.Pick([M(1, @"C:\Music\a.mp3"), M(2, @"C:\Music\b.mp3")]);

        Assert.Equal(["The copies are indistinguishable; this one comes first by path"], reasons);
    }

    [Fact]
    public void TheSuggestion_DoesNotDependOnTheOrderTheyAreGivenIn()
    {
        var a = M(1, @"C:\Music\Song (1).mp3"); var b = M(2, @"C:\Music\Song.mp3", plays: 1); var c = M(3, @"C:\Music\X\Song.mp3");

        var first = KeepSuggestion.Pick([a, b, c]);
        var second = KeepSuggestion.Pick([c, a, b]);

        Assert.Equal(first.TrackId, second.TrackId);
        Assert.Equal(first.Reasons, second.Reasons);
    }

    [Fact]
    public void ASingleMember_IsItsOwnSuggestion() => Assert.Equal(7, KeepSuggestion.Pick([M(7, @"C:\a.mp3")]).TrackId);

    [Fact]
    public void AnEmptyGroup_IsRejected() => Assert.Throws<ArgumentException>(() => KeepSuggestion.Pick([]));
}
