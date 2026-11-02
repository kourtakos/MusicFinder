using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Tags;

/// <summary>
/// Recognizes a JPEG or PNG by its first bytes (never by what a server or file name claims) and reads its pixel size without
/// decoding it. Anything else, anything empty or huge, is refused: bytes from the internet are not embedded blindly.
/// </summary>
public static class CoverImageReader
{
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>The cover, or null when the bytes are not a plausible JPEG/PNG.</summary>
    public static CoverImage? TryRead(byte[]? data)
    {
        if (data is null || data.Length < 24 || data.Length > MaxBytes) return null;
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            // PNG: the IHDR chunk follows the signature; width and height are big-endian 32-bit values.
            if (data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') return null;
            var w = ReadBigEndian(data, 16);
            var h = ReadBigEndian(data, 20);
            return Plausible(w, h) ? new CoverImage(data, "image/png", w, h) : null;
        }
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            var size = JpegSize(data);
            return size is { } s && Plausible(s.Width, s.Height) ? new CoverImage(data, "image/jpeg", s.Width, s.Height) : null;
        }
        return null;
    }

    /// <summary>True when an already-built cover is what it says it is (used again before writing).</summary>
    public static bool IsValid(CoverImage cover)
    {
        var read = TryRead(cover.Data);
        return read is not null && string.Equals(read.MimeType, cover.MimeType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Plausible(int w, int h) => w is > 0 and <= 20000 && h is > 0 and <= 20000;

    private static int ReadBigEndian(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    /// <summary>Walks the JPEG segments to the first "start of frame" marker, which holds the height and width.</summary>
    private static (int Width, int Height)? JpegSize(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }                         // fill byte
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }   // markers without a length
            var length = (b[i + 2] << 8) | b[i + 3];
            if (length < 2) return null;
            var isFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isFrame)
            {
                var height = (b[i + 5] << 8) | b[i + 6];
                var width = (b[i + 7] << 8) | b[i + 8];
                return (width, height);
            }
            i += 2 + length;
        }
        return null;
    }
}
