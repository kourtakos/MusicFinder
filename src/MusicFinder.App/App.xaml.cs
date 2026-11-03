using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MusicFinder.App.Services;
using MusicFinder.App.ViewModels;
using MusicFinder.Domain.Contracts;
using MusicFinder.Infrastructure;
using MusicFinder.Infrastructure.Genres;
using MusicFinder.Infrastructure.Persistence;
using Serilog;

namespace MusicFinder.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostic escape hatch for machines/sessions where GPU rendering misbehaves.
        if (e.Args.Contains("--software", StringComparer.OrdinalIgnoreCase))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var portable = e.Args.Contains("--portable", StringComparer.OrdinalIgnoreCase)
                       || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));
        var paths = AppPaths.Resolve(portable);
        Directory.CreateDirectory(paths.LogDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogDir, "musicfinder-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "MusicFinder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // A restore prepared in the last session is swapped in now, before anything has the database open.
        string? restoreMessage = null;
        try
        {
            var restore = MusicFinder.Infrastructure.Backup.RestoreFinalizer.TryApply(paths);
            if (restore is not null) { Log.Information("Restore: {Message}", restore.Message); restoreMessage = restore.Message; }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Applying the pending restore failed");
            restoreMessage = Localization.Loc.T("The pending restore could not be applied: {0}", ex.Message);
        }

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger, dispose: true);
            ConfigureServices(builder.Services, paths);
            _host = builder.Build();

            // The language is chosen once, before any window or view model produces text.
            Localization.Loc.Initialize(_host.Services.GetRequiredService<ISettingsStore>().Load().Language);
            Localization.UiTranslator.Install();
            MusicFinder.Application.Dj.RelativeTime.Texts = new(Localization.Loc.T("just now"), Localization.Loc.T("{0} min ago"), Localization.Loc.T("{0} h ago"),
                Localization.Loc.T("{0} h {1:00} min ago"), Localization.Loc.T("yesterday {0}"));

            // Schema + FTS5 check before anything touches the database.
            _host.Services.GetRequiredService<MigrationRunner>().Migrate();
            await _host.Services.GetRequiredService<GenreTaxonomySeeder>().EnsureSeededAsync();

            Services.UiZoom.Initialize(_host.Services.GetRequiredService<ISettingsStore>());    // text size from last time, and the zoom shortcuts
            var appSettings = _host.Services.GetRequiredService<ISettingsStore>().Load();
            var artwork = _host.Services.GetRequiredService<Services.Artwork.ArtworkService>();
            artwork.UseFolderArtwork = appSettings.UseFolderArtwork;
            _ = Task.Run(() => artwork.TrimDiskCache(appSettings.ArtworkCacheLimitMb)); // keep the thumbnail cache bounded

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Show();
            if (restoreMessage is not null) MessageBox.Show(window, restoreMessage, Localization.Loc.T("Restore"), MessageBoxButton.OK, MessageBoxImage.Information);
            _host.Services.GetRequiredService<Services.Hotkeys.QuickSearchService>().Start(); // global shortcut for the quick-search window
            _host.Services.GetRequiredService<SearchIndexCoordinator>().Start();            // smart Greek search index, built in the background

            var main = _host.Services.GetRequiredService<MainViewModel>();
            await _host.Services.GetRequiredService<MusicFinder.Domain.Contracts.ISessionService>().InitializeAsync(default);
            await main.InitializeAsync();
            _host.Services.GetRequiredService<HistoryAutoImporter>().Start();               // VirtualDJ/Traktor history, read-only, in the background

            // The first time only: a short tour and the song-recognition setup. Not modal, so the library is usable behind it.
            if (!_host.Services.GetRequiredService<ISettingsStore>().Load().FirstRunCompleted)
            {
                var firstRun = _host.Services.GetRequiredService<FirstRunViewModel>();
                firstRun.Open(toRecognition: false);
                _host.Services.GetRequiredService<IUserDialogs>().ShowFirstRun(firstRun);
            }

            // Pick up changes made while the app was closed (unchanged files are skipped cheaply).
            await _host.Services.GetRequiredService<LibraryRootService>().RescanAllAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(Localization.Loc.T("MusicFinder could not start:\n\n{0}\n\nLogs: {1}", ex.Message, paths.LogDir),
                "MusicFinder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddMusicFinderInfrastructure(paths);

        services.AddSingleton<IUserDialogs, UserDialogs>();
        services.AddSingleton<Services.Dragging.IFileDragSource, Services.Dragging.WindowsFileDragSource>();
        services.AddSingleton<Services.Dragging.TrackDragService>();
        services.AddSingleton<Services.Hotkeys.IGlobalHotkey, Services.Hotkeys.Win32GlobalHotkey>();
        services.AddSingleton<ScanCoordinator>();
        services.AddSingleton<SearchIndexCoordinator>();
        services.AddSingleton<LibraryRootService>();
        services.AddSingleton<Services.Artwork.ArtworkService>();
        services.AddSingleton<Services.Artwork.IArtworkService>(sp => sp.GetRequiredService<Services.Artwork.ArtworkService>());

        services.AddSingleton<FolderTreeViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<IdentificationSettingsViewModel>();
        services.AddSingleton<MusicFinder.Application.Analysis.AnalysisRunner>();
        services.AddSingleton<AnalysisSettingsViewModel>();
        services.AddSingleton<IAppRestarter, AppRestarter>();
        services.AddSingleton<BackupSettingsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TagsViewModel>();
        services.AddSingleton<HealthViewModel>();
        services.AddSingleton<RenameViewModelFactory>();
        services.AddSingleton<SessionViewModel>();
        services.AddSingleton<PreviewViewModel>();
        services.AddSingleton<IDownloadsFolder, UserDownloadsFolder>();
        services.AddSingleton<FirstRunViewModel>();
        services.AddSingleton<HistoryImportSettingsViewModel>();
        services.AddSingleton<HistoryAutoImporter>();
        services.AddSingleton<QuickSearchViewModel>();
        services.AddSingleton<Views.QuickSearchWindow>();
        services.AddSingleton<Services.Hotkeys.IQuickSearchPresenter>(sp => sp.GetRequiredService<Views.QuickSearchWindow>());
        services.AddSingleton<Services.Hotkeys.QuickSearchService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try { _host.Services.GetService<HistoryAutoImporter>()?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "History importer shutdown failed"); }
            // A preview lets go of its audio file and output first.
            try { _host.Services.GetService<PreviewViewModel>()?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Preview shutdown failed"); }
            // Unfinished 20-second windows become "interrupted" (never "played") and the session is closed.
            try { _host.Services.GetService<MusicFinder.Domain.Contracts.ISessionService>()?.ShutdownAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warning(ex, "Session shutdown failed"); }
            _host.Services.GetService<ScanCoordinator>()?.Dispose();
            _host.Services.GetService<SearchIndexCoordinator>()?.Dispose();
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
