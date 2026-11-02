using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicFinder.App.Services.Artwork;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UiSmokeTests;

internal static class Images
{
    /// <summary>Renders a gradient PNG at runtime so no binary fixture is committed. Must run on the UI thread.</summary>
    public static byte[] Png(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new LinearGradientBrush(Colors.MediumPurple, Colors.DarkSlateBlue, 45), null, new Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static ArtworkBlob Blob(byte[] bytes, string hash) => new(bytes, "image/png", hash);
}

/// <summary>Scriptable artwork source that counts how often the files would have been opened.</summary>
internal sealed class FakeSource : IArtworkSource
{
    private int _embedded, _folder;
    public int EmbeddedCalls => _embedded;
    public int FolderCalls => _folder;
    public TimeSpan Delay { get; set; }
    public Func<string, ArtworkBlob?> Embedded { get; set; } = _ => null;
    public Func<string, ArtworkBlob?> Folder { get; set; } = _ => null;

    public async Task<ArtworkBlob?> GetEmbeddedAsync(string audioPath, CancellationToken ct)
    {
        Interlocked.Increment(ref _embedded);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, CancellationToken.None);
        return Embedded(audioPath);
    }

    public async Task<ArtworkBlob?> GetFolderArtAsync(string audioPath, CancellationToken ct)
    {
        Interlocked.Increment(ref _folder);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, CancellationToken.None);
        return Folder(audioPath);
    }
}

/// <summary>A source that must never be consulted: proves a request was answered from a cache.</summary>
internal sealed class ForbiddenSource : IArtworkSource
{
    public Task<ArtworkBlob?> GetEmbeddedAsync(string audioPath, CancellationToken ct) => throw new InvalidOperationException("source was consulted");
    public Task<ArtworkBlob?> GetFolderArtAsync(string audioPath, CancellationToken ct) => throw new InvalidOperationException("source was consulted");
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mf-art-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

/// <summary>Per-key scripted artwork service for control tests.</summary>
internal sealed class FakeArtworkService : IArtworkService
{
    private int _requests;
    public bool UseFolderArtwork { get; set; }
    public int Requests => _requests;
    public Dictionary<string, (TimeSpan Delay, BitmapSource? Image)> Script { get; } = [];
    public Dictionary<string, BitmapSource> Cached { get; } = [];

    public BitmapSource? TryGetCached(string artKey, int size) => Cached.GetValueOrDefault(artKey);

    public async Task<BitmapSource?> GetThumbnailAsync(string audioPath, string? artKey, int size, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var (delay, image) = Script.GetValueOrDefault(artKey ?? "", (TimeSpan.Zero, null));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, CancellationToken.None); // ignores ct on purpose: the control must still discard stale results
        return image;
    }

    public void Invalidate(string audioPath) { }
}
