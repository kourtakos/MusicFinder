using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Dj;

/// <summary>What was read from one VirtualDJ history file. <see cref="HasIncompleteTail"/> means the last entry was still being written; the next import picks it up.</summary>
public sealed record HistoryParseResult(IReadOnlyList<ImportedPlayEvent> Events, IReadOnlyList<string> Warnings, bool HasIncompleteTail);

/// <summary>
/// Reads VirtualDJ's daily history file (<c>History\yyyy-MM-dd.m3u</c>). The format below was observed in VirtualDJ 2026, not taken from documentation:
/// an optional <c>#EXTM3U</c> line, then per play one <c>#EXTVDJ:&lt;tag&gt;value&lt;/tag&gt;...</c> line followed by the full file path. <c>lastplaytime</c> is
/// Unix seconds in UTC and is the authoritative time; <c>time</c> is local HH:mm with the date in the file name. The file grows while a set is played,
/// so a trailing entry may be half written. Pure: bytes in, events out.
/// </summary>
public static partial class VirtualDjHistoryParser
{
    private const string HeaderPrefix = "#EXTVDJ:";
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"<([A-Za-z]+)>(.*?)</\1>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    public static HistoryParseResult Parse(byte[] bytes, DateOnly fileDate, TimeZoneInfo localZone)
    {
        var warnings = new List<string>();
        var text = Decode(bytes, warnings);
        var events = new List<ImportedPlayEvent>();
        var tail = false;

        var endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (endsWithNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);

        string? header = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Count - 1;
            if (line.Trim().Length == 0) continue;

            if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                if (header is not null) warnings.Add("An entry without a file path was skipped.");
                header = line[HeaderPrefix.Length..];
                continue;
            }
            if (line.StartsWith('#')) continue;                        // #EXTM3U and any comment we do not know

            if (header is null)
            {
                warnings.Add($"Unexpected line {i + 1} was skipped.");
                continue;
            }
            if (isLast && !endsWithNewline)
            {
                tail = true;                                            // the path may be cut mid-write; read it next time
                header = null;
                break;
            }

            var entry = BuildEvent(header, line.Trim(), fileDate, localZone, warnings);
            if (entry is not null) events.Add(entry);
            header = null;
        }

        if (header is not null) tail = true;
        return new HistoryParseResult(events, warnings, tail);
    }

    /// <summary>Stable id of one play: the time VirtualDJ stamped it with, and a hash of the path (case and slash style do not matter).</summary>
    public static string EventKey(long lastPlayUnixSeconds, string path) => $"vdj:{lastPlayUnixSeconds}:{PathHash(path)}";

    private static string PathHash(string path)
    {
        var normalized = path.Trim().Replace('/', '\\').ToUpperInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    private static ImportedPlayEvent? BuildEvent(string header, string path, DateOnly fileDate, TimeZoneInfo zone, List<string> warnings)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TagPattern().Matches(header)) tags[m.Groups[1].Value] = Unescape(m.Groups[2].Value);

        TimeOnly? localTime = null;
        if (tags.TryGetValue("time", out var timeText) &&
            DateTime.TryParseExact(timeText.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
            localTime = TimeOnly.FromDateTime(parsedTime);

        DateTime playedUtc;
        string key;
        if (tags.TryGetValue("lastplaytime", out var stamp) && long.TryParse(stamp.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && unix > 0)
        {
            playedUtc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            key = EventKey(unix, path);
            if (localTime is { } shown)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(playedUtc, zone);
                var gap = Math.Abs((local.TimeOfDay - shown.ToTimeSpan()).TotalMinutes);
                gap = Math.Min(gap, 1440 - gap);                        // 23:59 and 00:01 are two minutes apart
                if (gap > TimeTolerance.TotalMinutes)
                    warnings.Add($"The play time of '{path}' differs from its shown time ({shown:HH\\:mm}); the stamped time was used.");
            }
        }
        else if (localTime is { } fallback)
        {
            var local = fileDate.ToDateTime(fallback, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                warnings.Add($"The time {fallback:HH\\:mm} of '{path}' does not exist on {fileDate:yyyy-MM-dd} (clock change); the entry was skipped.");
                return null;
            }
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);   // the earlier of two passes
            playedUtc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
            key = $"vdj:{fileDate:yyyy-MM-dd}T{fallback:HH\\:mm}:{PathHash(path)}";
        }
        else
        {
            warnings.Add($"The entry for '{path}' has no usable play time and was skipped.");
            return null;
        }

        return new ImportedPlayEvent(
            PlaySources.VirtualDj, key, playedUtc, path,
            NullIfEmpty(tags.GetValueOrDefault("artist")), NullIfEmpty(tags.GetValueOrDefault("title")), NullIfEmpty(tags.GetValueOrDefault("remix")),
            long.TryParse(tags.GetValueOrDefault("filesize")?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null,
            double.TryParse(tags.GetValueOrDefault("songlength")?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The tags look like XML; entities are decoded in case VirtualDJ escapes them. Text without entities is unchanged.</summary>
    private static string Unescape(string value) =>
        value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    private static string Decode(byte[] bytes, List<string> warnings)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            warnings.Add("The file is not valid UTF-8; it was read as Windows-1253 (Greek).");
            return Encoding.GetEncoding(1253).GetString(bytes, start, bytes.Length - start);
        }
    }
}
