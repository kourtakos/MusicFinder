namespace MusicFinder.Domain.Models;

/// <summary>Raw artwork bytes as found in a file or folder. <see cref="Hash"/> is lowercase hex SHA-1 of <see cref="Bytes"/>.</summary>
public sealed record ArtworkBlob(byte[] Bytes, string? MimeType, string Hash);
