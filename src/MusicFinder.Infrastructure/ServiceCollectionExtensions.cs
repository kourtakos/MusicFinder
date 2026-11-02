using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<ISearchDiagnostics>(sp => sp.GetRequiredService<SqliteSearchService>());
        services.AddSingleton<ISearchAliasService, MusicFinder.Application.Search.SearchAliasService>();
        services.AddSingleton<ISimilarCandidateSource, SimilarCandidateRepository>();
        services.AddSingleton<ISimilarTracksService, MusicFinder.Application.Similar.SimilarTracksService>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<IArtworkSource, Artwork.ArtworkExtractor>();
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
        return services;
    }
}
