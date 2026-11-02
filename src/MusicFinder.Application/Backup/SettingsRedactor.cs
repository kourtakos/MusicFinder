using System.Text.Json;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Backup;

/// <summary>Settings as stored in a backup: a copy, without the secrets unless the user asked for them.</summary>
public static class SettingsRedactor
{
    public static AppSettings ForBackup(AppSettings settings, bool includeSecrets)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        if (!includeSecrets)
        {
            copy.AcoustIdApiKey = null;
            copy.MusicBrainzContact = null;
            copy.IdentificationConsentUtc = null;
            copy.IdentificationEnabled = false;
        }
        return copy;
    }
}
