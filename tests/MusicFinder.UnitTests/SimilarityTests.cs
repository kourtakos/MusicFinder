using MusicFinder.Application.Similar;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class CamelotKeyTests
{
    private static CamelotKey Parse(string text)
    {
        Assert.True(CamelotKey.TryParse(text, out var key), $"'{text}' should parse");
        return key;
    }

    // The complete wheel, from the plan: minor 1A Ab/G# ... 12A Db/C#; major 1B B ... 12B E.
    [Theory]
    [InlineData("Abm", "1A")] [InlineData("G#m", "1A")] [InlineData("Ebm", "2A")] [InlineData("D#m", "2A")]
    [InlineData("Bbm", "3A")] [InlineData("A#m", "3A")] [InlineData("Fm", "4A")] [InlineData("Cm", "5A")] [InlineData("Gm", "6A")]
    [InlineData("Dm", "7A")] [InlineData("Am", "8A")] [InlineData("Em", "9A")] [InlineData("Bm", "10A")]
    [InlineData("F#m", "11A")] [InlineData("Gbm", "11A")] [InlineData("C#m", "12A")] [InlineData("Dbm", "12A")]
    [InlineData("B", "1B")] [InlineData("F#", "2B")] [InlineData("Gb", "2B")] [InlineData("Db", "3B")] [InlineData("C#", "3B")]
    [InlineData("Ab", "4B")] [InlineData("G#", "4B")] [InlineData("Eb", "5B")] [InlineData("D#", "5B")] [InlineData("Bb", "6B")] [InlineData("A#", "6B")]
    [InlineData("F", "7B")] [InlineData("C", "8B")] [InlineData("G", "9B")] [InlineData("D", "10B")] [InlineData("A", "11B")] [InlineData("E", "12B")]
    public void EveryNoteName_LandsOnItsCamelotPosition(string note, string camelot) => Assert.Equal(camelot, Parse(note).ToString());

    [Theory]
    [InlineData("8A")] [InlineData("08A")] [InlineData("8a")] [InlineData(" 8A ")] [InlineData("8 A")]
    public void CamelotNotation_IsForgiving(string text) => Assert.Equal("8A", Parse(text).ToString());

    [Theory]
    [InlineData("1m", "8A")] [InlineData("1d", "8B")] [InlineData("6m", "1A")] [InlineData("12d", "7B")] [InlineData("5D", "12B")]
    [InlineData("12m", "7A")] [InlineData("2d", "9B")]
    public void OpenKey_IsConvertedToCamelot(string openKey, string camelot) => Assert.Equal(camelot, Parse(openKey).ToString());

    [Theory]
    [InlineData("A minor", "8A")] [InlineData("Amin", "8A")] [InlineData("a minor", "8A")] [InlineData("C major", "8B")]
    [InlineData("Cmaj", "8B")] [InlineData("F# minor", "11A")] [InlineData("Gb major", "2B")] [InlineData("C♯m", "12A")] [InlineData("B♭", "6B")]
    [InlineData("AM", "11B")]                       // an uppercase M is major
    public void NoteNames_AcceptTheCommonSpellings(string text, string camelot) => Assert.Equal(camelot, Parse(text).ToString());

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData(null)] [InlineData("13A")] [InlineData("0A")] [InlineData("8C")]
    [InlineData("H")] [InlineData("Z minor")] [InlineData("8")] [InlineData("A#b")] [InlineData("major")] [InlineData("120")]
    public void Garbage_IsNotAKey(string? text) => Assert.False(CamelotKey.TryParse(text, out _));

    [Fact]
    public void NoteNamesAndCamelotAgree_SoMixedLibrariesCompareCorrectly()
    {
        Assert.Equal(Parse("Am"), Parse("8A"));
        Assert.Equal(Parse("C"), Parse("8B"));
        Assert.Equal(Parse("1m"), Parse("Am"));
    }

    [Fact]
    public void TheSameKey_ScoresOne()
    {
        var (v, why) = CamelotKey.Compare(Parse("8A"), Parse("Am"));
        Assert.Equal((1.0, "same key (8A)"), (v, why));
    }

    [Fact]
    public void TheRelativeKey_ScoresPointNine()
    {
        var (v, why) = CamelotKey.Compare(Parse("8A"), Parse("8B"));
        Assert.Equal((0.9, "relative key (8A → 8B)"), (v, why));
    }

    [Theory]
    [InlineData("8A", "9A")] [InlineData("8A", "7A")] [InlineData("12A", "1A")] [InlineData("1A", "12A")] [InlineData("12B", "1B")] [InlineData("3B", "2B")]
    public void NeighboursOnTheWheel_ScorePointEight_IncludingTheWrapAround(string seed, string other)
    {
        var (v, why) = CamelotKey.Compare(Parse(seed), Parse(other));
        Assert.Equal(0.8, v);
        Assert.StartsWith("adjacent key", why);
    }

    [Theory]
    [InlineData("8A", "9B")]          // a diagonal: not a rule
    [InlineData("8A", "10A")]
    [InlineData("8A", "2A")]
    [InlineData("8A", "7B")]
    [InlineData("1A", "11A")]
    public void EverythingElse_ScoresZero(string seed, string other)
    {
        var (v, why) = CamelotKey.Compare(Parse(seed), Parse(other));
        Assert.Equal(0, v);
        Assert.Null(why);
    }
}

public class GenreRelationsTests
{
    // Disco -> Nu Disco, Italo Disco;  Funk (a separate family);  Electronic -> House
    private static readonly Genre[] Tree =
    [
        new() { Id = 1, Name = "Disco" }, new() { Id = 2, ParentId = 1, Name = "Nu Disco" }, new() { Id = 3, ParentId = 1, Name = "Italo Disco" },
        new() { Id = 4, Name = "Funk" }, new() { Id = 5, Name = "Electronic" }, new() { Id = 6, ParentId = 5, Name = "House" }
    ];

    private static (double Value, string? Reason)? Compare(long[] a, long[] b) => new GenreRelations(Tree).Compare(a, b);

    [Fact]
    public void TheSameGenre_IsAFullMatch() => Assert.Equal((1.0, "same genre (Nu Disco)"), Compare([2], [2]));

    [Fact]
    public void ParentAndChild_AreRelated() => Assert.Equal((0.6, "related genre (Disco ≈ Nu Disco)"), Compare([1], [2]));

    [Fact]
    public void ChildAndParent_AreRelated() => Assert.Equal(0.6, Compare([2], [1])!.Value.Value);

    [Fact]
    public void Siblings_AreRelated() => Assert.Equal(0.6, Compare([2], [3])!.Value.Value);

    [Fact]
    public void UnrelatedGenres_ScoreZero() => Assert.Equal((0, (string?)null), Compare([1], [4]));

    [Fact]
    public void TheBestPairWins_WhenATrackHasSeveralGenres() => Assert.Equal(1.0, Compare([4, 2], [6, 2, 1])!.Value.Value);

    [Fact]
    public void WithoutGenresOnEitherSide_ThereIsNoInformation()
    {
        Assert.Null(Compare([], [1]));
        Assert.Null(Compare([1], []));
        Assert.Null(Compare([], []));
    }

    [Fact]
    public void UnknownIds_AreHarmless() => Assert.Equal(0, Compare([999], [1])!.Value.Value);

    [Fact]
    public void RelatedIds_CoverTheWholeFamilyOfAGenre()
    {
        var related = new GenreRelations(Tree).RelatedIds([2]);

        Assert.Equal(new long[] { 1, 2, 3 }, related.Order().ToArray());      // Disco, Nu Disco, Italo Disco; not Funk or House
    }
}

public class SimilarityScorerTests
{
    private static readonly Genre[] Tree =
    [
        new() { Id = 1, Name = "Disco" }, new() { Id = 2, ParentId = 1, Name = "Nu Disco" },
        new() { Id = 4, Name = "Funk" }, new() { Id = 5, Name = "Electronic" }
    ];

    private static readonly Dictionary<long, string> Tags = new() { [10] = "Warm Up", [11] = "Peak Time", [12] = "Wedding" };

    private static SimilarityScorer Scorer(SimilarTracksOptions? options = null) =>
        new(options ?? new SimilarTracksOptions(), new GenreRelations(Tree), Tags);

    private static SimilarSignals Track(long id = 1, double? bpm = null, string? key = null, long[]? genres = null, long[]? tags = null,
        string title = "T", string? artist = null, int? year = null) =>
        new(id, title, artist, year, bpm, key, genres ?? [], tags ?? []);

    private static readonly SimilarSignals Seed = Track(0, 120, "8A", [1]);

    // ===== the numbers from the design: weights 35 / 30 / 25 / 10, re-normalized =====

    [Fact]
    public void ARankingExample_WithoutTags()
    {
        // Same genre 35 + tempo +3 of 8 (0.625 x 30 = 18.75) + adjacent key (0.8 x 25 = 20) = 73.75 of 90 available.
        var a = Scorer().Score(Seed, Track(1, 123, "9A", [1]))!;
        Assert.Equal(82, a.Score);
        Assert.Equal(90, a.AvailableWeight);

        // Related genre (0.6 x 35 = 21) + same tempo 30 + same key 25 = 76 of 90.
        var b = Scorer().Score(Seed, Track(2, 120, "8A", [2]))!;
        Assert.Equal(84, b.Score);
    }

    [Fact]
    public void AnIdenticalTrack_ScoresOneHundred()
    {
        var s = Scorer().Score(Seed, Track(1, 120, "8A", [1]))!;
        Assert.Equal(100, s.Score);
        Assert.Equal(["same genre (Disco)", "same BPM", "same key (8A)"], s.Reasons);
    }

    [Fact]
    public void ANothingInCommonTrack_ScoresZero_ButIsStillComparable()
    {
        var s = Scorer().Score(Seed, Track(1, 175, "3B", [4]))!;
        Assert.Equal(0, s.Score);
        Assert.Equal(90, s.AvailableWeight);
        Assert.Empty(s.Reasons);
    }

    // ===== missing values never lower a score and are never invented =====

    [Fact]
    public void AMissingKeyOnTheCandidate_DropsTheKeyWeight_InsteadOfCountingAsZero()
    {
        var s = Scorer().Score(Seed, Track(1, 120, null, [1]))!;

        Assert.Equal(65, s.AvailableWeight);          // genre 35 + tempo 30
        Assert.Equal(100, s.Score);                   // a perfect match on everything that CAN be compared
    }

    [Fact]
    public void AMissingKeyOnTheSeed_DoesTheSame()
    {
        var s = Scorer().Score(Track(0, 120, null, [1]), Track(1, 120, "8A", [1]))!;
        Assert.Equal(65, s.AvailableWeight);
        Assert.Equal(100, s.Score);
    }

    [Fact]
    public void ABpmOfZero_IsNotABpm()
    {
        var s = Scorer().Score(Seed, Track(1, 0, "8A", [1]))!;
        Assert.Equal(60, s.AvailableWeight);          // genre 35 + key 25: below the 55 floor? no, 60 >= 55
        Assert.DoesNotContain(s.Reasons, r => r.StartsWith("BPM"));
    }

    [Fact]
    public void AnUnparseableKey_IsTreatedAsNoKey()
    {
        var s = Scorer().Score(Seed, Track(1, 120, "banana", [1]))!;
        Assert.Equal(65, s.AvailableWeight);
    }

    [Fact]
    public void TooLittleToCompareBy_GivesNoScoreAtAll()
    {
        // Only a genre in common (35 of 100): below the 55 floor. The score would claim more than the data supports.
        Assert.Null(Scorer().Score(Seed, Track(1, null, null, [1])));
        // Only tempo and nothing else.
        Assert.Null(Scorer().Score(Seed, Track(1, 120)));
    }

    [Fact]
    public void TheFloor_CanBeChanged()
    {
        var s = Scorer(new SimilarTracksOptions { MinAvailableWeight = 30 }).Score(Seed, Track(1, 120));
        Assert.NotNull(s);
        Assert.Equal(30, s!.AvailableWeight);
    }

    [Fact]
    public void SignalCount_IsTwoForGenreAndKey() =>
        Assert.Equal(2, SimilarityScorer.SignalCount(Track(1, null, "8A", [1])));

    [Fact]
    public void SignalCount_IsZeroForATitleOnlyTrack() => Assert.Equal(0, SimilarityScorer.SignalCount(Track(1)));

    // ===== tempo =====

    [Theory]
    [InlineData(120, 100)]
    [InlineData(124, 50)]
    [InlineData(116, 50)]
    [InlineData(128, 0)]
    [InlineData(112, 0)]
    [InlineData(140, 0)]
    public void TheTempoScore_FallsLinearlyToTheEdgeOfTheWindow(double bpm, int expectedScoreOfThirty)
    {
        var s = Scorer(new SimilarTracksOptions { AllowHalfDoubleTempo = false, MinAvailableWeight = 30 }).Score(Track(0, 120), Track(1, bpm))!;
        Assert.Equal(expectedScoreOfThirty, s.Score);
    }

    [Theory]
    [InlineData(123, "BPM +3")]
    [InlineData(117, "BPM -3")]
    [InlineData(120, "same BPM")]
    [InlineData(123.5, "BPM +3.5")]
    public void TheTempoReason_ShowsTheSignedDifference(double bpm, string reason)
    {
        var s = Scorer().Score(Seed, Track(1, bpm, "8A", [1]))!;
        Assert.Contains(reason, s.Reasons);
    }

    [Fact]
    public void DoubleTime_IsAMatch_AndSaysSo()
    {
        var seed = Track(0, 70, "8A", [1]);
        var s = Scorer().Score(seed, Track(1, 140, "8A", [1]))!;

        Assert.Equal(100, s.Score);
        Assert.True(s.UsedHalfDoubleTempo);
        Assert.Contains("BPM ×2 (+0)", s.Reasons);
    }

    [Fact]
    public void HalfTime_IsAMatch_AndSaysSo()
    {
        var s = Scorer().Score(Track(0, 140, "8A", [1]), Track(1, 71, "8A", [1]))!;

        Assert.True(s.UsedHalfDoubleTempo);
        Assert.Contains("BPM ½ (+1)", s.Reasons);
    }

    [Fact]
    public void WithHalfDoubleOff_SeventyAndOneForty_DoNotMatch()
    {
        var s = Scorer(new SimilarTracksOptions { AllowHalfDoubleTempo = false }).Score(Track(0, 70, "8A", [1]), Track(1, 140, "8A", [1]))!;

        Assert.False(s.UsedHalfDoubleTempo);
        Assert.DoesNotContain(s.Reasons, r => r.StartsWith("BPM"));
        Assert.Equal(67, s.Score);                              // genre 35 + key 25 earned out of 90 available (tempo was comparable but too far apart) = 66.7
    }

    [Fact]
    public void ADirectMatch_NeverCarriesAHalfDoubleLabel()
    {
        var s = Scorer().Score(Seed, Track(1, 121, "8A", [1]))!;

        Assert.False(s.UsedHalfDoubleTempo);
        Assert.DoesNotContain(s.Reasons, r => r.Contains('×') || r.Contains('½'));
    }

    [Fact]
    public void TheBpmWindow_CanBeChanged()
    {
        var narrow = Scorer(new SimilarTracksOptions { BpmWindow = 2, AllowHalfDoubleTempo = false }).Score(Seed, Track(1, 123, "8A", [1]))!;
        var wide = Scorer(new SimilarTracksOptions { BpmWindow = 20, AllowHalfDoubleTempo = false }).Score(Seed, Track(1, 123, "8A", [1]))!;

        Assert.True(wide.Score > narrow.Score);
    }

    // ===== tags =====

    [Fact]
    public void Tags_OnlyCountWhenTheSeedHasSome()
    {
        var withoutTags = Scorer().Score(Seed, Track(1, 120, "8A", [1], [10]))!;
        Assert.Equal(90, withoutTags.AvailableWeight);

        var seed = Track(0, 120, "8A", [1], [10]);
        var withTags = Scorer().Score(seed, Track(1, 120, "8A", [1], [10]))!;
        Assert.Equal(100, withTags.AvailableWeight);
        Assert.Contains("shared tag: Warm Up", withTags.Reasons);
    }

    [Fact]
    public void TagOverlap_IsMeasuredByJaccard()
    {
        var seed = Track(0, 120, "8A", [1], [10, 11]);
        // One shared of three distinct: 1/3 x 10 points.
        var s = Scorer().Score(seed, Track(1, 120, "8A", [1], [10, 12]))!;

        Assert.Equal(93, s.Score);                              // (35 + 30 + 25 + 3.33) / 100 = 93.3
        Assert.Contains("shared tag: Warm Up", s.Reasons);
    }

    [Fact]
    public void ACandidateWithoutTags_LosesTheTagPoints_WhenTheSeedHasTags()
    {
        var seed = Track(0, 120, "8A", [1], [10]);
        var s = Scorer().Score(seed, Track(1, 120, "8A", [1]))!;

        Assert.Equal(90, s.Score);                              // 90 of 100: the seed asked for a tag the candidate lacks
    }

    // ===== context that is reported but not scored =====

    [Fact]
    public void SameArtistAndDecade_AreReasons_NotPoints()
    {
        var seed = Track(0, 120, "8A", [1], artist: "Chic", year: 1978);
        var s = Scorer().Score(seed, Track(1, 120, "8A", [1], artist: "CHIC", year: 1979))!;

        Assert.Equal(100, s.Score);
        Assert.Contains("same artist", s.Reasons);
        Assert.Contains("same decade (1970s)", s.Reasons);
        Assert.True(s.SameArtist);
    }

    [Fact]
    public void ADifferentArtistAndDecade_AddsNoReason()
    {
        var seed = Track(0, 120, "8A", [1], artist: "Chic", year: 1978);
        var s = Scorer().Score(seed, Track(1, 120, "8A", [1], artist: "Daft Punk", year: 2001))!;

        Assert.DoesNotContain(s.Reasons, r => r.StartsWith("same artist") || r.StartsWith("same decade"));
    }

    [Fact]
    public void EveryScoredMatch_CarriesAtLeastOneReason_UnlessItScoredZero()
    {
        foreach (var c in new[] { Track(1, 121, "8A", [1]), Track(2, 120, "8A", [4]), Track(3, 128, "9A", [2]), Track(4, 100, "8B", [1]) })
        {
            var s = Scorer().Score(Seed, c)!;
            if (s.Score > 0) Assert.NotEmpty(s.Reasons);
        }
    }

    // ===== ordering =====

    private static List<long> Rank(IEnumerable<SimilarSignals> candidates, SimilarSignals? seed = null)
    {
        var scorer = Scorer();
        var list = candidates.Select(c => (Track: c, Score: scorer.Score(seed ?? Seed, c))).Where(x => x.Score is not null)
            .Select(x => (x.Track, Score: x.Score!)).ToList();
        list.Sort(SimilarityScorer.Compare);
        return list.Select(x => x.Track.TrackId).ToList();
    }

    [Fact]
    public void HigherScoresComeFirst() =>
        Assert.Equal([2L, 1L], Rank([Track(1, 123, "9A", [1]), Track(2, 120, "8A", [1])]));

    [Fact]
    public void EqualScores_AreOrderedByTitleThenId_SoTheOrderIsStable()
    {
        var ranked = Rank([Track(3, 120, "8A", [1], title: "beta"), Track(1, 120, "8A", [1], title: "Alpha"),
                           Track(2, 120, "8A", [1], title: "alpha"), Track(4, 120, "8A", [1], title: "Beta")]);

        Assert.Equal([1L, 2L, 3L, 4L], ranked);                 // alpha/Alpha tie on title: lower id first; same for beta
    }

    [Fact]
    public void TheSameArtist_BreaksATie()
    {
        var seed = Track(0, 120, "8A", [1], artist: "Chic");
        var ranked = Rank([Track(1, 120, "8A", [1], title: "A", artist: "Other"), Track(2, 120, "8A", [1], title: "Z", artist: "Chic")], seed);

        Assert.Equal([2L, 1L], ranked);
    }

    [Fact]
    public void ADirectTempoMatch_BeatsAHalfDoubleOne_WhenScoresAreEqual()
    {
        var scorer = Scorer();
        var seed = Track(0, 70, "8A", [1]);
        var direct = scorer.Score(seed, Track(1, 70, "8A", [1]))!;
        var doubled = scorer.Score(seed, Track(2, 140, "8A", [1]))!;

        Assert.Equal((100, 100), (direct.Score, doubled.Score));
        Assert.True(SimilarityScorer.Compare((Track(2, 140, title: "A"), doubled), (Track(1, 70, title: "Z"), direct)) > 0);   // the doubled one sorts after, whatever the titles
    }

    [Fact]
    public void ACloserTempo_BreaksATie_WhenTheScoresRoundTogether()
    {
        // Both round to the same score; the one nearer in tempo comes first (before the title decides).
        var scorer = Scorer(new SimilarTracksOptions { BpmWindow = 1000 });
        var near = scorer.Score(Seed, Track(1, 121, "8A", [1]))!;
        var far = scorer.Score(Seed, Track(2, 122, "8A", [1]))!;

        Assert.Equal(near.Score, far.Score);
        Assert.True(SimilarityScorer.Compare((Track(1, 121, title: "Z"), near), (Track(2, 122, title: "A"), far)) < 0);
    }

    [Fact]
    public void ShuffledInput_GivesTheSameOrder()
    {
        var all = Enumerable.Range(1, 40).Select(i => Track(i, 110 + i % 20, i % 3 == 0 ? "9A" : "8A", [i % 2 == 0 ? 1L : 2L], title: "T" + i % 7)).ToList();
        var expected = Rank(all);

        for (var seed = 0; seed < 5; seed++)
        {
            var shuffled = all.OrderBy(_ => new Random(seed).Next()).OrderBy(x => (x.TrackId * 7919 + seed) % 13).ToList();
            Assert.Equal(expected, Rank(shuffled));
        }
    }

    [Fact]
    public void ScoringTheSamePairTwice_GivesTheSameResult()
    {
        var c = Track(1, 123, "9A", [2], [10]);
        Assert.Equal(Scorer().Score(Seed, c), Scorer().Score(Seed, c), new ScoreComparer());
    }

    private sealed class ScoreComparer : IEqualityComparer<SimilarityScore?>
    {
        public bool Equals(SimilarityScore? a, SimilarityScore? b) =>
            a is not null && b is not null && a.Score == b.Score && a.AvailableWeight == b.AvailableWeight && a.Reasons.SequenceEqual(b.Reasons);
        public int GetHashCode(SimilarityScore? s) => s?.Score ?? 0;
    }
}
