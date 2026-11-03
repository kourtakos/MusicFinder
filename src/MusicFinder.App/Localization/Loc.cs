using System.Globalization;
using System.Resources;

namespace MusicFinder.App.Localization;

/// <summary>
/// UI text in the user's language. English text in the code and in the XAML is the key: the Greek resource file maps it to Greek, and
/// a text with no entry is shown in English, so a missing translation can never break a screen. The language is chosen once at start-up.
/// </summary>
public static class Loc
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Greek = "el";

    private static readonly ResourceManager Resources = new("MusicFinder.App.Resources.Strings", typeof(Loc).Assembly);
    private static CultureInfo _culture = CultureInfo.InvariantCulture;

    /// <summary>True when texts are looked up in a translation (anything but English).</summary>
    public static bool IsTranslated => !_culture.Equals(CultureInfo.InvariantCulture);

    public static CultureInfo Culture => _culture;

    /// <summary>"auto" follows Windows' display language; "en" and "el" are fixed. Anything else means English.</summary>
    public static string Resolve(string? setting)
    {
        var s = (setting ?? Auto).Trim().ToLowerInvariant();
        if (s == Auto) s = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return s == Greek ? Greek : English;
    }

    public static void Initialize(string? setting)
    {
        var language = Resolve(setting);
        _culture = language == Greek ? CultureInfo.GetCultureInfo("el-GR") : CultureInfo.InvariantCulture;
        if (!IsTranslated) return;
        // Dates, numbers and the resource lookup follow the chosen language too.
        CultureInfo.DefaultThreadCurrentCulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _culture;
    }

    /// <summary>The text in the current language, or the English text itself when there is no translation.</summary>
    public static string T(string english)
    {
        if (!IsTranslated || string.IsNullOrEmpty(english)) return english;
        return Lookup(english) ?? english;
    }

    /// <summary>A translated format string filled in with <paramref name="args"/>.</summary>
    public static string T(string englishFormat, params object?[] args) => string.Format(_culture, T(englishFormat), args);

    /// <summary>Like <see cref="T(string)"/> but keeps leading and trailing spaces (they are not part of the key).</summary>
    public static string Padded(string english)
    {
        if (!IsTranslated || string.IsNullOrWhiteSpace(english)) return english;
        var core = english.Trim();
        var translated = Lookup(core);
        if (translated is null) return english;
        var start = english.Length - english.TrimStart().Length;
        var end = english.Length - english.TrimEnd().Length;
        return english[..start] + translated + english[(english.Length - end)..];
    }

    private static string? Lookup(string key)
    {
        try { return Resources.GetString(key, _culture); }
        catch (MissingManifestResourceException) { return null; }
    }
}
