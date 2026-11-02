namespace MusicFinder.Infrastructure;

/// <summary>Where the app keeps its data. Installed mode uses LocalAppData; portable mode uses Data\ beside the executable.</summary>
public sealed record AppPaths(string DataDir, string DatabasePath, string SettingsPath, string LogDir, string CacheDir)
{
    public static AppPaths Resolve(bool portable, string? baseDirectory = null)
    {
        var dataDir = portable
            ? Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicFinder");
        return new AppPaths(
            dataDir,
            Path.Combine(dataDir, "musicfinder.db"),
            Path.Combine(dataDir, "settings.json"),
            Path.Combine(dataDir, "logs"),
            Path.Combine(dataDir, "Cache"));
    }
}
