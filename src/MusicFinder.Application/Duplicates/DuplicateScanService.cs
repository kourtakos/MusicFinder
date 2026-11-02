using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Duplicates;

/// <summary>
/// Finds byte-identical files. Only files that share a size with another file are hashed (a file with a unique size cannot
/// have an identical twin), one at a time, throttled, and each result is remembered, so a repeat scan or one resumed after
/// Stop hashes only what is new or changed. Nothing is ever deleted, moved or written.
/// </summary>
public sealed class DuplicateScanService : IDuplicateScanService
{
    private readonly IFileHashStore _store;
    private readonly IFileHashService _hasher;
    private readonly ISettingsStore _settings;

    public DuplicateScanService(IFileHashStore store, IFileHashService hasher, ISettingsStore settings)
    {
        _store = store;
        _hasher = hasher;
        _settings = settings;
    }

    public async Task<DuplicateScanSummary> ScanAsync(IProgress<DuplicateScanProgress>? progress, CancellationToken ct)
    {
        var candidates = await _store.GetCandidatesAsync(ct);
        var pending = candidates.Where(c => c.Hash is null).ToList();
        var known = candidates.Count - pending.Count;
        var limit = Math.Max(0, _settings.Load().HashThrottleMbPerSec) * 1024L * 1024L;

        int done = 0, hashed = 0, unreadable = 0;
        long bytes = 0;
        var cancelled = false;
        progress?.Report(new DuplicateScanProgress(pending.Count, 0, 0, 0, null, 0));

        foreach (var c in pending)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            try
            {
                var info = new FileInfo(c.FullPath);
                // The library's idea of the file must still be true, or the hash would describe other bytes.
                if (!info.Exists || info.Length != c.FileSize || UtcTime.Normalize(info.LastWriteTimeUtc) != c.ModifiedUtc)
                {
                    unreadable++;
                }
                else
                {
                    var before = bytes;
                    var hash = await _hasher.HashAsync(c.FullPath, limit, new Progress<long>(n => bytes = before + n), ct);
                    info.Refresh();
                    if (!info.Exists || info.Length != c.FileSize || UtcTime.Normalize(info.LastWriteTimeUtc) != c.ModifiedUtc) unreadable++; // changed while reading
                    else
                    {
                        await _store.PutAsync(c.TrackId, c.FileSize, c.ModifiedUtc, hash, ct);
                        hashed++;
                    }
                    bytes = before + c.FileSize;
                }
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }

            done++;
            progress?.Report(new DuplicateScanProgress(pending.Count, done, hashed, unreadable, c.FullPath, bytes));
        }

        var groups = await GetGroupsAsync(CancellationToken.None);
        return new DuplicateScanSummary(candidates.Count, known, hashed, unreadable, cancelled, groups.Count, groups.Sum(g => g.Members.Count));
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetGroupsAsync(CancellationToken ct)
    {
        var members = await _store.GetHashedMembersAsync(ct);
        return members
            .GroupBy(m => m.Hash)
            .Select(g =>
            {
                var list = g.Select(m => m.Member).ToList();
                var (keep, reasons) = KeepSuggestion.Pick(list);
                return new DuplicateGroup(g.Key, list[0].FileSize, list, keep, reasons);
            })
            .OrderByDescending(g => g.ExtraBytes)
            .ThenBy(g => g.Hash, StringComparer.Ordinal)
            .ToList();
    }

    public Task DismissAsync(string hash, CancellationToken ct) => _store.DismissAsync(hash, ct);
}
