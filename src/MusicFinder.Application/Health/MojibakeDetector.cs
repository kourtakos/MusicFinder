using System.Text;

namespace MusicFinder.Application.Health;

/// <summary>
/// Finds text that is UTF-8 which was read as a single-byte code page ("Î§Î¬ÏÎ¹Ï‚" for "Χάρις"). The test is a round trip:
/// the text is turned back into bytes with Latin-1, Windows-1252 or Windows-1253, and counts as damaged only if those
/// bytes are valid UTF-8 that is shorter than the original. Real Greek text never passes (its bytes are not valid UTF-8),
/// and neither do genuine accented Latin words ("Über", "Café", "Łódź").
/// </summary>
public static class MojibakeDetector
{
    private static readonly Encoding[] Candidates = BuildCandidates();
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static Encoding[] BuildCandidates()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return
        [
            Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), // Latin-1
            Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(1253, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        ];
    }

    public static bool IsDamaged(string? text) => Check(text).Damaged;

    /// <summary>Whether the text looks damaged and, when a repair is certain enough, what it would read as.</summary>
    public static (bool Damaged, string? Repaired) Check(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (false, null);
        if (text.Contains('�')) return (true, null);
        if (!text.Any(c => c >= 0x80)) return (false, null);

        foreach (var encoding in Candidates)
        {
            byte[] bytes;
            try { bytes = encoding.GetBytes(text); }
            catch (EncoderFallbackException) { continue; }

            string repaired;
            try { repaired = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }

            if (repaired.Length < text.Length && !repaired.Any(c => char.IsControl(c) && c != '\t')) return (true, repaired);
        }
        return (false, null);
    }
}
