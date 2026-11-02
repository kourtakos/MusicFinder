using System.Globalization;
using System.Text;

namespace MusicFinder.Application.Services;

/// <summary>
/// Basic case/diacritic/final-sigma folding for search. Display values are never altered.
/// Greeklish and keyboard-layout handling arrive in M7.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                var c = char.ToLowerInvariant(ch);
                sb.Append(c == 'ς' ? 'σ' : c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }
        return sb.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }

    public static string BuildSearchText(params string?[] parts) =>
        string.Join(' ', parts.Select(Normalize).Where(p => p.Length > 0));
}
