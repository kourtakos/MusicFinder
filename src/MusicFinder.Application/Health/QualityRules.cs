using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Health;

/// <summary>
/// Quality hints derived from what the library already stores (no file is opened). Every flag is a suspicion to
/// review: a low bitrate or an odd size never proves where a recording came from, and the app never says it does.
/// Bump <see cref="Version"/> when a rule changes so the library recomputes (see Track.CurrentMetaVersion).
/// </summary>
public static class QualityRules
{
    public const int Version = 1;
    public const int LowBitrateKbps = 128;

    private static readonly HashSet<int> NormalSampleRates = [8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".aiff", ".aif" };

    public static bool IsLossless(string extension) => Lossless.Contains(extension);

    public static IssueFlags Evaluate(string extension, long fileSize, TrackMetadata meta, bool readFailed)
    {
        var flags = IssueFlags.None;
        if (readFailed) return IssueFlags.Unreadable;

        var lossless = IsLossless(extension);

        if (!lossless && meta.BitrateKbps is > 0 and < LowBitrateKbps) flags |= IssueFlags.LowBitrate;
        if (meta.SampleRateHz is > 0 && !NormalSampleRates.Contains(meta.SampleRateHz.Value)) flags |= IssueFlags.OddSampleRate;
        if (meta.DurationMs is null or <= 0) flags |= IssueFlags.ZeroDuration;
        if (CodecDisagrees(extension, meta.Codec)) flags |= IssueFlags.CodecMismatch;

        // A lossy file's size should be close to bitrate x duration (plus tags and cover). Far smaller: cut short;
        // far larger: unusual. Variable bitrate and big covers move it, so the margins are wide. Lossless is exempt.
        if (!lossless && meta is { BitrateKbps: > 0, DurationMs: > 0 } && fileSize > 0)
        {
            var expected = meta.BitrateKbps!.Value * 1000.0 / 8 * meta.DurationMs!.Value / 1000.0;
            if (expected > 50_000 && (fileSize < expected * 0.85 || fileSize > expected * 1.6)) flags |= IssueFlags.SizeMismatch;
        }

        if (Mojibake(meta)) flags |= IssueFlags.Mojibake;
        return flags;
    }

    public static bool Mojibake(TrackMetadata m) =>
        MojibakeDetector.IsDamaged(m.Title) || MojibakeDetector.IsDamaged(m.Artist) || MojibakeDetector.IsDamaged(m.Album)
        || MojibakeDetector.IsDamaged(m.AlbumArtist) || MojibakeDetector.IsDamaged(m.GenreRaw);

    private static bool CodecDisagrees(string extension, string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false; // unknown is not a mismatch
        var c = codec.ToLowerInvariant();
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => !c.Contains("mpeg") && !c.Contains("mp3"),
            ".flac" => !c.Contains("flac"),
            ".m4a" => !c.Contains("aac") && !c.Contains("alac") && !c.Contains("lossless") && !c.Contains("mpeg"),
            ".aac" => !c.Contains("aac"),
            ".ogg" => !c.Contains("vorbis") && !c.Contains("opus") && !c.Contains("flac"),
            ".wav" or ".aiff" or ".aif" => !c.Contains("pcm") && !c.Contains("wav") && !c.Contains("aiff") && !c.Contains("float"),
            _ => false
        };
    }

    /// <summary>How many of Title, Artist, Album, Genre, Year and a cover the track has (0-6).</summary>
    public static int TagCompleteness(Domain.Entities.Track t) =>
        (string.IsNullOrWhiteSpace(t.Title) ? 0 : 1) + (string.IsNullOrWhiteSpace(t.Artist) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(t.Album) ? 0 : 1) + (string.IsNullOrWhiteSpace(t.GenreRaw) ? 0 : 1) +
        (t.Year is null ? 0 : 1) + (string.IsNullOrEmpty(t.ArtworkCacheKey) ? 0 : 1);
}
