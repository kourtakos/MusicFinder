namespace MusicFinder.Application.Identification;

/// <summary>The outcome of looking at pasted text that should be an AcoustID application key. <see cref="Key"/> is the cleaned key when valid.</summary>
public sealed record AcoustIdKeyCheck(bool IsValid, string Key, string? Problem);

/// <summary>
/// A first look at a pasted AcoustID application key, before anything is sent. It only catches what people really paste by mistake
/// (a web address, two words, the quotes around it). Whether AcoustID accepts the key is decided by AcoustID, through "Test the key".
/// Real keys are short ASCII letters and digits; that shape is assumed from the keys seen, not from documentation.
/// </summary>
public static class AcoustIdKeyFormat
{
    public const int MinLength = 4;
    public const int MaxLength = 64;

    public static AcoustIdKeyCheck Validate(string? text)
    {
        var key = (text ?? "").Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
        if (key.Length == 0) return new(false, "", "Paste your AcoustID application key here.");
        if (key.Contains("://", StringComparison.Ordinal) || key.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || key.Contains('/'))
            return new(false, key, "That looks like a web address. Paste only the key itself, the short code shown on your application's page.");
        if (key.Any(char.IsWhiteSpace))
            return new(false, key, "A key has no spaces. Copy only the code, without any words around it.");
        if (key.Length < MinLength) return new(false, key, "That is too short to be a key.");
        if (key.Length > MaxLength) return new(false, key, "That is too long to be a key. Copy only the short code.");
        if (!key.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            return new(false, key, "A key has only letters and digits. Check that nothing else was copied.");
        return new(true, key, null);
    }
}
