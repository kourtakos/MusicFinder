using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using AppClass = MusicFinder.App.App;
using MusicFinder.App;
using MusicFinder.App.ViewModels;
using MusicFinder.Application.Genres;
using MusicFinder.Application.Services;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Infrastructure;
using MusicFinder.Infrastructure.Genres;
using MusicFinder.Infrastructure.Persistence;

namespace MusicFinder.UiSmokeTests;

/// <summary>
/// One shared STA thread owns the WPF Application (only one is allowed per process). Tests marshal onto it,
/// so every test runs against the real App.xaml resources and the real composition root.
/// </summary>
public static class Ui
{
    private static readonly Lazy<Dispatcher> Dispatcher_ = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new AppClass();
            app.InitializeComponent(); // loads Themes/*.xaml exactly as in production
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "UiSmoke STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static Task RunAsync(Func<Task> test) => Dispatcher_.Value.InvokeAsync(test).Task.Unwrap();

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out waiting for: {what ?? "condition"}");
            await Task.Delay(25);
        }
    }

    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}

/// <summary>A real window + view models on a throwaway database seeded with a few tracks.</summary>
public sealed class UiTestApp : IAsyncDisposable
{
    private readonly string _dir;
    private readonly ServiceProvider _services;

    public MainWindow Window { get; }
    public MainViewModel Main { get; }
    public ISettingsStore Settings => _services.GetRequiredService<ISettingsStore>();

    private UiTestApp(string dir, ServiceProvider services, MainWindow window, MainViewModel main)
    {
        _dir = dir;
        _services = services;
        Window = window;
        Main = main;
    }

    /// <summary>The folder holding real audio files when created with <see cref="CreateWithFilesAsync"/>.</summary>
    public string MusicDir => Path.Combine(_dir, "Music");
    public T Service<T>() where T : notnull => _services.GetRequiredService<T>();

    public static async Task<UiTestApp> CreateAsync(bool seed = true, Action<IServiceCollection>? configure = null)
    {
        var (dir, provider) = await BuildAsync(configure);
        if (seed) await SeedAsync(provider);

        var window = provider.GetRequiredService<MainWindow>();
        var main = provider.GetRequiredService<MainViewModel>();
        return new UiTestApp(dir, provider, window, main);
    }

    /// <summary>
    /// Real tagged mp3 files (with an embedded cover), indexed by the real scanner, so the tag editor can write to them.
    /// Each file is (name, title, artist, genre); all share the album "Original Album".
    /// </summary>
    public static async Task<UiTestApp> CreateWithFilesAsync(Action<IServiceCollection>? configure = null,
        params (string Name, string Title, string Artist, string Genre)[] files)
    {
        var (dir, provider) = await BuildAsync(configure);
        var music = Path.Combine(dir, "Music");
        Directory.CreateDirectory(music);

        foreach (var (name, title, artist, genre) in files)
        {
            var path = Path.Combine(music, name);
            using (var fs = File.Create(path))
            {
                var frame = new byte[417]; // MPEG-1 Layer III, 128 kbps, 44.1 kHz
                frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90;
                for (var i = 0; i < 30; i++) fs.Write(frame);
            }
            using var f = TagLib.File.Create(path);
            f.Tag.Title = title;
            f.Tag.Performers = [artist];
            f.Tag.Genres = [genre];
            f.Tag.Album = "Original Album";
            f.Save();
        }

        var root = await provider.GetRequiredService<ILibraryRootRepository>().AddAsync(music);
        await provider.GetRequiredService<IIndexer>().ScanRootAsync(root.Id, null, default);
        await provider.GetRequiredService<GenreMappingJob>().RunAsync();

        var window = provider.GetRequiredService<MainWindow>();
        var main = provider.GetRequiredService<MainViewModel>();
        return new UiTestApp(dir, provider, window, main);
    }

    private static async Task<(string Dir, ServiceProvider Provider)> BuildAsync(Action<IServiceCollection>? configure)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-ui-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Resolve(portable: true, baseDirectory: dir);
        var services = new ServiceCollection();
        services.AddLogging();
        AppClass.ConfigureServices(services, paths);
        configure?.Invoke(services); // tests can replace services, e.g. the dialog service
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<MigrationRunner>().Migrate();
        await provider.GetRequiredService<GenreTaxonomySeeder>().EnsureSeededAsync();
        return (dir, provider);
    }

    private static async Task SeedAsync(IServiceProvider sp)
    {
        var roots = sp.GetRequiredService<ILibraryRootRepository>();
        var folders = sp.GetRequiredService<IFolderRepository>();
        var tracks = sp.GetRequiredService<ITrackRepository>();

        var root = await roots.AddAsync(@"C:\UiTestMusic");
        var top = await folders.UpsertAsync(root.Id, null, @"C:\UiTestMusic", "UiTestMusic");
        var disco = await folders.UpsertAsync(root.Id, top, @"C:\UiTestMusic\Disco", "Disco");

        Track T(long folder, string file, string title, string artist, string genre, double bpm) => new()
        {
            FolderId = folder, FullPath = $@"C:\UiTestMusic\{file}", FileName = Path.GetFileName(file), Extension = ".mp3",
            FileSize = 5_000_000, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
            Title = title, Artist = artist, GenreRaw = genre, Bpm = bpm, MusicalKey = "8A", DurationMs = 200_000,
            NormalizedText = TextNormalizer.BuildSearchText(Path.GetFileName(file), title, artist, genre)
        };

        await tracks.UpsertBatchAsync(
        [
            T(disco, @"Disco\Chic - Le Freak.mp3", "Le Freak", "Chic", "Disco;Funk", 120),
            T(disco, @"Disco\Chic - Good Times.mp3", "Good Times", "Chic", "Disco", 119),
            T(top, @"Χάρις Αλεξίου - Σ' αγαπώ.mp3", "Σ' αγαπώ", "Χάρις Αλεξίου", "Λαϊκά", 100)
        ]);
        await folders.RecomputeCountsAsync(root.Id);
        await sp.GetRequiredService<GenreMappingJob>().RunAsync(); // map the raw genres onto the taxonomy
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await _services.DisposeAsync();
        SqliteConnectionFactory.ReleaseAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public static class BindingAudit
{
    /// <summary>
    /// Walks the visual and logical trees and reports every binding whose status is a path/update error.
    /// Deterministic alternative to WPF trace listeners. Popups and context menus are only covered while open.
    /// </summary>
    public static List<string> FindBroken(DependencyObject root)
    {
        var broken = new List<string>();
        var seen = new HashSet<DependencyObject>();

        void Visit(DependencyObject d)
        {
            if (!seen.Add(d)) return;

            var values = d.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                if (values.Current.Value is not System.Windows.Data.BindingExpressionBase expr) continue;
                if (expr.Status is System.Windows.Data.BindingStatus.PathError
                    or System.Windows.Data.BindingStatus.UpdateSourceError
                    or System.Windows.Data.BindingStatus.UpdateTargetError)
                {
                    var path = (expr as System.Windows.Data.BindingExpression)?.ParentBinding.Path?.Path;
                    broken.Add($"{d.GetType().Name}.{values.Current.Property.Name} -> '{path}' [{expr.Status}]");
                }
            }

            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    Visit(VisualTreeHelper.GetChild(d, i));

            foreach (var logical in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                Visit(logical);
        }

        Visit(root);
        return broken;
    }
}
