using System.Net;
using System.Text.RegularExpressions;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.Infrastructure.Identification;

/// <summary>
/// MusicBrainz web service (https://musicbrainz.org/ws/2). Lookups are cached by id for 30 days; searches are cached by the
/// caller (they depend on the user's tags). The User-Agent carries the contact the user gave, as MusicBrainz requires.
/// </summary>
public sealed partial class MusicBrainzClient : IMusicBrainzClient
{
    public static readonly TimeSpan EntityLifetime = TimeSpan.FromDays(30);
    private const string RecordingInc = "artist-credits+releases+release-groups+isrcs+genres";
    private const string ReleaseInc = "artist-credits+recordings+release-groups+labels+media";

    private readonly IdentificationHttp _http;
    private readonly ISettingsStore _settings;
    private readonly AppInfo _app;
    private readonly IIdentificationCache _cache;
    private readonly TimeProvider _time;

    public MusicBrainzClient(IdentificationHttp http, ISettingsStore settings, AppInfo app, IIdentificationCache cache, TimeProvider time)
    {
        _http = http;
        _settings = settings;
        _app = app;
        _cache = cache;
        _time = time;
    }

    public async Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken ct)
    {
        if (!IsId(recordingId)) return null;
        var json = await GetEntityJsonAsync("recording", recordingId, $"recording/{recordingId}?inc={RecordingInc}&fmt=json", ct);
        return json is null ? null : MusicBrainzJson.ParseRecording(json);
    }

    public async Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken ct)
    {
        if (!IsId(releaseId)) return null;
        var json = await GetEntityJsonAsync("release", releaseId, $"release/{releaseId}?inc={ReleaseInc}&fmt=json", ct);
        return json is null ? null : MusicBrainzJson.ParseRelease(json);
    }

    public async Task<IReadOnlyList<MbSearchHit>> SearchRecordingsAsync(string luceneQuery, int limit, CancellationToken ct)
    {
        var path = $"recording?query={Uri.EscapeDataString(luceneQuery)}&limit={Math.Clamp(limit, 1, 25)}&fmt=json";
        using var response = await SendAsync(path, ct);
        if (!response.IsSuccessStatusCode) return [];
        return MusicBrainzJson.ParseSearch(await response.Content.ReadAsStringAsync(ct));
    }

    // ===== helpers =====

    private async Task<string?> GetEntityJsonAsync(string type, string id, string path, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cached = await _cache.GetEntityAsync(type, id, now, ct);
        if (cached is not null) return cached;

        using var response = await SendAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        await _cache.PutEntityAsync(type, id, json, now, now + EntityLifetime, ct);
        return json;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        var ua = UserAgentBuilder.Build(_app, _settings.Load().MusicBrainzContact)
                 ?? throw new InvalidOperationException("MusicBrainz requests need a contact address in the settings.");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.UserAgent.ParseAdd(ua);
        return await _http.MusicBrainz.SendAsync(request, ct);
    }

    /// <summary>Only a UUID is ever put into a URL path, so an id from a tag file cannot reshape the request.</summary>
    public static bool IsId(string value) => IdPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex IdPattern();
}
