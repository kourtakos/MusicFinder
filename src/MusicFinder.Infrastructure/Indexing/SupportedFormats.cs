namespace MusicFinder.Infrastructure.Indexing;

public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".aiff" };

    /// <summary>Audio-like files the scanner skips; counted per root so the user can see what is not in the library.</summary>
    private static readonly HashSet<string> KnownUnsupported =
        new(StringComparer.OrdinalIgnoreCase) { ".wma", ".ape", ".opus", ".mp4", ".m4p", ".aif", ".wv", ".mpc", ".dsf", ".ac3", ".mid", ".midi" };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsKnownUnsupported(string path) => KnownUnsupported.Contains(Path.GetExtension(path));
}
