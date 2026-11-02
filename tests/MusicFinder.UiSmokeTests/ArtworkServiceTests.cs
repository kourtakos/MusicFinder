using MusicFinder.App.Services.Artwork;

namespace MusicFinder.UiSmokeTests;

public class ArtworkServiceTests
{
    private static ArtworkService Service(MusicFinder.Domain.Contracts.IArtworkSource source, string dir) => new(source, dir);

    private static int FileCount(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public Task Thumbnail_IsResized_Frozen_AndCachedInMemoryAndOnDisk() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var png = Images.Png(300, 300);
        var source = new FakeSource { Embedded = _ => Images.Blob(png, "k1") };
        var service = Service(source, dir.Path);

        // Called from the thread pool on purpose: decoding must not depend on the UI thread.
        var bitmap = await Task.Run(() => service.GetThumbnailAsync(@"C:\m\a.mp3", "k1", 64, default));

        Assert.NotNull(bitmap);
        Assert.Equal(64, bitmap!.PixelWidth);
        Assert.Equal(64, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);                       // frozen => safe to hand to the UI thread
        Assert.Equal(1, FileCount(dir.Path));                // thumbnail written to the disk cache
        Assert.Same(bitmap, service.TryGetCached("k1", 64)); // and kept in the memory LRU
        Assert.Null(service.TryGetCached("k1", 96));         // other sizes are separate entries

        Assert.Same(bitmap, await service.GetThumbnailAsync(@"C:\m\other.mp3", "k1", 64, default));
        Assert.Equal(1, source.EmbeddedCalls);               // the album's other tracks reuse it without opening a file
    });

    [Fact]
    public Task AFreshServiceInstance_ReusesTheDiskCache_WithoutOpeningTheAudioFile() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var first = new FakeSource { Embedded = _ => Images.Blob(Images.Png(200, 200), "k1") };
        await Service(first, dir.Path).GetThumbnailAsync(@"C:\m\a.mp3", "k1", 64, default);

        var afterRestart = Service(new ForbiddenSource(), dir.Path); // would throw if the source were consulted
        var bitmap = await afterRestart.GetThumbnailAsync(@"C:\m\a.mp3", "k1", 64, default);

        Assert.NotNull(bitmap);
        Assert.Equal(64, bitmap!.PixelWidth);
        Assert.True(bitmap.IsFrozen);
    });

    [Theory]
    [InlineData(300, 150, 64, 32)]   // landscape: long side becomes 64
    [InlineData(150, 300, 32, 64)]   // portrait
    [InlineData(20, 20, 20, 20)]     // never upscaled
    public Task Aspect_IsPreserved_AndSmallImagesAreNotUpscaled(int width, int height, int expectedWidth, int expectedHeight) => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var source = new FakeSource { Embedded = _ => Images.Blob(Images.Png(width, height), "k") };

        var bitmap = await Service(source, dir.Path).GetThumbnailAsync(@"C:\m\a.mp3", "k", 64, default);

        Assert.Equal((expectedWidth, expectedHeight), (bitmap!.PixelWidth, bitmap.PixelHeight));
    });

    [Fact]
    public Task MalformedImage_YieldsNullWithoutThrowing_AndIsExaminedOnlyOnce() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var source = new FakeSource { Embedded = _ => Images.Blob([1, 2, 3, 4, 5, 6, 7, 8], "bad") };
        var service = Service(source, dir.Path);

        for (var i = 0; i < 5; i++)
            Assert.Null(await service.GetThumbnailAsync($@"C:\m\{i}.mp3", "bad", 64, default));

        Assert.Equal(1, source.EmbeddedCalls);   // a corrupt cover is not re-decoded on every scroll
        Assert.Equal(0, FileCount(dir.Path));
    });

    [Fact]
    public Task TruncatedPng_NeverThrows() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var png = Images.Png(200, 200);
        var source = new FakeSource { Embedded = _ => Images.Blob(png[..(png.Length / 3)], "cut") };

        // WPF's PNG decoder is lenient and renders what it could read, like a browser would. Either outcome
        // (a partial frozen image or null) is acceptable; an exception is not.
        var bitmap = await Service(source, dir.Path).GetThumbnailAsync(@"C:\m\a.mp3", "cut", 64, default);
        if (bitmap is not null) Assert.True(bitmap.IsFrozen);
    });

    [Fact]
    public Task FileWithoutACover_IsAskedAboutOnce() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var source = new FakeSource(); // Embedded returns null
        var service = Service(source, dir.Path);

        for (var i = 0; i < 4; i++) Assert.Null(await service.GetThumbnailAsync(@"C:\m\a.mp3", "none", 64, default));
        Assert.Equal(1, source.EmbeddedCalls);
    });

    [Fact]
    public Task ConcurrentRequestsForTheSameImage_ShareOneLoad() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var png = Images.Png(300, 300);
        var source = new FakeSource { Delay = TimeSpan.FromMilliseconds(150), Embedded = _ => Images.Blob(png, "shared") };
        var service = Service(source, dir.Path);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => service.GetThumbnailAsync($@"C:\m\{i}.mp3", "shared", 64, default))));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(1, source.EmbeddedCalls);
        Assert.Equal(1, FileCount(dir.Path));
    });

    [Fact]
    public Task PreCancelledRequest_ThrowsWithoutTouchingTheSource() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var source = new FakeSource { Embedded = _ => Images.Blob(Images.Png(100, 100), "k") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(source, dir.Path).GetThumbnailAsync(@"C:\m\a.mp3", "k", 64, cts.Token));
        Assert.Equal(0, source.EmbeddedCalls);
    });

    [Fact]
    public Task CancellingOneWaiter_DoesNotCancelTheSharedLoad() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var png = Images.Png(200, 200);
        var source = new FakeSource { Delay = TimeSpan.FromMilliseconds(200), Embedded = _ => Images.Blob(png, "k") };
        var service = Service(source, dir.Path);
        using var cts = new CancellationTokenSource();

        var cancelled = Task.Run(() => service.GetThumbnailAsync(@"C:\m\a.mp3", "k", 64, cts.Token));
        var patient = Task.Run(() => service.GetThumbnailAsync(@"C:\m\b.mp3", "k", 64, default));
        await Task.Delay(50);
        cts.Cancel(); // a scrolled-away row gives up...

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.NotNull(await patient);                // ...but the row that still needs the cover gets it
        Assert.Equal(1, source.EmbeddedCalls);
    });

    [Fact]
    public Task FolderArt_IsOnlyUsedWhenEnabled_AndMemoizedPerFolder() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var png = Images.Png(240, 240);
        var source = new FakeSource { Folder = _ => Images.Blob(png, "abc") };
        var service = Service(source, dir.Path);

        // Disabled (the default): no key, no folder access at all.
        Assert.Null(await service.GetThumbnailAsync(@"C:\m\album\a.mp3", null, 64, default));
        Assert.Equal(0, source.FolderCalls);

        service.UseFolderArtwork = true;
        var first = await service.GetThumbnailAsync(@"C:\m\album\a.mp3", null, 64, default);
        Assert.NotNull(first);
        var callsAfterFirst = source.FolderCalls;

        // Another track in the same folder: answered from the memo + memory cache, no more folder reads.
        var second = await service.GetThumbnailAsync(@"C:\m\album\b.mp3", null, 64, default);
        Assert.Same(first, second);
        Assert.Equal(callsAfterFirst, source.FolderCalls);
        Assert.Equal(0, source.EmbeddedCalls);

        // After the folder changed (e.g. a new cover.jpg was dropped in), invalidation makes it look again.
        service.Invalidate(@"C:\m\album\a.mp3");
        await service.GetThumbnailAsync(@"C:\m\album\c.mp3", null, 64, default);
        Assert.True(source.FolderCalls > callsAfterFirst);
    });

    [Fact]
    public Task FolderWithoutArt_ReturnsNull_AndIsNotRescannedForEveryTrack() => Ui.RunAsync(async () =>
    {
        using var dir = new TempDir();
        var source = new FakeSource(); // no folder art
        var service = Service(source, dir.Path);
        service.UseFolderArtwork = true;

        for (var i = 0; i < 5; i++) Assert.Null(await service.GetThumbnailAsync($@"C:\m\bare\{i}.mp3", null, 64, default));
        Assert.Equal(1, source.FolderCalls);
    });

    [Fact]
    public Task TrimDiskCache_DeletesTheOldestFilesFirst_DownToEightyPercent() => Ui.RunAsync(() =>
    {
        using var dir = new TempDir();
        var bucket = Path.Combine(dir.Path, "64", "ab");
        Directory.CreateDirectory(bucket);
        for (var i = 0; i < 5; i++)
        {
            var file = Path.Combine(bucket, $"img{i}.jpg");
            File.WriteAllBytes(file, new byte[300 * 1024]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-(10 - i))); // img0 oldest ... img4 newest
        }

        new ArtworkService(new ForbiddenSource(), dir.Path).TrimDiskCache(limitMegabytes: 1); // 1.5 MB on disk > 1 MB limit

        var left = Directory.GetFiles(bucket).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["img3.jpg", "img4.jpg"], left);   // trimmed to <= 0.8 MB, oldest gone first
        return Task.CompletedTask;
    });

    [Fact]
    public Task TrimDiskCache_DoesNothingUnderTheLimit_AndToleratesAMissingFolder() => Ui.RunAsync(() =>
    {
        using var dir = new TempDir();
        var bucket = Path.Combine(dir.Path, "64", "ab");
        Directory.CreateDirectory(bucket);
        File.WriteAllBytes(Path.Combine(bucket, "a.jpg"), new byte[1024]);

        new ArtworkService(new ForbiddenSource(), dir.Path).TrimDiskCache(limitMegabytes: 200);
        Assert.Single(Directory.GetFiles(bucket));

        new ArtworkService(new ForbiddenSource(), Path.Combine(dir.Path, "does-not-exist")).TrimDiskCache(1); // no throw
        return Task.CompletedTask;
    });

    [Fact]
    public void LruCache_EvictsTheLeastRecentlyUsedEntry()
    {
        var cache = new LruCache<string, int>(3);
        cache.Set("a", 1); cache.Set("b", 2); cache.Set("c", 3);
        Assert.True(cache.TryGet("a", out _));   // touching "a" makes "b" the oldest
        cache.Set("d", 4);

        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out var a) && a == 1);
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(3, cache.Count);

        cache.Set("a", 10); // overwrite keeps one entry
        Assert.Equal(3, cache.Count);
        Assert.True(cache.TryGet("a", out var updated) && updated == 10);

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
