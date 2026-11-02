using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Metadata;

/// <summary>Read-only TagLibSharp adapter. Throws on corrupt/unsupported files; callers record the error and continue.</summary>
public sealed class TagLibMetadataReader : IMetadataReader
{
    public Task<TrackMetadata> ReadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Read(path));
    }

    /// <summary>
    /// TagLib chooses the format from the file extension. When a copy has a different extension (the writer's
    /// temporary ".tmp" file) the original's format is passed explicitly via a "taglib/ext" mime type.
    /// </summary>
    internal static string MimeTypeFor(string originalPath) =>
        "taglib/" + Path.GetExtension(originalPath).TrimStart('.').ToLowerInvariant();

    internal static TrackMetadata Read(string path, string? mimeType = null)
    {
        using var file = mimeType is null
            ? TagLib.File.Create(path, TagLib.ReadStyle.Average)
            : TagLib.File.Create(path, mimeType, TagLib.ReadStyle.Average);
        var tag = file.Tag;
        var props = file.Properties;

        return new TrackMetadata
        {
            Title = Clean(tag.Title),
            Artist = Clean(tag.JoinedPerformers),
            AlbumArtist = Clean(tag.FirstAlbumArtist),
            Album = Clean(tag.Album),
            GenreRaw = Clean(tag.JoinedGenres),
            Year = tag.Year > 0 ? (int)tag.Year : null,
            TrackNumber = tag.Track > 0 ? (int)tag.Track : null,
            Bpm = tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null,
            MusicalKey = Clean(tag.InitialKey),
            MusicBrainzRecordingId = Clean(tag.MusicBrainzTrackId),
            ArtworkHash = Artwork.ArtworkExtractor.PickFront(tag.Pictures)?.Hash,
            DurationMs = props is { Duration.TotalMilliseconds: > 0 } ? (long)props.Duration.TotalMilliseconds : null,
            BitrateKbps = props is { AudioBitrate: > 0 } ? props.AudioBitrate : null,
            SampleRateHz = props is { AudioSampleRate: > 0 } ? props.AudioSampleRate : null
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
