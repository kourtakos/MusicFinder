using System.Diagnostics;
using System.Security.Cryptography;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.Infrastructure.Duplicates;

/// <summary>
/// SHA-256 of a file read in 1 MB chunks. The file is opened for shared reading (a DJ program may have it open), the read is
/// sequential, cancellation is checked after every chunk, and an optional limit on bytes per second keeps the scan from taking the disk.
/// </summary>
public sealed class StreamingFileHasher : IFileHashService
{
    private const int ChunkSize = 1 << 20;

    public async Task<string> HashAsync(string path, long maxBytesPerSecond, IProgress<long>? progress, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0
        });

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        var clock = Stopwatch.StartNew();
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            total += read;
            progress?.Report(total);

            if (maxBytesPerSecond > 0)
            {
                var dueMs = total * 1000.0 / maxBytesPerSecond;
                var wait = dueMs - clock.Elapsed.TotalMilliseconds;
                if (wait >= 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            }
            ct.ThrowIfCancellationRequested();
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
