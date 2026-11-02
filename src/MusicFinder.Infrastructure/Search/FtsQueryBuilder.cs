using MusicFinder.Application.Search;
using MusicFinder.Application.Services;

namespace MusicFinder.Infrastructure.Search;

/// <summary>
/// Builds a safe FTS5 MATCH expression. Every user value is emitted as a double-quoted string
/// (embedded quotes doubled), so FTS operators in user input are always literals. The result is
/// passed as a SQL parameter, never concatenated into SQL.
/// </summary>
public static class FtsQueryBuilder
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = "Artist", ["title"] = "Title", ["album"] = "Album", ["genre"] = "GenreRaw"
    };

    /// <summary>Returns null when no term contains searchable characters.</summary>
    public static string? Build(IEnumerable<TextTerm> terms)
    {
        var parts = new List<string>();
        foreach (var term in terms)
        {
            var norm = TextNormalizer.Normalize(term.Value);
            if (norm.Length == 0) continue; // punctuation-only input has nothing to match

            var star = term.IsPhrase ? "" : "*";
            var raw = $"\"{Escape(term.Value)}\"{star}";
            var normalized = $"\"{Escape(norm)}\"{star}";

            if (term.Field is not null && Columns.TryGetValue(term.Field, out var col))
            {
                parts.Add(raw == normalized
                    ? $"{col}:{raw}"
                    : $"({col}:{raw} OR {col}:{normalized})");
            }
            else
            {
                // Original columns (exact tokens, path) OR the normalized text (accent/case-insensitive Greek).
                parts.Add($"({raw} OR NormalizedText:{normalized})");
            }
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static string Escape(string value) => value.Replace("\"", "\"\"");
}
