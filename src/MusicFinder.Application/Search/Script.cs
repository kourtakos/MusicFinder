namespace MusicFinder.Application.Search;

public enum Script
{
    /// <summary>No letters (digits, punctuation).</summary>
    None,
    Latin,
    Greek,
    /// <summary>Letters from both alphabets, or from another script.</summary>
    Mixed
}

public static class ScriptDetector
{
    public static bool IsGreekLetter(char c) => c is >= 'Α' and <= 'ω' or >= 'ά' and <= 'ώ' or >= 'ἀ' and <= '῿';

    public static bool IsLatinLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static Script Of(string? text)
    {
        var greek = false;
        var latin = false;
        var other = false;
        foreach (var c in text ?? "")
        {
            if (IsGreekLetter(c)) greek = true;
            else if (IsLatinLetter(c)) latin = true;
            else if (char.IsLetter(c)) other = true; // accented Latin, Cyrillic, ... : not something the Greek rules understand
        }
        if (other || (greek && latin)) return Script.Mixed;
        return greek ? Script.Greek : latin ? Script.Latin : Script.None;
    }
}
