using System.Text.RegularExpressions;

namespace MusicFinder.Application.Similar;

/// <summary>
/// A musical key on the Camelot wheel (1A-12B; A = minor, B = major). Parses the notations DJ software and taggers actually write:
/// Camelot (<c>8A</c>, <c>08a</c>), Open Key (<c>1m</c>, <c>1d</c>) and note names (<c>Am</c>, <c>A minor</c>, <c>F#m</c>, <c>Gbmaj</c>, <c>C</c>).
/// Anything else is not a key: it is reported as absent, never guessed.
/// </summary>
public readonly partial record struct CamelotKey(int Number, bool IsMinor)
{
    public override string ToString() => $"{Number}{(IsMinor ? 'A' : 'B')}";

    [GeneratedRegex(@"^(?<n>\d{1,2})\s*(?<l>[ABab])$")]
    private static partial Regex CamelotPattern();

    [GeneratedRegex(@"^(?<n>\d{1,2})\s*(?<l>[mdMD])$")]
    private static partial Regex OpenKeyPattern();

    [GeneratedRegex(@"^(?<note>[A-Ga-g])(?<acc>[#♯b♭]?)\s*(?<mode>maj(?:or)?|min(?:or)?|m|M)?$")]
    private static partial Regex NotePattern();

    public static bool TryParse(string? text, out CamelotKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var camelot = CamelotPattern().Match(s);
        if (camelot.Success)
        {
            var n = int.Parse(camelot.Groups["n"].Value);
            if (n is < 1 or > 12) return false;
            key = new CamelotKey(n, char.ToUpperInvariant(camelot.Groups["l"].Value[0]) == 'A');
            return true;
        }

        var open = OpenKeyPattern().Match(s);
        if (open.Success)
        {
            var n = int.Parse(open.Groups["n"].Value);
            if (n is < 1 or > 12) return false;
            // Open Key 1d is C major (Camelot 8B) and 1m is A minor (8A): Camelot = Open + 7 on the wheel of twelve.
            var number = (n + 7) % 12;
            key = new CamelotKey(number == 0 ? 12 : number, char.ToLowerInvariant(open.Groups["l"].Value[0]) == 'm');
            return true;
        }

        var note = NotePattern().Match(s);
        if (!note.Success) return false;

        var pitch = PitchClass(char.ToUpperInvariant(note.Groups["note"].Value[0]));
        pitch += note.Groups["acc"].Value switch { "#" or "♯" => 1, "b" or "♭" => -1, _ => 0 };
        pitch = ((pitch % 12) + 12) % 12;

        var mode = note.Groups["mode"].Value;
        var minor = mode.StartsWith("min", StringComparison.OrdinalIgnoreCase) || mode == "m";
        // Around the circle of fifths each step of seven semitones is one Camelot number: C major is 8B and A minor is 8A.
        var camelotNumber = minor ? 8 + 7 * (pitch - 9) : 8 + 7 * pitch;
        camelotNumber = ((camelotNumber % 12) + 12) % 12;
        key = new CamelotKey(camelotNumber == 0 ? 12 : camelotNumber, minor);
        return true;
    }

    /// <summary>The key whose tonic is this pitch class (0 = C, 1 = C#, ... 11 = B), major or minor.</summary>
    public static CamelotKey FromPitchClass(int tonicPitchClass, bool minor)
    {
        var pc = ((tonicPitchClass % 12) + 12) % 12;
        var number = minor ? 8 + 7 * (pc - 9) : 8 + 7 * pc;           // the same circle-of-fifths rule as parsing
        number = ((number % 12) + 12) % 12;
        return new CamelotKey(number == 0 ? 12 : number, minor);
    }

    /// <summary>The pitch class of the tonic (0 = C ... 11 = B). Seven is its own inverse modulo twelve, so the circle of fifths is undone by multiplying by 7.</summary>
    public int TonicPitchClass
    {
        get
        {
            var step = (((Number - 8) * 7) % 12 + 12) % 12;
            return IsMinor ? (step + 9) % 12 : step;
        }
    }

    /// <summary>Open Key: the same wheel turned so that 1d is C major and 1m is A minor.</summary>
    public string ToOpenKey() => $"{((Number + 4) % 12) + 1}{(IsMinor ? 'm' : 'd')}";

    private static readonly string[] MinorNames = ["Abm", "Ebm", "Bbm", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F#m", "Dbm"];
    private static readonly string[] MajorNames = ["B", "F#", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E"];

    /// <summary>The key as a note name in the spelling the Camelot wheel uses ("Am", "Dbm", "F#", "Bb").</summary>
    public string ToNoteName() => (IsMinor ? MinorNames : MajorNames)[Number - 1];

    public string Format(MusicFinder.Domain.Models.KeyNotation notation) => notation switch
    {
        MusicFinder.Domain.Models.KeyNotation.OpenKey => ToOpenKey(),
        MusicFinder.Domain.Models.KeyNotation.NoteName => ToNoteName(),
        _ => ToString()
    };

    private static int PitchClass(char note) => note switch
    {
        'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11
    };

    /// <summary>
    /// How well two keys mix, using the standard Camelot rules only: the same key (1.0), the relative major/minor (0.9: same number, other letter)
    /// and a neighbour on the wheel (0.8: same letter, number one up or down, 12 wraps to 1). Everything else scores 0; "energy boost" diagonals are
    /// a matter of taste, not a rule, and are not included.
    /// </summary>
    public static (double Value, string? Reason) Compare(CamelotKey seed, CamelotKey other)
    {
        if (seed == other) return (1.0, $"same key ({seed})");
        if (seed.Number == other.Number) return (0.9, $"relative key ({seed} → {other})");

        var step = ((other.Number - seed.Number) % 12 + 12) % 12;
        if (seed.IsMinor == other.IsMinor && (step == 1 || step == 11)) return (0.8, $"adjacent key ({seed} → {other})");
        return (0, null);
    }
}
