using MusicFinder.Application.Health;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Health;

public class QualityRulesTests
{
    private static TrackMetadata Mp3(int kbps = 320, int rate = 44100, long ms = 240_000, string codec = "MPEG Version 1 Audio, Layer 3") =>
        new() { BitrateKbps = kbps, SampleRateHz = rate, DurationMs = ms, Codec = codec, Title = "T", Artist = "A" };

    private static long SizeFor(int kbps, long ms) => (long)(kbps * 1000.0 / 8 * ms / 1000.0);

    [Fact]
    public void ANormalFile_HasNoFlags() =>
        Assert.Equal(IssueFlags.None, QualityRules.Evaluate(".mp3", SizeFor(320, 240_000), Mp3(), false));

    [Theory]
    [InlineData(64, true)]
    [InlineData(127, true)]
    [InlineData(128, false)]
    [InlineData(320, false)]
    public void ALossyFileBelow128_IsLowBitrate(int kbps, bool flagged)
    {
        var flags = QualityRules.Evaluate(".mp3", SizeFor(kbps, 240_000), Mp3(kbps), false);
        Assert.Equal(flagged, flags.HasFlag(IssueFlags.LowBitrate));
    }

    [Fact]
    public void ALosslessFile_IsNeverLowBitrate_AndNeverSizeChecked()
    {
        var meta = Mp3(kbps: 50, codec: "FLAC");
        var flags = QualityRules.Evaluate(".flac", 100, meta, false);
        Assert.False(flags.HasFlag(IssueFlags.LowBitrate));
        Assert.False(flags.HasFlag(IssueFlags.SizeMismatch));
    }

    [Theory]
    [InlineData(44100, false)]
    [InlineData(48000, false)]
    [InlineData(96000, false)]
    [InlineData(45000, true)]
    [InlineData(12345, true)]
    public void AnUnusualSampleRate_IsFlagged(int rate, bool flagged) =>
        Assert.Equal(flagged, QualityRules.Evaluate(".mp3", SizeFor(320, 240_000), Mp3(rate: rate), false).HasFlag(IssueFlags.OddSampleRate));

    [Fact]
    public void AnUnknownSampleRate_IsNotFlagged() =>
        Assert.False(QualityRules.Evaluate(".mp3", SizeFor(320, 240_000), Mp3() with { SampleRateHz = null }, false).HasFlag(IssueFlags.OddSampleRate));

    [Theory]
    [InlineData(".mp3", "FLAC", true)]
    [InlineData(".flac", "MPEG Version 1 Audio, Layer 3", true)]
    [InlineData(".mp3", "MPEG Version 1 Audio, Layer 3", false)]
    [InlineData(".flac", "FLAC", false)]
    [InlineData(".m4a", "MPEG-4 Audio (mp4a)", false)]
    [InlineData(".m4a", "AAC", false)]
    [InlineData(".wav", "PCM", false)]
    [InlineData(".ogg", "Vorbis", false)]
    [InlineData(".mp3", null, false)]
    public void TheCodecIsComparedWithTheExtension_AndAnUnknownCodecIsNotAMismatch(string ext, string? codec, bool flagged)
    {
        var meta = Mp3(codec: codec!) with { Codec = codec };
        Assert.Equal(flagged, QualityRules.Evaluate(ext, SizeFor(320, 240_000), meta, false).HasFlag(IssueFlags.CodecMismatch));
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0.84, true)]
    [InlineData(0.9, false)]
    [InlineData(1.0, false)]
    [InlineData(1.3, false)]
    [InlineData(2.0, true)]
    public void ALossyFileMuchSmallerOrLargerThanItsBitrateImplies_IsFlagged(double ratio, bool flagged)
    {
        var size = (long)(SizeFor(256, 300_000) * ratio);
        Assert.Equal(flagged, QualityRules.Evaluate(".mp3", size, Mp3(256, ms: 300_000), false).HasFlag(IssueFlags.SizeMismatch));
    }

    [Fact]
    public void ATinyFile_IsNotSizeChecked() =>
        Assert.False(QualityRules.Evaluate(".mp3", 100, Mp3(320, ms: 1000), false).HasFlag(IssueFlags.SizeMismatch));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void NoDuration_IsFlagged(long? ms) =>
        Assert.True(QualityRules.Evaluate(".mp3", 1000, Mp3() with { DurationMs = ms }, false).HasFlag(IssueFlags.ZeroDuration));

    [Fact]
    public void AFileWhoseTagsCouldNotBeRead_IsUnreadable_AndNothingElseIsGuessed() =>
        Assert.Equal(IssueFlags.Unreadable, QualityRules.Evaluate(".mp3", 5000, new TrackMetadata(), true));

    [Fact]
    public void DamagedTagText_IsFlaggedAsMojibake() =>
        Assert.True(QualityRules.Evaluate(".mp3", SizeFor(320, 240_000), Mp3() with { Artist = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("Χάρις")) }, false).HasFlag(IssueFlags.Mojibake));

    [Fact]
    public void GreekTagText_IsNotMojibake() =>
        Assert.False(QualityRules.Evaluate(".mp3", SizeFor(320, 240_000), Mp3() with { Artist = "Χάρις Αλεξίου", Title = "Σ' αγαπώ" }, false).HasFlag(IssueFlags.Mojibake));

    [Fact]
    public void TagCompleteness_CountsSixThings()
    {
        Assert.Equal(0, QualityRules.TagCompleteness(new Track()));
        Assert.Equal(0, QualityRules.TagCompleteness(new Track { Title = "  ", Artist = "" }));
        Assert.Equal(6, QualityRules.TagCompleteness(new Track { Title = "t", Artist = "a", Album = "b", GenreRaw = "g", Year = 1999, ArtworkCacheKey = "x" }));
    }
}
