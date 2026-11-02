using System.Globalization;
using MusicFinder.Domain.Enums;

namespace MusicFinder.Application.Search;

/// <summary>A free-text token or phrase, optionally scoped to a field (artist/title/album/genre).</summary>
public sealed record TextTerm(string? Field, string Value, bool IsPhrase);

public sealed class ParsedQuery
{
    public List<TextTerm> Terms { get; } = [];
    public double? BpmMin { get; set; }
    public double? BpmMax { get; set; }
    public int? YearMin { get; set; }
    public int? YearMax { get; set; }
    public string? Extension { get; set; }
    public string? MusicalKey { get; set; }
    /// <summary>From <c>played:never|session|possible|confirmed</c>.</summary>
    public PlayedFilter? Played { get; set; }
    public List<string> Missing { get; } = [];
    /// <summary>From <c>issue:mojibake|error|lowbitrate|samplerate|mismatch|size|duration|any</c>; each value is a bit of <see cref="Domain.Enums.IssueFlags"/> (any = any bit).</summary>
    public List<string> Issues { get; } = [];
    public double? BitrateMin { get; set; }
    public double? BitrateMax { get; set; }
    public string? Codec { get; set; }
    /// <summary>Non-blocking validation messages; parsing never throws.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Lexer/parser for the search grammar: tokens, "quoted phrases", and field:value filters
/// (artist, title, album, genre, ext, key, bpm, year, decade, missing, played, issue, bitrate, codec).
/// </summary>
public static class SearchQueryParser
{
    private static readonly HashSet<string> TextFields = new(StringComparer.OrdinalIgnoreCase) { "artist", "title", "album", "genre" };
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
        { "artist", "title", "album", "genre", "ext", "key", "bpm", "year", "decade", "missing", "played", "issue", "bitrate", "codec" };
    private static readonly HashSet<string> IssueNames = new(StringComparer.OrdinalIgnoreCase)
        { "any", "mojibake", "error", "lowbitrate", "samplerate", "mismatch", "size", "duration" };
    private static readonly HashSet<string> MissingFields = new(StringComparer.OrdinalIgnoreCase)
        { "title", "artist", "album", "genre", "year", "bpm", "key", "art", "cover" };

    public static ParsedQuery Parse(string? input)
    {
        var result = new ParsedQuery();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var s = input;
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }

            if (s[i] == '"')
            {
                var phrase = ReadQuoted(s, ref i);
                if (phrase.Length > 0) result.Terms.Add(new TextTerm(null, phrase, true));
                continue;
            }

            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '"') i++;
            var word = s[start..i];

            // field:"quoted value"
            if (i < s.Length && s[i] == '"' && word.EndsWith(':') && KnownFields.Contains(word[..^1]))
            {
                var field = word[..^1];
                var phrase = ReadQuoted(s, ref i);
                ApplyField(result, field, phrase, isPhrase: true);
                continue;
            }

            var colon = word.IndexOf(':');
            if (colon > 0 && KnownFields.Contains(word[..colon]))
            {
                ApplyField(result, word[..colon], word[(colon + 1)..], isPhrase: false);
                continue;
            }

            if (word.Length > 0) result.Terms.Add(new TextTerm(null, word, false));
        }
        return result;
    }

    private static string ReadQuoted(string s, ref int i)
    {
        i++; // opening quote
        var start = i;
        while (i < s.Length && s[i] != '"') i++;
        var value = s[start..i];
        if (i < s.Length) i++; // closing quote (an unterminated quote runs to the end)
        return value.Trim();
    }

    private static void ApplyField(ParsedQuery q, string field, string value, bool isPhrase)
    {
        field = field.ToLowerInvariant();
        if (value.Length == 0)
        {
            q.Warnings.Add($"'{field}:' needs a value.");
            return;
        }

        switch (field)
        {
            case "artist" or "title" or "album" or "genre":
                q.Terms.Add(new TextTerm(field, value, isPhrase));
                break;
            case "ext":
                q.Extension = "." + value.TrimStart('.').ToLowerInvariant();
                break;
            case "key":
                q.MusicalKey = value.ToUpperInvariant();
                break;
            case "bpm":
                if (TryParseRange(value, out var bmin, out var bmax)) { q.BpmMin = bmin; q.BpmMax = bmax; }
                else q.Warnings.Add($"Invalid bpm range '{value}' (use bpm:120 or bpm:115-125).");
                break;
            case "year":
                if (TryParseRange(value, out var ymin, out var ymax)) { q.YearMin = (int)ymin; q.YearMax = (int)ymax; }
                else q.Warnings.Add($"Invalid year range '{value}' (use year:1985 or year:1980-1989).");
                break;
            case "decade":
                if (TryParseDecade(value, out var d)) { q.YearMin = d; q.YearMax = d + 9; }
                else q.Warnings.Add($"Invalid decade '{value}' (use decade:80s or decade:1980s).");
                break;
            case "missing":
                if (MissingFields.Contains(value)) q.Missing.Add(value.ToLowerInvariant());
                else q.Warnings.Add($"Unknown missing:{value} (title, artist, album, genre, year, bpm, key, art).");
                break;
            case "issue":
                if (IssueNames.Contains(value)) q.Issues.Add(value.ToLowerInvariant());
                else q.Warnings.Add($"Unknown issue:{value} (any, mojibake, error, lowbitrate, samplerate, mismatch, size, duration).");
                break;
            case "bitrate":
                if (TryParseRange(value, out var kmin, out var kmax)) { q.BitrateMin = kmin; q.BitrateMax = kmax; }
                else q.Warnings.Add($"Invalid bitrate range '{value}' (use bitrate:320 or bitrate:0-127).");
                break;
            case "codec":
                q.Codec = value;
                break;
            case "played":
                if (value.All(char.IsLetter) && Enum.TryParse<PlayedFilter>(value, ignoreCase: true, out var played))
                    q.Played = played;
                else
                    q.Warnings.Add($"Unknown played:{value} (never, session, possible, confirmed).");
                break;
        }
    }

    private static bool TryParseRange(string value, out double min, out double max)
    {
        min = max = 0;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)) return false;
        if (parts.Length == 1) { max = min; return true; }
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)) return false;
        if (min > max) (min, max) = (max, min);
        return true;
    }

    private static bool TryParseDecade(string value, out int decade)
    {
        decade = 0;
        var v = value.TrimEnd('s', 'S');
        if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
        if (n < 100) n = n >= 30 ? 1900 + n : 2000 + n; // 80s -> 1980, 10s -> 2010, 00s -> 2000
        if (n is < 1000 or > 2999) return false;
        decade = n / 10 * 10;
        return true;
    }
}
