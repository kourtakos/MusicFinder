using MusicFinder.Application.Tags;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class CoverImageReaderTests
{
    /// <summary>A PNG header with the given size (enough for the reader; not a decodable picture).</summary>
    internal static byte[] Png(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange([(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h]);
        b.AddRange([8, 2, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        return [.. b];
    }

    /// <summary>A JPEG with a JFIF segment, a comment, and a baseline start-of-frame of the given size.</summary>
    internal static byte[] Jpeg(int w, int h, byte sof = 0xC0)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
        b.AddRange([0xFF, 0xFE, 0x00, 0x05, 1, 2, 3]);                                     // a comment segment to step over
        b.AddRange([0xFF, sof, 0x00, 0x11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(1200, 1190)]
    [InlineData(1, 1)]
    public void APng_IsRecognizedByItsSignature_AndItsSizeIsRead(int w, int h)
    {
        var cover = CoverImageReader.TryRead(Png(w, h))!;

        Assert.Equal(("image/png", w, h), (cover.MimeType, cover.Width, cover.Height));
    }

    [Theory]
    [InlineData(500, 500, 0xC0)]
    [InlineData(640, 480, 0xC2)]       // progressive
    [InlineData(1200, 1200, 0xC0)]
    public void AJpeg_IsRecognized_AndItsSizeIsReadPastOtherSegments(int w, int h, byte sof)
    {
        var cover = CoverImageReader.TryRead(Jpeg(w, h, sof))!;

        Assert.Equal(("image/jpeg", w, h), (cover.MimeType, cover.Width, cover.Height));
    }

    [Fact]
    public void TheTypeComesFromTheBytes_NotFromAnyName()
    {
        // These bytes are a PNG whatever anyone calls them.
        Assert.Equal("image/png", CoverImageReader.TryRead(Png(10, 10))!.MimeType);
    }

    [Theory]
    [MemberData(nameof(NotImages))]
    public void Anything_ThatIsNotAPlausibleJpegOrPng_IsRefused(byte[]? data) =>
        Assert.Null(CoverImageReader.TryRead(data));

    public static TheoryData<byte[]?> NotImages() => new()
    {
        null,
        Array.Empty<byte>(),
        new byte[] { 1, 2, 3 },
        System.Text.Encoding.ASCII.GetBytes("<html><body>Not found</body></html> padding padding"),
        System.Text.Encoding.ASCII.GetBytes("GIF89a" + new string('x', 40)),
        Png(0, 10),                                   // an impossible size
        Png(100000, 100000),
        Jpeg(0, 0),
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },   // no frame header
    };

    [Fact]
    public void AnHugeFile_IsRefused()
    {
        var big = new byte[CoverImageReader.MaxBytes + 1];
        Png(500, 500).CopyTo(big, 0);

        Assert.Null(CoverImageReader.TryRead(big));
    }

    [Fact]
    public void ATruncatedJpeg_NeverThrows()
    {
        var full = Jpeg(500, 500);
        for (var cut = 0; cut < full.Length; cut++)
            _ = CoverImageReader.TryRead(full[..cut]);   // must simply answer, one way or the other
    }

    [Fact]
    public void ACoverWithTheWrongDeclaredType_IsNotValid()
    {
        var png = CoverImageReader.TryRead(Png(10, 10))!;

        Assert.True(CoverImageReader.IsValid(png));
        Assert.False(CoverImageReader.IsValid(png with { MimeType = "image/jpeg" }));
        Assert.False(CoverImageReader.IsValid(png with { Data = [1, 2, 3] }));
    }

    [Fact]
    public void TheHash_IsTheSha1ThatTheLibraryUsesAsAnArtworkKey()
    {
        var cover = new CoverImage([1, 2, 3], "image/png", null, null);

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), cover.Hash);
    }

    [Theory]
    [InlineData("image/jpeg", 500, 500, 84 * 1024, "500×500 JPEG, 84 KB")]
    [InlineData("image/png", 1200, 1200, 2 * 1024 * 1024, "1200×1200 PNG, 2.0 MB")]
    public void TheDescription_SaysWhatWillBeEmbedded(string mime, int w, int h, int bytes, string expected) =>
        Assert.Equal(expected, new CoverImage(new byte[bytes], mime, w, h).Description);
}
