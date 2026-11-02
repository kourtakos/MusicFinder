using System.Globalization;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Tags;

/// <summary>
/// Pure validation and canonicalization of tag edits, shared by the editor's preview and the file writer so
/// "what you previewed" is exactly "what gets written".
/// </summary>
public static class TagChangeValidator
{
    public const int MinYear = 1900;
    public const int MaxYear = 2100;
    public const double MinBpm = 40;
    public const double MaxBpm = 300;
    private const int MaxTextLength = 500;
    private const int MaxKeyLength = 10;

    /// <summary>Human-readable problems; empty when every value is acceptable.</summary>
    public static IReadOnlyList<string> Validate(TagChangeSet changes)
    {
        var errors = new List<string>();
        foreach (var (field, raw) in changes.Changes)
            if (Canonicalize(field, raw, out var error) is null && error is not null)
                errors.Add(error);
        return errors;
    }

    /// <summary>
    /// The canonical text to store for a field: trimmed text, "1985", "3", "128", or "A; B" for genres.
    /// Returns null with no error when the field should be cleared, and null with an error when invalid.
    /// </summary>
    public static string? Canonicalize(TagField field, string? raw, out string? error)
    {
        error = null;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null; // blank clears the field

        switch (field)
        {
            case TagField.Year:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < MinYear or > MaxYear)
                    return Fail($"Year must be a number between {MinYear} and {MaxYear} (got '{value}').", out error);
                return year.ToString(CultureInfo.InvariantCulture);

            case TagField.TrackNumber:
                var number = value.Split('/', 2)[0].Trim(); // accept "3" and "3/12"; only the track number is stored
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var track) || track is < 0 or > 999)
                    return Fail($"Track number must be a number between 0 and 999 (got '{value}').", out error);
                return track.ToString(CultureInfo.InvariantCulture);

            case TagField.Bpm:
                if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)
                    || double.IsNaN(bpm) || bpm < MinBpm || bpm > MaxBpm)
                    return Fail($"BPM must be a number between {MinBpm} and {MaxBpm} (got '{value}').", out error);
                return Math.Round(bpm, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

            case TagField.Genre:
                var parts = SplitGenres(value);
                if (parts.Count == 0) return null;
                if (parts.Any(p => !IsPlainText(p, MaxTextLength)))
                    return Fail("Genre contains unsupported characters or is too long.", out error);
                return string.Join("; ", parts);

            case TagField.Key:
                return IsPlainText(value, MaxKeyLength) ? value : Fail($"Key is limited to {MaxKeyLength} characters.", out error);

            default:
                return IsPlainText(value, MaxTextLength)
                    ? value
                    : Fail($"{field} contains unsupported characters or is longer than {MaxTextLength} characters.", out error);
        }
    }

    /// <summary>Genres are entered separated by ';' and stored as separate values.</summary>
    public static IReadOnlyList<string> SplitGenres(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsPlainText(string value, int maxLength) =>
        value.Length <= maxLength && !value.Any(char.IsControl);

    private static string? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }
}
