using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

public class CoverArtClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Release = "3763af37-0dea-41d2-a54e-1ce6ffde9a5e";

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeHttpHandler Net { get; }
        public IdentificationHttp Http { get; }
        public MusicBrainzClientTests.FakeSettings Settings { get; } = new();
        public CoverArtClient Client { get; }

        public Rig(Action<AppSettings>? configure = null)
        {
            Settings.Current = new AppSettings
            {
                IdentificationEnabled = true, IdentificationConsentUtc = Start.UtcDateTime, MusicBrainzContact = "dj@example.com", UseCoverArtArchive = true
            };
            configure?.Invoke(Settings.Current);
            Net = new FakeHttpHandler(() => Time);
            Http = new IdentificationHttp(new RateLimiter(Time), Time, () => Net);
            Client = new CoverArtClient(Http, Settings, new AppInfo("MusicFinder", "1.0.0"));
        }

        public async Task<T> RunAsync<T>(Task<T> call)
        {
            for (var i = 0; i < 2000 && !call.IsCompleted; i++)
            {
                Time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(1);
            }
            return await call;
        }

        public void Dispose() => Http.Dispose();

        public Rig ImageThen(byte[] bytes, string contentType = "image/png")
        {
            Net.Respond(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                return r;
            });
            return this;
        }
    }

    private static byte[] Png() => Tests_Png(500, 500);
    private static byte[] Tests_Png(int w, int h) => UnitCover.Png(w, h);

    [Fact]
    public async Task TheFiveHundredPixelFront_IsFetched_AndReadFromItsBytes()
    {
        using var rig = new Rig().ImageThen(Png());

        var cover = await rig.RunAsync(rig.Client.GetFrontAsync(Release.ToUpperInvariant(), default));

        Assert.Equal(("image/png", 500, 500), (cover!.MimeType, cover.Width, cover.Height));
        var request = Assert.Single(rig.Net.Requests);
        Assert.Equal($"https://coverartarchive.org/release/{Release}/front-500", request.Uri.ToString());
        Assert.DoesNotContain("@", request.Headers["User-Agent"]);                       // the contact goes to MusicBrainz only
        Assert.StartsWith("MusicFinder/1.0.0", request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task WhenThatSizeDoesNotExist_TheOriginalIsTried()
    {
        using var rig = new Rig();
        rig.Net.Then(HttpStatusCode.NotFound).ThenImage(Png());

        var cover = await rig.RunAsync(rig.Client.GetFrontAsync(Release, default));

        Assert.NotNull(cover);
        Assert.Equal([$"/release/{Release}/front-500", $"/release/{Release}/front"], rig.Net.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ARelease_WithNoCover_IsNull()
    {
        using var rig = new Rig();
        rig.Net.Always(HttpStatusCode.NotFound, """{"error":"Not Found"}""");

        Assert.Null(await rig.RunAsync(rig.Client.GetFrontAsync(Release, default)));
        Assert.Equal(2, rig.Net.Requests.Count);                                         // the thumbnail, then the original
    }

    [Theory]
    [InlineData("image/jpeg", "<html><body>Rate limit page</body></html> padding padding padding")]   // a web page called an image
    [InlineData("text/html", "GIF89a............................................")]
    public async Task WhatIsNotReallyAJpegOrPng_IsRefused_WhateverTheServerSays(string contentType, string body)
    {
        using var rig = new Rig().ImageThen(System.Text.Encoding.ASCII.GetBytes(body), contentType);

        Assert.Null(await rig.RunAsync(rig.Client.GetFrontAsync(Release, default)));
    }

    [Fact]
    public async Task AnImageTooLargeToBeACover_IsRefused_AfterTheHeaderOrWhileReading()
    {
        using var rig = new Rig();
        var huge = new byte[CoverImageReaderLimit + 10];
        Png().CopyTo(huge, 0);
        rig.Net.Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(huge) });

        Assert.Null(await rig.RunAsync(rig.Client.GetFrontAsync(Release, default)));
    }

    private const int CoverImageReaderLimit = MusicFinder.Application.Tags.CoverImageReader.MaxBytes;

    [Theory]
    [InlineData(false, true, true)]    // off
    [InlineData(true, false, true)]    // never agreed
    [InlineData(true, true, false)]    // covers switched off
    public async Task WithoutAgreement_NothingIsSent(bool enabled, bool consented, bool covers)
    {
        using var rig = new Rig(s =>
        {
            s.IdentificationEnabled = enabled;
            s.IdentificationConsentUtc = consented ? Start.UtcDateTime : null;
            s.UseCoverArtArchive = covers;
        }).ImageThen(Png());

        Assert.Null(await rig.Client.GetFrontAsync(Release, default));
        Assert.Empty(rig.Net.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("3763af37-0dea-41d2-a54e-1ce6ffde9a5e/../x")]
    public async Task AnIdThatIsNotAUuid_NeverReachesTheNetwork(string id)
    {
        using var rig = new Rig().ImageThen(Png());

        Assert.Null(await rig.Client.GetFrontAsync(id, default));
        Assert.Empty(rig.Net.Requests);
    }

    [Fact]
    public async Task NoNetwork_SurfacesAsAnOutage_NotAsNoCover()
    {
        using var rig = new Rig();
        rig.Net.Respond(_ => throw new HttpRequestException("offline"));

        var ex = await Assert.ThrowsAsync<IdentificationHttpException>(() => rig.RunAsync(rig.Client.GetFrontAsync(Release, default)));

        Assert.Equal(IdentificationFailure.Offline, ex.Failure);
    }
}

internal static class UnitCover
{
    /// <summary>A PNG header of the given size (enough for the reader; shared with the unit tests' idea of a fake image).</summary>
    public static byte[] Png(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange([(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h]);
        b.AddRange([8, 2, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        return [.. b];
    }
}
