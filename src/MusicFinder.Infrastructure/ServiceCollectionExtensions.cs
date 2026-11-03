using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MusicFinder.Domain.Contracts;
using MusicFinder.Infrastructure.Genres;
using MusicFinder.Infrastructure.Indexing;
using MusicFinder.Infrastructure.Metadata;
using MusicFinder.Infrastructure.Persistence;
using MusicFinder.Infrastructure.Persistence.Repositories;
using MusicFinder.Infrastructure.Search;
using MusicFinder.Infrastructure.Settings;

namespace MusicFinder.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMusicFinderInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
        services.AddSingleton<SqliteWriteQueue>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<ILibraryRootRepository, LibraryRootRepository>();
        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IGenreRepository, GenreRepository>();
        services.AddSingleton<IDjSessionRepository, DjSessionRepository>();
        services.AddSingleton<ITrackEventRepository, TrackEventRepository>();
        services.AddSingleton<IPlayHistoryRepository, PlayHistoryRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MusicFinder.Application.Dj.DjSessionService>();
        services.AddSingleton<ISessionService>(sp => sp.GetRequiredService<MusicFinder.Application.Dj.DjSessionService>());
        services.AddSingleton<IPlayHistoryQuery>(sp => sp.GetRequiredService<MusicFinder.Application.Dj.DjSessionService>());
        services.AddSingleton<IImportedPlayRecorder>(sp => sp.GetRequiredService<MusicFinder.Application.Dj.DjSessionService>());
        services.AddSingleton<HistoryImportRepository>();
        services.AddSingleton<IHistoryImportStore>(sp => sp.GetRequiredService<HistoryImportRepository>());
        services.AddSingleton<IHistoryTrackLookup>(sp => sp.GetRequiredService<HistoryImportRepository>());
        services.AddSingleton<MusicFinder.Application.Dj.HistoryTrackMatcher>();
        services.AddSingleton<IHistoryImporter, MusicFinder.Infrastructure.Dj.VirtualDjHistoryImporter>();
        services.AddSingleton<IHistoryImporter, MusicFinder.Infrastructure.Dj.TraktorHistoryImporter>();
        services.AddSingleton<IHistoryImportService, MusicFinder.Application.Dj.HistoryImportService>();
        services.AddSingleton<ITrackGenreRepository, TrackGenreRepository>();
        services.AddSingleton<MusicFinder.Application.Genres.GenreMappingJob>();
        services.AddSingleton<ITrackSearchAliasRepository, TrackSearchAliasRepository>();
        services.AddSingleton<ISearchAliasOverrideRepository, SearchAliasOverrideRepository>();
        services.AddSingleton<MusicFinder.Application.Search.SearchAliasIndexJob>();
        services.AddSingleton<IGenreService, MusicFinder.Application.Genres.GenreService>();
        services.AddSingleton<IDjTagService, MusicFinder.Application.Genres.DjTagService>();
        services.AddSingleton<ICustomTagRepository, CustomTagRepository>();
        services.AddSingleton<GenreTaxonomySeeder>();
        services.AddSingleton<FtsVocabulary>(sp =>
        {
            var vocabulary = new FtsVocabulary(sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<FtsVocabulary>>());
            // Whenever the alias index changed, the word list is read again in the background.
            sp.GetRequiredService<MusicFinder.Application.Search.SearchAliasIndexJob>().Indexed += (_, _) => vocabulary.ReloadInBackground();
            return vocabulary;
        });
        services.AddSingleton<MusicFinder.Application.Search.IVocabularyLookup>(sp => sp.GetRequiredService<FtsVocabulary>());
        services.AddSingleton<SqliteSearchService>();
        services.AddSingleton<ISearchService>(sp => sp.GetRequiredService<SqliteSearchService>());
        services.AddSingleton<IMetadataHealthService, Health.MetadataHealthService>();
        services.AddSingleton<IFileHashService, Duplicates.StreamingFileHasher>();
        services.AddSingleton<IFileHashStore, FileHashRepository>();
        services.AddSingleton<FileOperationJournalRepository>();
        services.AddSingleton<IFileOperationJournal>(sp => sp.GetRequiredService<FileOperationJournalRepository>());
        services.AddSingleton<IRenameSourceReader>(sp => sp.GetRequiredService<FileOperationJournalRepository>());
        services.AddSingleton<FileOperations.SystemFileMover>();
        services.AddSingleton<IFileMover>(sp => sp.GetRequiredService<FileOperations.SystemFileMover>());
        services.AddSingleton<MusicFinder.Application.Organize.IFileProbe>(sp => sp.GetRequiredService<FileOperations.SystemFileMover>());
        services.AddSingleton<ILibraryWriteLock, FileOperations.LibraryWriteLock>();
        services.AddSingleton<IFileOperationService, MusicFinder.Application.Organize.FileOperationService>();
        services.AddSingleton<IDuplicateScanService, MusicFinder.Application.Duplicates.DuplicateScanService>();
        services.AddSingleton<ISongCandidateStore, SongCandidateRepository>();
        services.AddSingleton<ILibraryBackupService>(sp => new Backup.LibraryBackupService(
            sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<ISettingsStore>(), Path.Combine(paths.CacheDir, "Artwork")));
        services.AddSingleton<ILibraryRestoreService, Backup.RestoreService>();
        services.AddSingleton<IProbableDuplicateService, MusicFinder.Application.Duplicates.ProbableDuplicateService>();
        services.AddSingleton<ISearchDiagnostics>(sp => sp.GetRequiredService<SqliteSearchService>());
        services.AddSingleton<ISearchAliasService, MusicFinder.Application.Search.SearchAliasService>();
        services.AddSingleton<ISimilarCandidateSource, SimilarCandidateRepository>();
        services.AddSingleton<ISimilarTracksService, MusicFinder.Application.Similar.SimilarTracksService>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<IArtworkSource, Artwork.ArtworkExtractor>();
        // The preview player is created lazily and touches no audio device until a track is played.
        services.AddSingleton<IPreviewPlayer>(sp => OperatingSystem.IsWindows()
            ? new Audio.WasapiPreviewPlayer(sp.GetService<Microsoft.Extensions.Logging.ILogger<Audio.WasapiPreviewPlayer>>())
            : new Audio.NullPreviewPlayer());
        services.AddSingleton<IPreviewOutputDevices>(_ => OperatingSystem.IsWindows() ? new Audio.MmPreviewOutputDevices() : new Audio.NoPreviewDevices());
        services.AddSingleton<IMetadataWriter, TagLibMetadataWriter>();
        services.AddSingleton<ITagEditingService, MusicFinder.Application.Tags.TagEditingService>();
        services.AddSingleton<ISettingsStore>(new JsonSettingsStore(paths.SettingsPath));
        services.AddSingleton<IIndexer>(sp => new LibraryScanner(
            sp.GetRequiredService<ILibraryRootRepository>(),
            sp.GetRequiredService<IFolderRepository>(),
            sp.GetRequiredService<ITrackRepository>(),
            sp.GetRequiredService<IMetadataReader>(),
            sp.GetService<ILogger<LibraryScanner>>())
        {
            ReaderConcurrency = sp.GetRequiredService<ISettingsStore>().Load().ReaderConcurrency
        });
        AddIdentification(services, paths);
        AddAnalysis(services);
        return services;
    }

    /// <summary>BPM and key analysis: estimates from the audio, kept apart from the tags until the user writes them.</summary>
    private static void AddAnalysis(IServiceCollection services)
    {
        // A factory, not the type: the router also has a constructor that takes decoders, which the container would try to fill with itself.
        services.AddSingleton<IAudioDecoder>(_ => new Audio.AudioDecoderRouter());
        services.AddSingleton<ITrackAnalysisStore, TrackAnalysisRepository>();
        services.AddSingleton<IAudioAnalysisService, MusicFinder.Application.Analysis.AudioAnalysisService>();
    }

    /// <summary>Online identification (M5). Nothing here touches the network until the user turns it on and accepts the explanation.</summary>
    private static void AddIdentification(IServiceCollection services, AppPaths paths)
    {
        services.TryAddSingleton(new MusicFinder.Application.Identification.AppInfo("MusicFinder",
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"));
        services.AddSingleton<MusicFinder.Application.Identification.IRateLimiter>(sp =>
            new MusicFinder.Application.Identification.RateLimiter(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new Identification.IdentificationHttp(
            sp.GetRequiredService<MusicFinder.Application.Identification.IRateLimiter>(), sp.GetRequiredService<TimeProvider>(),
            innerFactory: null, sp.GetService<ILogger<Identification.IdentificationHttp>>()));
        services.AddSingleton<IIdentificationCache, IdentificationCacheRepository>();
        services.AddSingleton<Identification.IProcessRunner, Identification.WindowsProcessRunner>();
        services.AddSingleton(sp => new Identification.FpcalcLocator(sp.GetRequiredService<ISettingsStore>(),
            toolsDirectory: Path.Combine(paths.DataDir, Identification.FpcalcLocator.ToolsFolderName)));
        services.AddSingleton(sp => new Identification.FpcalcInstaller(
            Path.Combine(paths.DataDir, Identification.FpcalcLocator.ToolsFolderName), sp.GetRequiredService<Identification.IProcessRunner>()));
        services.AddSingleton<MusicFinder.Application.Identification.AcoustIdKeyChecker>();
        services.AddSingleton<IFingerprintService, Identification.ChromaprintFingerprintService>();
        services.AddSingleton<IMusicBrainzClient, Identification.MusicBrainzClient>();
        services.AddSingleton<IAcoustIdClient, Identification.AcoustIdClient>();
        services.AddSingleton<ICoverArtClient, Identification.CoverArtClient>();
        services.AddSingleton<IIdentificationService, MusicFinder.Application.Identification.IdentificationService>();
    }
}
