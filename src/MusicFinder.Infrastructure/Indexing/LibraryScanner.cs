using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Application.Health;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Indexing;

/// <summary>
/// Breadth-first library scanner. Folders appear in the tree as they are discovered, unchanged files
/// (same size + mtime) are not re-read, tag reads run with bounded concurrency, and files that are
/// no longer found are flagged Missing, never deleted. An unreachable root is marked Offline and
/// leaves every existing track untouched.
/// </summary>
public sealed class LibraryScanner : IIndexer
{
    private const int BatchSize = 200;

    // Hidden/System attributes are common on user folders (Windows folder customization) and must not hide music.
    private static readonly EnumerationOptions FileOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0
    };

    private readonly ILibraryRootRepository _roots;
    private readonly IFolderRepository _folders;
    private readonly ITrackRepository _tracks;
    private readonly IMetadataReader _reader;
    private readonly ILogger<LibraryScanner> _log;
    private readonly ILibraryWriteLock? _writeLock;

    public int ReaderConcurrency { get; set; } = 3;

    public LibraryScanner(ILibraryRootRepository roots, IFolderRepository folders, ITrackRepository tracks,
        IMetadataReader reader, ILogger<LibraryScanner>? log = null, ILibraryWriteLock? writeLock = null)
    {
        _writeLock = writeLock;
        _roots = roots;
        _folders = folders;
        _tracks = tracks;
        _reader = reader;
        _log = log ?? NullLogger<LibraryScanner>.Instance;
    }

    public Task ReconcileAsync(long rootId, CancellationToken ct) => ScanRootAsync(rootId, null, ct);

    public async Task ScanRootAsync(long rootId, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        // A scan waits for a running batch of file renames; renames refuse to start while a scan runs.
        using var gate = _writeLock is null ? null : await _writeLock.EnterAsync(ct).ConfigureAwait(false);
        var root = await _roots.GetAsync(rootId, ct) ?? throw new InvalidOperationException($"Unknown library root {rootId}.");

        if (!Directory.Exists(root.Path))
        {
            _log.LogWarning("Library root {Path} is not accessible; marking offline without touching its tracks.", root.Path);
            await _roots.UpdateStatusAsync(rootId, ScanStatus.Offline, null, CancellationToken.None);
            return;
        }

        var scanStart = DateTime.UtcNow;
        var state = new ScanState();
        await _roots.UpdateStatusAsync(rootId, ScanStatus.Scanning, null, ct);

        try
        {
            var known = (await _tracks.GetIndexedFilesAsync(rootId, ct))
                .ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

            var rootName = new DirectoryInfo(root.Path).Name;
            var rootFolderId = await _folders.UpsertAsync(rootId, null, root.Path, rootName, ct);
            state.Folders++;

            var queue = new Queue<(string Path, long Id)>();
            queue.Enqueue((root.Path, rootFolderId));
            var clock = Stopwatch.StartNew();
            long lastReport = -1000;

            void Report(string phase, string? current, bool force = false)
            {
                if (progress is null) return;
                if (!force && clock.ElapsedMilliseconds - lastReport < 250) return;
                lastReport = clock.ElapsedMilliseconds;
                progress.Report(new ScanProgress(phase, state.Folders, state.Files, state.Processed, state.Errors, current));
            }

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, folderId) = queue.Dequeue();
                Report("Scanning", dir);

                if (root.IncludeSubfolders)
                {
                    foreach (var sub in EnumerateSubdirectories(dir))
                    {
                        var subId = await _folders.UpsertAsync(rootId, folderId, sub.FullName, sub.Name, ct);
                        state.Folders++;
                        queue.Enqueue((sub.FullName, subId));
                    }
                }

                await ProcessDirectoryAsync(dir, folderId, known, scanStart, state, () => Report("Scanning", dir), ct);
            }

            Report("Finalizing", null, force: true);
            await _roots.SetUnsupportedAsync(rootId, state.Unsupported.ToDictionary(kv => kv.Key, kv => kv.Value), ct);
            await _tracks.MarkMissingAsync(rootId, scanStart, ct);
            await _folders.RecomputeCountsAsync(rootId, ct);
            await _roots.UpdateStatusAsync(rootId, ScanStatus.Indexed, DateTime.UtcNow, ct);
            Report("Done", null, force: true);
        }
        catch (OperationCanceledException)
        {
            await _roots.UpdateStatusAsync(rootId, ScanStatus.Cancelled, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan of {Path} failed.", root.Path);
            await _roots.UpdateStatusAsync(rootId, ScanStatus.Error, null, CancellationToken.None);
            throw;
        }
    }

    private async Task ProcessDirectoryAsync(string dir, long folderId, Dictionary<string, IndexedFile> known,
        DateTime scanStart, ScanState state, Action report, CancellationToken ct)
    {
        var unchanged = new List<long>();
        var changed = new List<FileInfo>();

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*", FileOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Cannot enumerate {Dir}; skipping.", dir);
            state.Errors++;
            return;
        }

        foreach (var path in files)
        {
            if (!SupportedFormats.IsSupported(path))
            {
                if (SupportedFormats.IsKnownUnsupported(path))
                    state.Unsupported.AddOrUpdate(Path.GetExtension(path).ToLowerInvariant(), 1, (_, n) => n + 1);
                continue;
            }
            FileInfo info;
            try { info = new FileInfo(path); _ = info.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.Errors++;
                continue;
            }

            state.Files++;
            if (known.TryGetValue(path, out var existing) &&
                existing.FileSize == info.Length &&
                existing.ModifiedUtc == info.LastWriteTimeUtc &&
                existing.MetaVersion >= Track.CurrentMetaVersion) // older rows are re-read once when the reader learns new fields
            {
                unchanged.Add(existing.Id);
                state.Processed++;
            }
            else
            {
                changed.Add(info);
            }
        }

        if (unchanged.Count > 0) await _tracks.TouchAsync(unchanged, scanStart, ct);

        foreach (var chunk in changed.Chunk(BatchSize))
        {
            var batch = new Track?[chunk.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunk.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, ReaderConcurrency), CancellationToken = ct },
                async (i, token) =>
                {
                    batch[i] = await BuildTrackAsync(chunk[i], folderId, scanStart, state, token);
                    Interlocked.Increment(ref state.Processed);
                });
            await _tracks.UpsertBatchAsync(batch.Where(t => t is not null).Select(t => t!).ToList(), ct);
            report();
        }
    }

    private async Task<Track> BuildTrackAsync(FileInfo info, long folderId, DateTime scanStart, ScanState state, CancellationToken ct)
    {
        TrackMetadata meta;
        string? readError = null;
        try
        {
            meta = await _reader.ReadAsync(info.FullName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Corrupt or unsupported tags: index by file name so the file is still findable.
            _log.LogWarning(ex, "Could not read tags from {Path}.", info.FullName);
            Interlocked.Increment(ref state.Errors);
            meta = new TrackMetadata();
            readError = ex.GetType().Name + ": " + ex.Message;
        }

        return new Track
        {
            FolderId = folderId,
            FullPath = info.FullName,
            FileName = info.Name,
            Extension = info.Extension.ToLowerInvariant(),
            FileSize = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            IndexedUtc = scanStart,
            Title = meta.Title,
            Artist = meta.Artist,
            AlbumArtist = meta.AlbumArtist,
            Album = meta.Album,
            GenreRaw = meta.GenreRaw,
            Year = meta.Year,
            TrackNumber = meta.TrackNumber,
            DurationMs = meta.DurationMs,
            BitrateKbps = meta.BitrateKbps,
            SampleRateHz = meta.SampleRateHz,
            Bpm = meta.Bpm,
            MusicalKey = meta.MusicalKey,
            MusicBrainzRecordingId = meta.MusicBrainzRecordingId,
            ArtworkCacheKey = meta.ArtworkHash,
            Codec = meta.Codec,
            Channels = meta.Channels,
            BitDepth = meta.BitDepth,
            ReadError = readError,
            IssueFlags = (int)QualityRules.Evaluate(info.Extension, info.Length, meta, readError is not null),
            MetaVersion = Track.CurrentMetaVersion,
            NormalizedText = TextNormalizer.BuildSearchText(
                info.Name, meta.Title, meta.Artist, meta.AlbumArtist, meta.Album, meta.GenreRaw)
        };
    }

    /// <summary>Subdirectories excluding reparse points (symlinks/junctions) to avoid cycles.</summary>
    private IEnumerable<DirectoryInfo> EnumerateSubdirectories(string dir)
    {
        IEnumerable<DirectoryInfo> subs;
        try
        {
            subs = new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Cannot enumerate subfolders of {Dir}.", dir);
            yield break;
        }
        foreach (var s in subs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)) yield return s;
    }

    private sealed class ScanState
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Unsupported = new();
        public int Folders;
        public int Files;
        public int Processed;
        public int Errors;
    }
}
