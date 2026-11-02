using MusicFinder.Domain.Enums;

namespace MusicFinder.Domain.Entities;

public sealed class LibraryRoot
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    private DateTime? _lastScanUtc;

    /// <summary>Always UTC; the setter normalizes Kind because nullable DateTime bypasses the Dapper handler.</summary>
    public DateTime? LastScanUtc
    {
        get => _lastScanUtc;
        set => _lastScanUtc = Models.UtcTime.Normalize(value);
    }
    public ScanStatus Status { get; set; } = ScanStatus.Pending;
}
