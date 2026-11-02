using MusicFinder.Application.Duplicates;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Duplicates;

public class SongIdentityTests
{
    [Theory]
    [InlineData("Frozen", "Frozen", "")]
    [InlineData("Frozen (Radio Edit)", "Frozen", "radio edit")]
    [InlineData("Frozen [Radio Edit]", "Frozen", "radio edit")]
    [InlineData("Frozen - Radio Edit", "Frozen", "radio edit")]
    [InlineData("Frozen (Original Mix)", "Frozen", "")]
    [InlineData("Frozen (Album Version)", "Frozen", "")]
    [InlineData("Frozen (Extended Mix)", "Frozen", "extended")]
    [InlineData("Frozen (Live at Wembley)", "Frozen", "live")]
    [InlineData("Frozen (Instrumental)", "Frozen", "instrumental")]
    [InlineData("Frozen (Thin White Duke Remix)", "Frozen", "remix:thin white duke")]
    [InlineData("Frozen - Thin White Duke Remix", "Frozen", "remix:thin white duke")]
    [InlineData("Frozen (2011 Remastered)", "Frozen", "remaster")]
    public void VersionWords_AreTakenOutOfTheTitle(string title, string baseTitle, string versions)
    {
        var p = SongIdentity.Parse("Madonna", title);
        Assert.Equal(baseTitle, p.BaseTitle);
        Assert.Equal(versions, p.VersionText);
    }

    [Theory]
    [InlineData("Song (Part 2)")]
    [InlineData("Song (1)")]
    [InlineData("Song - Part Two")]
    [InlineData("Song (Feelings)")]
    public void OtherBrackets_StayInTheTitle_SoDifferentSongsAreNotMerged(string title)
    {
        var p = SongIdentity.Parse("A", title);
        Assert.Equal("", p.VersionText);
        Assert.NotEqual(SongIdentity.Parse("A", "Song").TitleKey, p.TitleKey);
    }

    [Fact]
    public void FeaturedArtists_AreParsedFromTheArtistAndTheTitle_AndTheMainArtistKeepsTheSong()
    {
        var a = SongIdentity.Parse("Madonna feat. Justin Timberlake", "4 Minutes");
        var b = SongIdentity.Parse("Madonna", "4 Minutes (feat. Justin Timberlake)");
        var c = SongIdentity.Parse("Madonna", "4 Minutes ft. Justin Timberlake");

        Assert.Equal(SongIdentity.Parse("Madonna", "4 Minutes").SongKey, a.SongKey);
        Assert.Equal(a.SongKey, b.SongKey);
        Assert.Equal(a.SongKey, c.SongKey);
        Assert.Single(a.Featured);
        Assert.Equal(a.Featured, b.Featured);
    }

    [Fact]
    public void GreekAndGreeklish_GiveTheSameSong()
    {
        var greek = SongIdentity.Parse("Χάρις Αλεξίου", "Σ' αγαπώ");
        var latin = SongIdentity.Parse("Xaris Alexiou", "S agapo");
        Assert.Equal(greek.SongKey, latin.SongKey);
    }

    [Fact]
    public void CaseAccentsAndPunctuation_DoNotMatter() =>
        Assert.Equal(SongIdentity.Parse("Mr. Brightside", "Mr Brightside").TitleKey, SongIdentity.Parse("X", "MR. BRIGHTSIDE").TitleKey);

    [Fact]
    public void DifferentSongsOfOneArtist_AreDifferent() =>
        Assert.NotEqual(SongIdentity.Parse("Madonna", "Frozen").SongKey, SongIdentity.Parse("Madonna", "Vogue").SongKey);
}

public class ProbableDuplicateGroupingTests
{
    private static SongCandidate T(long id, string artist, string title, int? seconds, string ext = ".mp3", int? kbps = 320, int plays = 0) =>
        new(id, $@"C:\M\{id}{ext}", ext, artist, title, seconds is null ? null : seconds * 1000, kbps, null, plays);

    private static ProbableDuplicateResult Run(params SongCandidate[] tracks) =>
        ProbableDuplicateService.Group(tracks, new HashSet<string>());

    [Fact]
    public void SameArtistTitleAndVersion_WithClosedLengths_IsOneSameRecordingGroup()
    {
        var r = Run(T(1, "Madonna", "Frozen", 372), T(2, "madonna", "FROZEN", 374), T(3, "Madonna", "Vogue", 300));

        var g = Assert.Single(r.SameRecording);
        Assert.Equal([1L, 2L], g.Members.Select(m => m.TrackId).Order());
        Assert.Empty(r.OtherVersions);
    }

    [Fact]
    public void ARemix_IsAnotherVersion_NotAProbableDuplicate()
    {
        var r = Run(T(1, "Madonna", "Frozen", 372), T(2, "Madonna", "Frozen (Thin White Duke Remix)", 372));

        Assert.Empty(r.SameRecording);
        var g = Assert.Single(r.OtherVersions);
        Assert.Equal(2, g.Members.Count);
        Assert.Contains(g.Members, m => m.VersionLabel.StartsWith("remix thin white duke"));
    }

    [Fact]
    public void ADifferentLength_SplitsASameRecordingGroup_AndShowsAsAnotherVersion()
    {
        var r = Run(T(1, "A", "Song", 200), T(2, "A", "Song", 201), T(3, "A", "Song", 260));

        var same = Assert.Single(r.SameRecording);
        Assert.Equal([1L, 2L], same.Members.Select(m => m.TrackId).Order());
        var other = Assert.Single(r.OtherVersions);
        Assert.Equal(3, other.Members.Count);
    }

    [Fact]
    public void TheToleranceIsThreeSeconds()
    {
        Assert.Single(Run(T(1, "A", "S", 200), T(2, "A", "S", 203)).SameRecording);
        Assert.Empty(Run(T(1, "A", "S", 200), T(2, "A", "S", 204)).SameRecording);
    }

    [Fact]
    public void UnknownDurations_NeverMakeASameRecordingGroup_ButTheSongStillShowsItsVersions()
    {
        var r = Run(T(1, "A", "S", null), T(2, "A", "S", null));
        Assert.Empty(r.SameRecording);
        Assert.Empty(r.OtherVersions);                       // two unknowns are one variant, nothing to compare

        var mixed = Run(T(1, "A", "S", null), T(2, "A", "S", 200));
        Assert.Empty(mixed.SameRecording);
        Assert.Single(mixed.OtherVersions);
    }

    [Fact]
    public void TracksWithoutArtistOrTitle_AreLeftOut()
    {
        var r = Run(T(1, "", "Song", 200), T(2, "", "Song", 200), T(3, "A", " ", 200), T(4, "A", " ", 200));
        Assert.Empty(r.SameRecording);
        Assert.Empty(r.OtherVersions);
    }

    [Fact]
    public void GreekAndGreeklishTags_AreTheSameSong()
    {
        var r = Run(T(1, "Χάρις Αλεξίου", "Σ' αγαπώ", 240), T(2, "Xaris Alexiou", "S agapo", 241));
        Assert.Single(r.SameRecording);
    }

    [Fact]
    public void ADismissedGroup_StaysHidden()
    {
        var tracks = new[] { T(1, "A", "Song", 200), T(2, "A", "Song", 200) };
        var key = Assert.Single(ProbableDuplicateService.Group(tracks, new HashSet<string>()).SameRecording).Key;

        Assert.Empty(ProbableDuplicateService.Group(tracks, new HashSet<string> { key }).SameRecording);
    }

    [Fact]
    public void TheSuggestion_PrefersLossless_ThenBitrate_ThenPlays_AndSaysWhy()
    {
        var lossless = ProbableDuplicateService.BestQuality([T(1, "A", "S", 200, ".mp3", 320), T(2, "A", "S", 200, ".flac", null)]);
        Assert.Equal((2L, "lossless format"), lossless);

        var bitrate = ProbableDuplicateService.BestQuality([T(1, "A", "S", 200, ".mp3", 128), T(2, "A", "S", 200, ".mp3", 320)]);
        Assert.Equal(2L, bitrate.TrackId);
        Assert.Contains("320", bitrate.Reason);

        var plays = ProbableDuplicateService.BestQuality([T(1, "A", "S", 200, plays: 0), T(2, "A", "S", 200, plays: 4)]);
        Assert.Equal((2L, "most plays (4)"), plays);
    }

    [Fact]
    public void TheOrderIsStable()
    {
        var a = Run(T(5, "B", "Z", 100), T(6, "B", "Z", 100), T(1, "A", "Y", 100), T(2, "A", "Y", 100));
        var b = Run(T(2, "A", "Y", 100), T(1, "A", "Y", 100), T(6, "B", "Z", 100), T(5, "B", "Z", 100));
        Assert.Equal(a.SameRecording.Select(g => g.Key), b.SameRecording.Select(g => g.Key));
        Assert.Equal(["A — Y", "B — Z"], a.SameRecording.Select(g => g.Heading));
    }
}
