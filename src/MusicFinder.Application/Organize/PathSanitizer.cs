using System.Text;

namespace MusicFinder.Application.Organize;

/// <summary>Turns text from tags into something Windows accepts as one file or folder name. Unicode (Greek included) is kept as it is.</summary>
public static class PathSanitizer
{
    public const int MaxSegmentLength = 255;
    public const int MaxPathLength = 259;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>One tag value: characters that cannot appear in a name (including path separators) become '_', whitespace runs collapse.</summary>
    public static string CleanValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastSpace = false;
        foreach (var ch in value)
        {
            var c = IsInvalid(ch) ? '_' : ch;
            if (char.IsWhiteSpace(c))
            {
                if (lastSpace) continue;
                lastSpace = true;
                c = ' ';
            }
            else lastSpace = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>A whole file or folder name: no trailing dots or spaces, no reserved device name, at most 255 characters (the extension is kept).</summary>
    public static string Segment(string name)
    {
        var s = CleanValue(name).TrimEnd('.', ' ').TrimStart(' ');
        if (s.Length == 0) return "_";

        var stemEnd = s.IndexOf('.');
        var stem = (stemEnd < 0 ? s : s[..stemEnd]).TrimEnd(' ');
        if (Reserved.Contains(stem)) s = "_" + s;

        if (s.Length > MaxSegmentLength)
        {
            var dot = s.LastIndexOf('.');
            var ext = dot > 0 && s.Length - dot <= 10 ? s[dot..] : "";
            var keep = MaxSegmentLength - ext.Length;
            if (keep > 0 && char.IsHighSurrogate(s[keep - 1])) keep--; // never cut a surrogate pair in half
            s = s[..keep].TrimEnd('.', ' ') + ext;
        }
        return s;
    }

    private static bool IsInvalid(char c) => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
}
