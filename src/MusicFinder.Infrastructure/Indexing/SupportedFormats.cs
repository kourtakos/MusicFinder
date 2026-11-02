namespace MusicFinder.Infrastructure.Indexing;

public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".wav", ".aiff" };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));
}
