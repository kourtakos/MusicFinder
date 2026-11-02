using MusicFinder.Application.Organize;
using MusicFinder.Domain.Contracts;

namespace MusicFinder.Infrastructure.FileOperations;

/// <summary>The real file system for renames. <see cref="File.Move(string,string,bool)"/> is always called without overwrite.</summary>
public sealed class SystemFileMover : IFileMover, IFileProbe
{
    public bool Exists(string path) => File.Exists(path);

    public bool ExistsExact(string path)
    {
        if (!File.Exists(path)) return false;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir)) return true;
        try
        {
            // File.Exists ignores case; the folder listing shows the spelling that is really stored.
            return Directory.EnumerateFiles(dir, name).Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Move(string from, string to) => File.Move(from, to, overwrite: false);

    public void EnsureFolder(string directory) => Directory.CreateDirectory(directory);
}

/// <summary>One holder at a time: a scan or a batch of file operations.</summary>
public sealed class LibraryWriteLock : ILibraryWriteLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IDisposable? TryEnter() => _gate.Wait(0) ? new Releaser(_gate) : null;

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }
}
