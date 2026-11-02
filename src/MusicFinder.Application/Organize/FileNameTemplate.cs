using System.Globalization;
using System.Text;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Organize;

public sealed record TemplateRender(string? Text, IReadOnlyList<string> Missing);

/// <summary>
/// A rename template such as <c>{Artist} - {Title}</c>. Tokens: Artist, Title, Album, AlbumArtist, Year, Track (<c>{Track:00}</c> pads),
/// Genre, Bpm, Key, Extension, FileName. A text token takes <c>:upper</c>, <c>:lower</c> or <c>:title</c> only when asked.
/// Without <c>{Extension}</c> the file keeps its extension automatically.
/// </summary>
public sealed class FileNameTemplate
{
    private abstract record Part;
    private sealed record Literal(string Text) : Part;
    private sealed record Token(string Name, string? Format) : Part;

    private static readonly string[] Known =
        ["Artist", "Title", "Album", "AlbumArtist", "Genre", "Key", "FileName", "Extension", "Year", "Track", "Bpm"];

    private readonly List<Part> _parts;
    public string? Error { get; }
    public bool HasExtensionToken { get; }

    private FileNameTemplate(List<Part> parts, string? error)
    {
        _parts = parts;
        Error = error;
        HasExtensionToken = parts.OfType<Token>().Any(t => t.Name == "Extension");
    }

    public static FileNameTemplate Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail("The template is empty.");
        var parts = new List<Part>();
        var lit = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '}') return Fail("A '}' has no matching '{'.");
            if (c != '{') { lit.Append(c); continue; }

            var close = text.IndexOf('}', i + 1);
            if (close < 0) return Fail("A '{' is never closed.");
            var inner = text[(i + 1)..close];
            if (inner.Contains('{')) return Fail("A '{' is never closed.");

            var colon = inner.IndexOf(':');
            var name = (colon < 0 ? inner : inner[..colon]).Trim();
            var format = colon < 0 ? null : inner[(colon + 1)..].Trim();
            var canonical = Known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
                return Fail($"Unknown token {{{name}}}. Use Artist, Title, Album, AlbumArtist, Year, Track, Genre, Bpm, Key, Extension or FileName.");

            if (format is not null)
            {
                var numeric = canonical is "Track" or "Year" or "Bpm";
                var ok = numeric ? format.Length > 0 && format.All(ch => ch == '0') : format is "upper" or "lower" or "title";
                if (!ok)
                    return Fail(numeric
                        ? $"{{{canonical}:{format}}}: the format is a run of zeros, such as {{{canonical}:00}}."
                        : $"{{{canonical}:{format}}}: use upper, lower or title.");
            }

            if (lit.Length > 0) { parts.Add(new Literal(lit.ToString())); lit.Clear(); }
            parts.Add(new Token(canonical, format));
            i = close;
        }
        if (lit.Length > 0) parts.Add(new Literal(lit.ToString()));
        return new FileNameTemplate(parts, null);

        static FileNameTemplate Fail(string why) => new([], why);
    }

    /// <summary>The new name (not yet sanitized as a whole), or null with the tokens that had no value.</summary>
    public TemplateRender Render(RenameSource s, bool placeholders)
    {
        var missing = new List<string>();
        var sb = new StringBuilder();
        var ext = Path.GetExtension(s.FullPath).TrimStart('.');
        foreach (var p in _parts)
        {
            if (p is Literal l) { sb.Append(l.Text); continue; }
            var t = (Token)p;
            var value = ValueOf(t, s, ext);
            if (value is null)
            {
                if (!placeholders) { missing.Add(t.Name); continue; }
                value = Placeholder(t.Name);
            }
            sb.Append(PathSanitizer.CleanValue(value));
        }
        return missing.Count > 0 ? new TemplateRender(null, missing) : new TemplateRender(sb.ToString(), missing);
    }

    private static string? ValueOf(Token t, RenameSource s, string ext)
    {
        var text = t.Name switch
        {
            "Artist" => Blank(s.Artist),
            "Title" => Blank(s.Title),
            "Album" => Blank(s.Album),
            "AlbumArtist" => Blank(s.AlbumArtist),
            "Genre" => Blank(s.Genre),
            "Key" => Blank(s.Key),
            "Extension" => ext,
            "FileName" => Path.GetFileNameWithoutExtension(s.FullPath),
            "Year" => s.Year is > 0 ? Pad(s.Year.Value, t.Format) : null,
            "Track" => s.TrackNumber is > 0 ? Pad(s.TrackNumber.Value, t.Format) : null,
            "Bpm" => s.Bpm is > 0 ? FormatBpm(s.Bpm.Value, t.Format) : null,
            _ => null,
        };
        if (text is null) return null;
        return t.Format switch
        {
            "upper" => text.ToUpperInvariant(),
            "lower" => text.ToLowerInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
            _ => text,
        };
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static string Pad(int n, string? format) => n.ToString(CultureInfo.InvariantCulture).PadLeft(format?.Length ?? 0, '0');

    private static string FormatBpm(double bpm, string? format)
    {
        var rounded = Math.Round(bpm);
        var whole = Math.Abs(bpm - rounded) < 0.05;
        var text = whole ? ((int)rounded).ToString(CultureInfo.InvariantCulture) : bpm.ToString("0.0", CultureInfo.InvariantCulture);
        return format is null ? text : text.PadLeft(format.Length, '0');
    }

    private static string Placeholder(string name) => name switch
    {
        "Artist" or "AlbumArtist" => "Unknown Artist",
        "Title" => "Unknown Title",
        "Album" => "Unknown Album",
        "Genre" => "Unknown Genre",
        "Year" => "Unknown Year",
        "Track" => "00",
        "Bpm" => "Unknown BPM",
        "Key" => "Unknown Key",
        _ => "Unknown",
    };
}
