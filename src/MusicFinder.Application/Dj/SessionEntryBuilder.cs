using System.Text.Json;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Dj;

/// <summary>
/// Turns what was stored about an export into what the Session tab shows: a status and a plain-language reason.
/// The wording separates what was observed (a drop happened, the user typed something) from what is inferred (a possible play).
/// </summary>
public static class SessionEntryBuilder
{
    public static SessionEntry Build(SessionEntryRow row)
    {
        var export = Parse(row.ExportEvidenceJson);
        var effect = Text(export, "effect") ?? "Unknown";
        var target = Text(export, "foreground");
        var windowSeconds = Number(export, "windowSeconds") ?? 20;

        var status = row.PlayStatus switch
        {
            "Confirmed" => SessionPlayStatus.Confirmed,
            "Possible" => SessionPlayStatus.Possible,
            _ => SessionPlayStatus.Exported
        };

        return new SessionEntry(row.AnchorEventId, row.TrackId, row.Title, row.Artist, row.ExportedUtc, status,
            Evidence(row, status, windowSeconds), effect, target);
    }

    private static string Evidence(SessionEntryRow row, SessionPlayStatus status, int windowSeconds)
    {
        switch (status)
        {
            case SessionPlayStatus.Confirmed:
                return row.PlaySource switch
                {
                    PlaySources.User => "Marked as played by you.",
                    var s => $"Confirmed by {s}'s own history."
                };

            case SessionPlayStatus.Possible:
                return $"Dropped, then no search, filter, folder or selection change for {windowSeconds} s. " +
                       "That is weak evidence, not proof that it was played.";
        }

        var resolution = Parse(row.ResolutionEvidenceJson);
        switch (row.ResolutionEventType)
        {
            case nameof(TrackEventType.ManualMarkNotPlayed):
                return "Marked as not played by you.";

            case nameof(TrackEventType.ActivityInterrupted):
                var seconds = Number(resolution, "atSeconds");
                var when = seconds is { } s ? $" {s} s after the drop" : " after the drop";
                return Text(resolution, "activity") switch
                {
                    nameof(UserActivity.Search) => $"Search or filters changed{when}, so it stays Exported.",
                    nameof(UserActivity.Scope) => $"The folder changed{when}, so it stays Exported.",
                    nameof(UserActivity.Selection) => $"A different track was selected{when}, so it stays Exported.",
                    nameof(UserActivity.SessionStopped) => $"The session was stopped{when}, so it stays Exported.",
                    nameof(UserActivity.AppClosed) => "MusicFinder was closed before the quiet period ended, so it stays Exported.",
                    _ => $"Activity{when}, so it stays Exported."
                };

            default:
                return $"Dropped. Waiting for {windowSeconds} s without activity.";
        }
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; } // evidence is informational; a damaged row must not break the tab
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Number(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;
}
