using System.Security.Cryptography;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Artwork;

/// <summary>TagLib-based artwork lookup. Returns raw bytes only; decoding is the UI layer's job.</summary>
public sealed class ArtworkExtractor : IArtworkSource
{
    private static readonly string[] FolderNames = ["cover", "folder"];
    private static readonly string[] FolderExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>Pictures larger than this are ignored: far beyond any real cover, and a guard against hostile tags.</summary>
    private const int MaxBytes = 25 * 1024 * 1024;

    public Task<ArtworkBlob?> GetEmbeddedAsync(string audioPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadEmbedded(audioPath));
    }

    public Task<ArtworkBlob?> GetFolderArtAsync(string audioPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var dir = Path.GetDirectoryName(audioPath);
            if (dir is null || !Directory.Exists(dir)) return Task.FromResult<ArtworkBlob?>(null);

            // Prefer "cover" over "folder"; compare names case-insensitively (Windows paths are, other volumes may not be).
            var files = Directory.EnumerateFiles(dir).ToList();
            foreach (var name in FolderNames)
                foreach (var ext in FolderExtensions)
                {
                    var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name + ext, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;
                    var info = new FileInfo(match);
                    if (info.Length is 0 or > MaxBytes) continue;
                    return Task.FromResult<ArtworkBlob?>(Blob(File.ReadAllBytes(match), MimeFor(ext)));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder simply has no folder art.
        }
        return Task.FromResult<ArtworkBlob?>(null);
    }

    private static ArtworkBlob? ReadEmbedded(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            return PickFront(file.Tag.Pictures);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The picture marked as front cover, otherwise the first one. Shared with the metadata reader.</summary>
    internal static ArtworkBlob? PickFront(TagLib.IPicture[] pictures)
    {
        var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures.FirstOrDefault();
        if (picture?.Data is not { Count: > 0 and <= MaxBytes } data) return null;
        return Blob(data.Data, picture.MimeType);
    }

    private static ArtworkBlob Blob(byte[] bytes, string? mime) =>
        new(bytes, mime, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());

    private static string MimeFor(string ext) => ext == ".png" ? "image/png" : "image/jpeg";
}
