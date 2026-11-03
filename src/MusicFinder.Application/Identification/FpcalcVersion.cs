using System.Text.RegularExpressions;

namespace MusicFinder.Application.Identification;

/// <summary>Reads the version out of what <c>fpcalc -version</c> prints, e.g. "fpcalc version 1.6.2". The exact wording is not verified, so only the number is taken.</summary>
public static partial class FpcalcVersion
{
    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+(?:\.\d+)?)(?![\d.])")]
    private static partial Regex Number();

    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Number().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }
}
