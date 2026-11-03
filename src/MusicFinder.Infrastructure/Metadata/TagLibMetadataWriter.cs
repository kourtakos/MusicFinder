using System.Security.Cryptography;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Metadata;

/// <summary>
/// Safe tag writer. The original is never modified in place: changes are applied to a temporary copy next to it,
/// the copy is read back and verified (requested fields changed, cover art identical), and only then does it
/// replace the original. Any failure before the swap deletes the copy and leaves the original byte-for-byte intact.
/// </summary>
public sealed class TagLibMetadataWriter : IMetadataWriter
{
    private const int LockRetries = 3;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly TagLibMetadataReader _reader = new();
    private readonly IPreviewPlayer? _preview;

    static TagLibMetadataWriter()
    {
        // ID3v2.3 with UTF-16 is what Explorer, VirtualDJ and Traktor read most reliably; new tags use it.
        // Existing ID3v2.4 tags are left in v2.4 (not force-converted). Genre names are written as text, not "(17)" codes.
        TagLib.Id3v2.Tag.DefaultVersion = 3;
        TagLib.Id3v2.Tag.ForceDefaultVersion = false;
        TagLib.Id3v2.Tag.UseNumericGenres = false;
    }

    /// <param name="preview">When this file is being previewed, the preview lets go of it before the write (the write probes for exclusive access).</param>
    public TagLibMetadataWriter(IPreviewPlayer? preview = null) => _preview = preview;

    public IReadOnlyList<string> Validate(TagChangeSet changes) => TagChangeValidator.Validate(changes);

    public async Task<TagWriteResult> WriteAsync(string path, TagChangeSet changes, TagWriteOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var problems = TagChangeValidator.Validate(changes);
        if (problems.Count > 0) return Fail(path, string.Join(" ", problems));
        if (changes.IsEmpty) return Fail(path, "There is nothing to write.");
        if (options.KeepBackup && string.IsNullOrWhiteSpace(options.BackupDirectory))
            return Fail(path, "A backup was requested but no backup folder is configured.");

        if (!File.Exists(path)) return Fail(path, "The file no longer exists.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            return Fail(path, "The file is read-only. Clear the read-only attribute in Explorer if you want to edit it.");

        _preview?.ReleaseIfPlaying(path);

        string? temp = null;
        string? backup = null;
        try
        {
            TrackMetadata before;
            try { before = await _reader.ReadAsync(path, ct); }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                return Fail(path, "The file could not be read as audio, so its tags were not changed.");
            }

            if (!await WaitUntilUnlockedAsync(path, ct))
                return Fail(path, "The file is in use by another program (for example a DJ application). Close it and try again.");

            if (options.KeepBackup)
            {
                backup = BackupPathFor(path, options.BackupDirectory!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(path, backup, overwrite: true);
            }

            temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.mf-{Guid.NewGuid():N}.tmp");
            File.Copy(path, temp);
            var mime = TagLibMetadataReader.MimeTypeFor(path);
            ApplyChanges(temp, mime, changes);

            // Read the copy back and prove it is what we meant to write before it gets anywhere near the original.
            var after = TagLibMetadataReader.Read(temp, mime);
            var mismatch = Verify(changes, before, after);
            if (mismatch is not null)
            {
                DeleteQuietly(temp);
                return Fail(path, mismatch);
            }

            ct.ThrowIfCancellationRequested(); // last chance to back out; after the swap the write is committed
            Swap(temp, path);
            temp = null;

            var info = new FileInfo(path);
            return new TagWriteResult(path, true, null, after, info.Length, info.LastWriteTimeUtc, backup);
        }
        catch (OperationCanceledException)
        {
            if (temp is not null) DeleteQuietly(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException or InvalidOperationException or NotSupportedException)
        {
            if (temp is not null) DeleteQuietly(temp);
            return Fail(path, ex is UnauthorizedAccessException
                ? "Access to the file was denied."
                : "The tags could not be written: " + ex.Message);
        }
    }

    // ===== helpers =====

    private static void ApplyChanges(string file, string mime, TagChangeSet changes)
    {
        using var f = TagLib.File.Create(file, mime, TagLib.ReadStyle.Average);
        var tag = f.Tag;

        foreach (var (field, raw) in changes.Changes)
        {
            var value = TagChangeValidator.Canonicalize(field, raw, out _); // already validated
            switch (field)
            {
                case TagField.Title: tag.Title = value; break;
                case TagField.Artist: tag.Performers = value is null ? [] : [value]; break;
                case TagField.AlbumArtist: tag.AlbumArtists = value is null ? [] : [value]; break;
                case TagField.Album: tag.Album = value; break;
                case TagField.Genre: tag.Genres = value is null ? [] : TagChangeValidator.SplitGenres(value).ToArray(); break;
                case TagField.Year: tag.Year = value is null ? 0u : uint.Parse(value); break;
                case TagField.TrackNumber: tag.Track = value is null ? 0u : uint.Parse(value); break;
                case TagField.Bpm: tag.BeatsPerMinute = value is null ? 0u : uint.Parse(value); break;
                case TagField.Key: tag.InitialKey = value; break;
                case TagField.MusicBrainzRecordingId: tag.MusicBrainzTrackId = value; break;
            }
        }
        if (changes.Cover is { } cover)
        {
            // Replace the front cover (or the first picture when none is marked front) and keep every other picture.
            var existing = tag.Pictures.ToList();
            var old = existing.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? existing.FirstOrDefault();
            if (old is not null) existing.Remove(old);
            var picture = new TagLib.Picture(new TagLib.ByteVector(cover.Data)) { Type = TagLib.PictureType.FrontCover, MimeType = cover.MimeType, Description = "" };
            tag.Pictures = [picture, .. existing];
        }
        f.Save();
    }

    /// <summary>Null when the written copy matches the request and kept the cover; otherwise a reason.</summary>
    private static string? Verify(TagChangeSet changes, TrackMetadata before, TrackMetadata after)
    {
        foreach (var (field, raw) in changes.Changes)
        {
            var expected = TagChangeValidator.Canonicalize(field, raw, out _);
            var actual = field switch
            {
                TagField.Title => after.Title,
                TagField.Artist => after.Artist,
                TagField.AlbumArtist => after.AlbumArtist,
                TagField.Album => after.Album,
                TagField.Genre => after.GenreRaw,
                TagField.Year => after.Year?.ToString(),
                TagField.TrackNumber => after.TrackNumber?.ToString(),
                TagField.Bpm => after.Bpm is { } b ? Math.Round(b).ToString("0") : null,
                TagField.Key => after.MusicalKey,
                TagField.MusicBrainzRecordingId => after.MusicBrainzRecordingId,
                _ => null
            };
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                return $"Verification failed for {field}: expected '{expected}', found '{actual}'. The original file was not changed.";
        }

        if (changes.Cover is { } cover)
        {
            // A cover was requested: the picture read back must be exactly the bytes that were given.
            if (!string.Equals(after.ArtworkHash, cover.Hash, StringComparison.OrdinalIgnoreCase))
                return "Verification failed: the new cover was not stored as expected. The original file was not changed.";
            return null;
        }

        // Editing text must never cost the embedded cover.
        if (before.ArtworkHash != after.ArtworkHash)
            return "Verification failed: the embedded cover would have changed. The original file was not changed.";
        return null;
    }

    /// <summary>Atomic replace on NTFS; plain overwrite-move where File.Replace is unsupported (some network/removable volumes).</summary>
    private static void Swap(string temp, string path)
    {
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException && File.Exists(temp) && File.Exists(path))
        {
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Another program holding the file open would make the swap fail, so check first and retry briefly.</summary>
    private static async Task<bool> WaitUntilUnlockedAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < LockRetries; attempt++)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                if (attempt < LockRetries - 1) await Task.Delay(LockRetryDelay, ct);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private static string BackupPathFor(string path, string backupRoot)
    {
        var id = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..8].ToLowerInvariant();
        return Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), $"{id}-{Path.GetFileName(path)}");
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leftover hidden temp; harmless */ }
    }

    private static TagWriteResult Fail(string path, string error) => new(path, false, error, null, null, null);
}
