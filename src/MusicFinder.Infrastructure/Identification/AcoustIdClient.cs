using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.Infrastructure.Identification;

/// <summary>
/// AcoustID's lookup (https://api.acoustid.org/v2/lookup). The request is a gzip-compressed POST, as the service recommends for
/// long fingerprints, so the application key and the fingerprint travel in the body and never appear in a URL or a log line.
/// Free for non-commercial use only.
/// </summary>
public sealed class AcoustIdClient : IAcoustIdClient
{
    private readonly IdentificationHttp _http;
    private readonly ISettingsStore _settings;
    private readonly AppInfo _app;

    public AcoustIdClient(IdentificationHttp http, ISettingsStore settings, AppInfo app)
    {
        _http = http;
        _settings = settings;
        _app = app;
    }

    public async Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(string apiKey, string fingerprint, int durationSeconds, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client"] = apiKey,
            ["duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fingerprint"] = fingerprint,
            ["meta"] = "recordingids",
            ["format"] = "json"
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "lookup") { Content = GzipForm(form) };
        // AcoustID asks for no particular agent, but an honest one costs nothing. The contact is not sent: only MusicBrainz needs it.
        request.Headers.UserAgent.ParseAdd($"{Safe(_app.Name)}/{Safe(_app.Version)}");

        using var response = await _http.AcoustId.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode is 400 or 401 or 403)
        {
            var error = AcoustIdJson.ReadError(body);
            if (error is not null)
                throw new IdentificationHttpException(IdentificationFailure.ServerError, (int)response.StatusCode, "AcoustID: " + error);
        }
        if (!response.IsSuccessStatusCode)
            throw new IdentificationHttpException(IdentificationFailure.ServerError, (int)response.StatusCode,
                $"AcoustID answered with an error ({(int)response.StatusCode}).");
        return AcoustIdJson.ParseMatches(body);
    }

    private static string Safe(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());

    private static ByteArrayContent GzipForm(IReadOnlyDictionary<string, string> form)
    {
        var text = string.Join("&", form.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }
}

public static class AcoustIdJson
{
    /// <summary>Matches with at least one MusicBrainz recording, best score first. Anything unusable gives an empty list.</summary>
    public static IReadOnlyList<AcoustIdMatch> ParseMatches(string json)
    {
        using var doc = Parse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];

        var list = new List<AcoustIdMatch>();
        foreach (var r in results.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var score = r.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            var ids = new List<string>();
            if (r.TryGetProperty("recordings", out var recs) && recs.ValueKind == JsonValueKind.Array)
                foreach (var rec in recs.EnumerateArray())
                    if (rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.String
                        && rid.GetString() is { } text && MusicBrainzClient.IsId(text) && !ids.Contains(text.ToLowerInvariant()))
                        ids.Add(text.ToLowerInvariant());
            if (id is not null && ids.Count > 0) list.Add(new AcoustIdMatch(id, Math.Clamp(score, 0, 1), ids));
        }
        return list.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>The message of a <c>{"status":"error","error":{"message":...}}</c> answer; null when the answer is not an error.</summary>
    public static string? ReadError(string json)
    {
        using var doc = Parse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var status = doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (!string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)) return null;
        if (doc.RootElement.TryGetProperty("error", out var e))
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
        }
        return "the service reported an error";
    }

    private static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }
}
