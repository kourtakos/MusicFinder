using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure;

namespace MusicFinder.App.Services.Artwork;

/// <summary>
/// Turns artwork bytes into small frozen bitmaps for the grid and details panel. Thumbnails are cached on disk
/// (content-addressed, so an album's tracks share one file) and in a bounded in-memory LRU. Decoding never
/// happens on the UI thread, never blocks scrolling, and malformed images yield null instead of exceptions.
/// </summary>
public interface IArtworkService
{
    /// <summary>When a file has no embedded cover, fall back to cover/folder.jpg|png in its folder.</summary>
    bool UseFolderArtwork { get; set; }
    /// <summary>Memory-only lookup; never touches the disk, so it is safe to call while scrolling.</summary>
    BitmapSource? TryGetCached(string artKey, int size);
    /// <summary>Returns a frozen thumbnail at most <paramref name="size"/> pixels on its long side, or null when there is no usable image.</summary>
    Task<BitmapSource?> GetThumbnailAsync(string audioPath, string? artKey, int size, CancellationToken ct);
    /// <summary>Forget what is known about the folder of this file (call after its tags or folder art changed).</summary>
    void Invalidate(string audioPath);
}

public sealed class ArtworkService : IArtworkService
{
    private const long MaxPixels = 40_000_000; // reject absurd dimensions before decoding (decompression-bomb guard)
    private const int GridCapacity = 600;      // 600 frozen 64 px bitmaps is a few MB

    private readonly IArtworkSource _source;
    private readonly string _cacheDir;
    private readonly ILogger _log;
    private readonly LruCache<(string Key, int Size), BitmapSource> _memory = new(GridCapacity);
    private readonly ConcurrentDictionary<(string Key, int Size), byte> _negative = new();
    private readonly ConcurrentDictionary<(string Key, int Size), Lazy<Task<BitmapSource?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, string?> _folderKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeSlots = new(2);

    public bool UseFolderArtwork { get; set; }

    public ArtworkService(IArtworkSource source, AppPaths paths, ILogger<ArtworkService>? log = null)
        : this(source, Path.Combine(paths.CacheDir, "Artwork"), log) { }

    public ArtworkService(IArtworkSource source, string cacheDir, ILogger? log = null)
    {
        _source = source;
        _cacheDir = cacheDir;
        _log = log ?? NullLogger.Instance;
    }

    public BitmapSource? TryGetCached(string artKey, int size) =>
        _memory.TryGet((artKey, size), out var hit) ? hit : null;

    public async Task<BitmapSource?> GetThumbnailAsync(string audioPath, string? artKey, int size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var key = artKey;
        if (key is null)
        {
            if (!UseFolderArtwork) return null;
            key = await ResolveFolderKeyAsync(audioPath, ct);
            if (key is null) return null;
        }

        if (_memory.TryGet((key, size), out var cached)) return cached;
        if (_negative.ContainsKey((key, size))) return null;

        // One load per (image, size) even when many rows ask at once; each caller still honours its own cancellation.
        // The Lazy matters: ConcurrentDictionary.GetOrAdd may run its factory on several threads at once, and
        // without it every racing caller would start its own decode.
        var entry = _inFlight.GetOrAdd((key, size), _ => new Lazy<Task<BitmapSource?>>(() => Task.Run(() => LoadAsync(audioPath, key, size))));
        var load = entry.Value;
        try
        {
            return await load.WaitAsync(ct);
        }
        finally
        {
            if (load.IsCompleted) _inFlight.TryRemove(new KeyValuePair<(string, int), Lazy<Task<BitmapSource?>>>((key, size), entry));
        }
    }

    public void Invalidate(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir is not null) _folderKeys.TryRemove(dir, out _);
    }

    // ===== loading =====

    private async Task<BitmapSource?> LoadAsync(string audioPath, string key, int size)
    {
        await _decodeSlots.WaitAsync();
        try
        {
            var disk = DiskPath(key, size);
            if (File.Exists(disk) && TryDecodeFile(disk) is { } fromDisk)
            {
                _memory.Set((key, size), fromDisk);
                return fromDisk;
            }

            var blob = key.StartsWith("f:", StringComparison.Ordinal)
                ? await _source.GetFolderArtAsync(audioPath, CancellationToken.None)
                : await _source.GetEmbeddedAsync(audioPath, CancellationToken.None);
            var bitmap = blob is null ? null : Decode(blob.Bytes, size);
            if (bitmap is null)
            {
                _negative[(key, size)] = 0; // a bad or missing cover is examined once per session, not on every scroll
                return null;
            }

            _memory.Set((key, size), bitmap);
            TryWriteDisk(disk, bitmap);
            return bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Could not load artwork for {Path}", audioPath);
            _negative[(key, size)] = 0;
            return null;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Folder art is keyed by its content hash ("f:" prefix), memoized per folder so scrolling never re-reads it.</summary>
    private async Task<string?> ResolveFolderKeyAsync(string audioPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir is null) return null;
        if (_folderKeys.TryGetValue(dir, out var known)) return known;

        var blob = await Task.Run(() => _source.GetFolderArtAsync(audioPath, ct), ct);
        var key = blob is null ? null : "f:" + blob.Hash;
        _folderKeys[dir] = key;
        return key;
    }

    private static BitmapSource? Decode(byte[] bytes, int size)
    {
        try
        {
            using var probe = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;       // fully decoded now, stream can be released
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height) image.DecodePixelWidth = Math.Min(size, width);
            else image.DecodePixelHeight = Math.Min(size, height);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();                                       // required to hand it to the UI thread
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException
                                       or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static BitmapSource? TryDecodeFile(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path), int.MaxValue);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void TryWriteDisk(string path, BitmapSource bitmap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, path, overwrite: true); // readers never see a half-written file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogDebug(ex, "Could not write artwork cache file {Path}", path); // caching is best-effort
        }
    }

    private string DiskPath(string key, int size)
    {
        var safe = key.Replace(':', '_');
        var bucket = safe.Length >= 2 ? safe[..2] : "_";
        return Path.Combine(_cacheDir, size.ToString(), bucket, safe + ".jpg");
    }

    // ===== maintenance =====

    /// <summary>Keeps the disk cache under <paramref name="limitMegabytes"/> by deleting the oldest files first (down to 80 %).</summary>
    public void TrimDiskCache(int limitMegabytes)
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*.jpg", SearchOption.AllDirectories).ToList();
            var limit = limitMegabytes * 1024L * 1024L;
            var total = files.Sum(f => f.Length);
            if (total <= limit) return;

            var target = limit * 8 / 10;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                try { total -= file.Length; file.Delete(); }
                catch (IOException) { /* in use; skip */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Artwork cache trim failed");
        }
    }
}
