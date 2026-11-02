using System.Text.Json;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Identification;

/// <summary>
/// Reads MusicBrainz's JSON (<c>fmt=json</c>). Every field is optional: a missing or oddly shaped value becomes null or empty,
/// never an exception, because the web service's output is not ours to control. Shapes were checked against live responses
/// for a recording search, a recording lookup and a release lookup.
/// </summary>
public static class MusicBrainzJson
{
    public static MbRecording? ParseRecording(string json)
    {
        using var doc = Parse(json);
        return doc is null ? null : ReadRecording(doc.RootElement);
    }

    public static MbRelease? ParseRelease(string json)
    {
        using var doc = Parse(json);
        return doc is null ? null : ReadRelease(doc.RootElement);
    }

    public static IReadOnlyList<MbSearchHit> ParseSearch(string json)
    {
        using var doc = Parse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("recordings", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var hits = new List<MbSearchHit>();
        foreach (var item in items.EnumerateArray())
        {
            var recording = ReadRecording(item);
            if (recording is null) continue;
            hits.Add(new MbSearchHit(recording.Id, Int(item, "score") ?? 0, recording));
        }
        return hits;
    }

    // ===== readers =====

    private static MbRecording? ReadRecording(JsonElement e)
    {
        var id = Str(e, "id");
        if (e.ValueKind != JsonValueKind.Object || id is null) return null;

        var releases = new List<ReleaseSummary>();
        if (e.TryGetProperty("releases", out var rel) && rel.ValueKind == JsonValueKind.Array)
            foreach (var r in rel.EnumerateArray())
            {
                var rid = Str(r, "id");
                if (rid is null) continue;
                var group = r.TryGetProperty("release-group", out var g) && g.ValueKind == JsonValueKind.Object ? g : (JsonElement?)null;
                releases.Add(new ReleaseSummary(rid, Str(r, "title") ?? "(untitled)", Str(r, "date"), Str(r, "country"), Str(r, "status"),
                    group is { } gg ? Str(gg, "id") : null, group is { } gp ? Str(gp, "primary-type") : null,
                    group is { } gs ? Strings(gs, "secondary-types") : []));
            }

        // Genres are community tags; the most voted come first.
        var genres = new List<(string Name, int Count)>();
        if (e.TryGetProperty("genres", out var gen) && gen.ValueKind == JsonValueKind.Array)
            foreach (var x in gen.EnumerateArray())
                if (Str(x, "name") is { } name) genres.Add((name, Int(x, "count") ?? 0));

        return new MbRecording(id.ToLowerInvariant(), Str(e, "title") ?? "(untitled)", Credit(e), Long(e, "length"),
            releases, genres.OrderByDescending(x => x.Count).Select(x => x.Name).ToList(), Strings(e, "isrcs"));
    }

    private static MbRelease? ReadRelease(JsonElement e)
    {
        var id = Str(e, "id");
        if (e.ValueKind != JsonValueKind.Object || id is null) return null;

        var labels = new List<string>();
        if (e.TryGetProperty("label-info", out var li) && li.ValueKind == JsonValueKind.Array)
            foreach (var x in li.EnumerateArray())
                if (x.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object && Str(l, "name") is { } name && !labels.Contains(name))
                    labels.Add(name);

        var tracks = new List<ReleaseTrack>();
        if (e.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            foreach (var m in media.EnumerateArray())
            {
                var disc = Int(m, "position") ?? 1;
                var count = Int(m, "track-count") ?? 0;
                if (!m.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in list.EnumerateArray())
                {
                    var recording = t.TryGetProperty("recording", out var r) && r.ValueKind == JsonValueKind.Object ? Str(r, "id") : null;
                    if (recording is null) continue;
                    tracks.Add(new ReleaseTrack(recording.ToLowerInvariant(), disc, Int(t, "position") ?? 0, count,
                        Str(t, "title") ?? "(untitled)", Credit(t)));
                }
            }

        return new MbRelease(id.ToLowerInvariant(), Str(e, "title") ?? "(untitled)", Str(e, "date"), Str(e, "country"), Credit(e), labels, tracks);
    }

    /// <summary>"Artist A feat. Artist B": each credited name followed by its join phrase, as MusicBrainz displays it.</summary>
    private static string Credit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
        var text = new System.Text.StringBuilder();
        foreach (var c in credit.EnumerateArray())
        {
            var name = Str(c, "name")
                       ?? (c.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "name") : null);
            text.Append(name).Append(Str(c, "joinphrase"));
        }
        return text.ToString().Trim();
    }

    // ===== tolerant primitives =====

    private static JsonDocument? Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
