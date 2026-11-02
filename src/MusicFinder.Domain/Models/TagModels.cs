namespace MusicFinder.Domain.Models;

/// <summary>The tag fields the editor can change. Anything else in the file (comments, lyrics, cover, ...) is left alone.</summary>
public enum TagField { Title, Artist, AlbumArtist, Album, Genre, Year, TrackNumber, Bpm, Key, MusicBrainzRecordingId, Cover }

/// <summary>
/// The fields to change, as the text the user typed. A field that is absent is not touched; a null or blank value clears it.
/// <paramref name="Cover"/> adds a front cover; when the file already has a cover it is replaced only if
/// <paramref name="ReplaceExistingCover"/> is set (otherwise the file's own cover is left alone).
/// </summary>
public sealed record TagChangeSet(IReadOnlyDictionary<TagField, string?> Changes, CoverImage? Cover = null, bool ReplaceExistingCover = false)
{
    public bool IsEmpty => Changes.Count == 0 && Cover is null;
}

/// <param name="KeepBackup">Copy the original file to <paramref name="BackupDirectory"/> before replacing it.</param>
/// <param name="BackupDirectory">Must be outside the music library; required when <paramref name="KeepBackup"/> is true.</param>
public sealed record TagWriteOptions(bool KeepBackup = false, string? BackupDirectory = null);

/// <summary>
/// Outcome of writing one file. On failure the original file is intact and <see cref="Error"/> says why.
/// On success <see cref="After"/> is what was read back from the written file, with its new size and time.
/// </summary>
public sealed record TagWriteResult(
    string Path, bool Success, string? Error, TrackMetadata? After, long? NewSize, DateTime? NewModifiedUtc, string? BackupPath = null);
