using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicFinder.App.Services.Artwork;

namespace MusicFinder.App.Controls;

/// <summary>
/// Cover thumbnail for grid rows and the details panel. DataGrid recycles row containers, so every change of
/// <see cref="AudioPath"/> or <see cref="ArtKey"/> cancels the previous request and clears the picture first,
/// which keeps a recycled row from ever showing the previous track's cover.
/// </summary>
public partial class ArtworkThumbnail : UserControl
{
    /// <summary>Waiting briefly lets fast scrolling skip rows that are already gone, so they cost nothing.</summary>
    private const int DebounceMs = 40;

    public static readonly DependencyProperty AudioPathProperty = Register(nameof(AudioPath), typeof(string));
    public static readonly DependencyProperty ArtKeyProperty = Register(nameof(ArtKey), typeof(string));
    public static readonly DependencyProperty ServiceProperty = Register(nameof(Service), typeof(IArtworkService));

    public static readonly DependencyProperty PixelSizeProperty =
        DependencyProperty.Register(nameof(PixelSize), typeof(int), typeof(ArtworkThumbnail),
            new PropertyMetadata(64, (d, _) => ((ArtworkThumbnail)d).ScheduleReload()));

    public string? AudioPath { get => (string?)GetValue(AudioPathProperty); set => SetValue(AudioPathProperty, value); }
    public string? ArtKey { get => (string?)GetValue(ArtKeyProperty); set => SetValue(ArtKeyProperty, value); }
    public IArtworkService? Service { get => (IArtworkService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public int PixelSize { get => (int)GetValue(PixelSizeProperty); set => SetValue(PixelSizeProperty, value); }

    /// <summary>The bitmap currently shown (null while loading or when there is none). Exposed for tests.</summary>
    public BitmapSource? Shown => Picture.Source as BitmapSource;

    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _reloadQueued;

    public ArtworkThumbnail()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Placeholder.FontSize = Math.Max(12, Math.Min(e.NewSize.Width, e.NewSize.Height) * 0.4);
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private static DependencyProperty Register(string name, Type type) =>
        DependencyProperty.Register(name, type, typeof(ArtworkThumbnail),
            new PropertyMetadata(null, (d, _) => ((ArtworkThumbnail)d).ScheduleReload()));

    /// <summary>A recycled row changes several properties at once; coalesce them into one reload.</summary>
    private void ScheduleReload()
    {
        // Clear and cancel immediately so a stale cover can never linger while the new one is pending.
        _cts?.Cancel();
        Show(null);
        if (_reloadQueued) return;
        _reloadQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _reloadQueued = false;
            Reload();
        });
    }

    private void Reload()
    {
        var service = Service;
        var path = AudioPath;
        var key = ArtKey;
        var size = PixelSize;
        var generation = ++_generation;
        if (service is null || string.IsNullOrEmpty(path)) return;

        // A memory hit applies at once: scrolling back over covers never flickers.
        if (key is not null && service.TryGetCached(key, size) is { } hit)
        {
            Show(hit);
            return;
        }
        if (key is null && !service.UseFolderArtwork) return; // no embedded cover and no folder-art fallback: nothing to load

        var cts = _cts = new CancellationTokenSource();
        _ = LoadAsync(service, path, key, size, generation, cts.Token);
    }

    private async Task LoadAsync(IArtworkService service, string path, string? key, int size, int generation, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMs, ct);
            var bitmap = await service.GetThumbnailAsync(path, key, size, ct);
            // Apply only if this request is still the current one (frozen bitmaps are safe to hand over).
            if (!ct.IsCancellationRequested && generation == _generation) Show(bitmap);
        }
        catch (OperationCanceledException) { }
    }

    private void Show(BitmapSource? bitmap)
    {
        Picture.Source = bitmap;
        Placeholder.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
