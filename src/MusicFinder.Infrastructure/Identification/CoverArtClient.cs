using System.Net;
using MusicFinder.Application.Identification;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Identification;

/// <summary>
/// Front covers from the Cover Art Archive (coverartarchive.org, which redirects to archive.org). Asked for only when the user
/// is looking at a release, and only while identification is on and covers are allowed. The 500-pixel version is used (plenty
/// for a library); the original is tried if that size does not exist. What comes back is accepted only if its first bytes say
/// JPEG or PNG and it is not larger than <see cref="CoverImageReader.MaxBytes"/>: the server's word is not trusted.
/// </summary>
public sealed class CoverArtClient : ICoverArtClient
{
    private readonly IdentificationHttp _http;
    private readonly ISettingsStore _settings;
    private readonly AppInfo _app;

    public CoverArtClient(IdentificationHttp http, ISettingsStore settings, AppInfo app)
    {
        _http = http;
        _settings = settings;
        _app = app;
    }

    public async Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken ct)
    {
        var s = _settings.Load();
        if (!s.IdentificationEnabled || s.IdentificationConsentUtc is null || !s.UseCoverArtArchive) return null;
        if (!MusicBrainzClient.IsId(releaseId)) return null;

        var id = releaseId.ToLowerInvariant();
        return await FetchAsync($"release/{id}/front-500", ct) ?? await FetchAsync($"release/{id}/front", ct);
    }

    private async Task<CoverImage?> FetchAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        // Only the program's name: no contact address is shared with this service.
        request.Headers.UserAgent.ParseAdd($"{Safe(_app.Name)}/{Safe(_app.Version)}");
        using var response = await _http.CoverArt.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;   // no cover chosen for this release
        if (response.Content.Headers.ContentLength is > CoverImageReader.MaxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > CoverImageReader.MaxBytes) return null;   // a body larger than it promised
        }
        return CoverImageReader.TryRead(buffer.ToArray());
    }

    private static string Safe(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
}
