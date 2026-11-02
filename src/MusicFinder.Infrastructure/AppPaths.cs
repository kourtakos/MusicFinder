namespace MusicFinder.Infrastructure;

/// <summary>Where the app keeps its data. Installed mode uses LocalAppData; portable mode uses Data\ beside the executable.</summary>
public sealed record AppPaths(string DataDir, string DatabasePath, string SettingsPath, string LogDir, string CacheDir)
{
    public string BackupsDir => Path.Combine(DataDir, "Backups");
    public string RestoreDir => Path.Combine(DataDir, "Restore");
    public string RestoreMarkerPath => Path.Combine(DataDir, "restore-pending.json");
    public string RestoreHistoryPath => Path.Combine(DataDir, "restore-history.json");

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
