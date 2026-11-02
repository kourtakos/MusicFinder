using System.Globalization;
using System.Text;

namespace MusicFinder.Application.Search;

/// <summary>
/// Physical-key mapping between the US QWERTY layout and the standard Windows "Greek" layout. This is NOT transliteration:
/// it answers "what was the user typing if the wrong keyboard layout was active?". Someone who searches for Χάρις but has the US layout
/// on presses the keys x ; a r i s and sees <c>x;aris</c>; someone who wants "giorgos" with the Greek layout on sees <c>γιοργοσ</c>.
/// The mapping is exactly reversible for letters. Only the standard Windows Greek layout is supported (not Polytonic, 319 or macOS).
/// </summary>
public static class GreekKeyboardLayout
{
    // The Greek letter printed on each US letter key. Q produces the Greek ';' (a punctuation mark, never part of a name).
    private const string UsKeys = "wertyuiopasdfghjklzxcvbnm";
    private const string GreekKeys = "ςερτυθιοπασδφγηξκλζχψωβνμ";

    // Dead key on the US ';' key: the tonos. Dead key on the US ':' key: the dialytika.
    private const char Tonos = ';';
    private const char Dialytika = ':';

    private const string GreekVowels = "αεηιουωάέήίόύώϊϋΐΰ";
    private const string LatinVowels = "aeiouy";

    private static readonly Dictionary<char, char> ToGreekMap = Build(UsKeys, GreekKeys);
    private static readonly Dictionary<char, char> ToLatinMap = Build(GreekKeys, UsKeys);

    private static readonly Dictionary<char, char> WithTonos = new()
    {
        ['α'] = 'ά', ['ε'] = 'έ', ['η'] = 'ή', ['ι'] = 'ί', ['ο'] = 'ό', ['υ'] = 'ύ', ['ω'] = 'ώ'
    };

    private static Dictionary<char, char> Build(string from, string to)
    {
        var map = new Dictionary<char, char>();
        for (var i = 0; i < from.Length; i++) map[from[i]] = to[i];
        return map;
    }

    /// <summary>
    /// Latin keys typed while the Greek layout was meant: <c>x;aris</c> becomes <c>χάρισ</c>. False when the token is not a plausible
    /// wrong-layout word (not purely Latin letters and dead keys, shorter than two letters, or the result has no Greek vowel).
    /// </summary>
    public static bool TryMapToGreek(string raw, out string greek)
    {
        greek = "";
        if (raw.Length < 2) return false;

        var sb = new StringBuilder(raw.Length);
        var letters = 0;
        var pending = '\0'; // a dead key waiting for the vowel it accents
        foreach (var original in raw)
        {
            var c = char.ToLowerInvariant(original);
            if (c is Tonos or Dialytika) { pending = c; continue; }

            if (c == 'q') { pending = '\0'; continue; } // the Greek ';' key: punctuation, no letter
            if (!ToGreekMap.TryGetValue(c, out var g)) return false; // digits, '&', accented letters...: not a wrong-layout word
            letters++;

            if (pending == Tonos && WithTonos.TryGetValue(g, out var accented)) g = accented;
            else if (pending == Dialytika) g = g switch { 'ι' => 'ϊ', 'υ' => 'ϋ', _ => g };
            pending = '\0';
            sb.Append(g);
        }

        if (letters < 2 || !sb.ToString().Any(ch => GreekVowels.Contains(ch))) return false;
        greek = sb.ToString();
        return true;
    }

    /// <summary>
    /// Greek letters typed while the Latin layout was meant: <c>γιοργοσ</c> becomes <c>giorgos</c>. Accents are ignored (the dead key
    /// that produced them has no Latin counterpart). False when the token is not purely Greek letters, is shorter than two letters, or has no vowel.
    /// </summary>
    public static bool TryMapToLatin(string raw, out string latin)
    {
        latin = "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (!ScriptDetector.IsGreekLetter(ch)) return false;
            if (!ToLatinMap.TryGetValue(char.ToLowerInvariant(ch), out var l)) return false;
            sb.Append(l);
        }

        if (sb.Length < 2 || !sb.ToString().Any(ch => LatinVowels.Contains(ch))) return false;
        latin = sb.ToString();
        return true;
    }
}
