using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Api;
using Jularr.Web.Features.Acquisition.AniListAutoMonitor;
using Jularr.Web.Features.Acquisition.Backup;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Naming;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Policy;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The anime acquisition pipeline wired like Program.cs, on an isolated data directory, library
/// root and database, with fakes for Prowlarr and SABnzbd. Sonarr is not configured, so Sonarr
/// ownership comes from the persisted ownership state. <see cref="RestartAsync"/> builds new
/// service instances on the same files, as after a process restart.
/// </summary>
internal sealed class AnimeAcquisitionEnvironment : IAsyncDisposable
{
    public const string AnimeKey = "frieren";
    public const string SimpleProfileId = "simple";

    private ServiceProvider services;

    private AnimeAcquisitionEnvironment(string tempRoot, DbContextOptions<AppDbContext> options, AppDbContext db, LibraryRoot root, IHardLinkCreator? hardLinkCreator)
    {
        TempRoot = tempRoot;
        Options = options;
        Db = db;
        Root = root;
        HardLinkCreator = hardLinkCreator ?? new FileSystemHardLinkCreator();
        services = Build();
    }

    public string TempRoot { get; }
    public DbContextOptions<AppDbContext> Options { get; }
    public AppDbContext Db { get; private set; }
    public LibraryRoot Root { get; }
    public string DataRoot => Path.Combine(TempRoot, "data");
    public DirectoryInfo AcquisitionDirectory => new(Path.Combine(DataRoot, "acquisition"));
    public string Downloads => Path.Combine(TempRoot, "downloads");
    public string SeriesFolder => Path.Combine(Root.Path, "Frieren");
    public FakeProwlarrClient Prowlarr { get; } = new();
    public FakeSabnzbdClient Sabnzbd { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public IHardLinkCreator HardLinkCreator { get; }
    public HttpMessageHandler AniListHandler { get; set; } = new NotConnectedAniListHandler();
    public FakeAnimeMetadataProvider AniListMetadata { get; } = new();

    /// <summary>What ffprobe says about files the library scan analyses; like a non-media file unless a test describes them.</summary>
    public FakeMediaProbeRunner Probe { get; } = new();
    public Guid AnimeId { get; private set; }
    public Guid ProwlarrIndexerEntryId { get; private set; }

    public AnimeAcquisitionScheduler Scheduler => services.GetRequiredService<AnimeAcquisitionScheduler>();
    public AnimeMonitoringStore Monitoring => services.GetRequiredService<AnimeMonitoringStore>();
    public QualityProfileStore QualityProfiles => services.GetRequiredService<QualityProfileStore>();
    public AcquisitionOwnershipStore Ownership => services.GetRequiredService<AcquisitionOwnershipStore>();
    public SabnzbdAcquisitionStore Acquisitions => services.GetRequiredService<SabnzbdAcquisitionStore>();
    public AnimeImportStore Imports => services.GetRequiredService<AnimeImportStore>();
    public OperationStore Operations => new(Db);
    public AnimeImportSettingsStore ImportSettings => services.GetRequiredService<AnimeImportSettingsStore>();
    public AcquisitionPolicyStore Policy => services.GetRequiredService<AcquisitionPolicyStore>();
    public AniListAutoMonitorSettingsStore AniListAutoMonitorSettings => services.GetRequiredService<AniListAutoMonitorSettingsStore>();
    public AniListAccountStore AniListAccounts => services.GetRequiredService<AniListAccountStore>();

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Runs an action against a scoped <see cref="AcquisitionApiKeyService"/>.</summary>
    public async Task<T> WithApiKeysAsync<T>(Func<AcquisitionApiKeyService, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AcquisitionApiKeyService>());
    }

    /// <summary>Runs an action against a scoped <see cref="AcquisitionApiService"/> — the same
    /// façade the acquisition automation API endpoints call.</summary>
    public async Task<T> WithAcquisitionApiAsync<T>(Func<AcquisitionApiService, Task<T>> action)
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AcquisitionApiService>());
    }

    public async Task<AnimeLibraryLocation?> GetLibraryLocationAsync(Guid animeId, Guid? preferredRootId = null)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AnimeAcquisitionInventory>()
            .GetLibraryLocationAsync(animeId, CancellationToken.None, preferredRootId);
    }

    public async Task<AcquisitionBackupBundle> ExportBackupAsync()
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AcquisitionBackupService>().ExportAsync(CancellationToken.None);
    }

    public async Task<AcquisitionBackupPreview> PreviewRestoreAsync(AcquisitionBackupBundle bundle)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AcquisitionBackupService>().PreviewRestoreAsync(bundle, CancellationToken.None);
    }

    public async Task<AcquisitionBackupRestoreResult> RestoreBackupAsync(AcquisitionBackupBundle bundle)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AcquisitionBackupService>().RestoreAsync(bundle, CancellationToken.None);
    }

    /// <summary>What an approved or automatic anime request from Discover runs.</summary>
    public async Task<AcquisitionExecution> ExecuteAnimeRequestAsync(string aniListId, string? payloadJson = null)
    {
        await using var scope = services.CreateAsyncScope();
        var execution = await scope.ServiceProvider.GetRequiredService<AnimeAcquisitionRequestExecutor>().ExecuteAsync(
            new AcquisitionRequest(
                Guid.NewGuid(), MediaAcquisitionKind.Anime, AniListMetadataProvider.ProviderKey, aniListId, "Requested", null, null, payloadJson,
                "owner", AcquisitionRequestStatus.Searching, null, null, null, DateTime.UtcNow, DateTime.UtcNow, "owner", DateTime.UtcNow),
            CancellationToken.None);
        Db.ChangeTracker.Clear();
        return execution;
    }

    public async Task<AniListAutoMonitorRunResult> RunAniListAutoMonitorAsync(string profileId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AniListAutoMonitorService>().RunForProfileAsync(profileId, CancellationToken.None);
    }

    public async Task<IReadOnlyList<AcquisitionHistoryEntry>> HistoryForAnimeAsync(Guid animeId, int limit = 50)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AcquisitionHistoryService>().ForAnimeAsync(animeId, limit, CancellationToken.None);
    }

    public static Task<AnimeAcquisitionEnvironment> CreateAsync() => CreateAsync(null);

    public static async Task<AnimeAcquisitionEnvironment> CreateAsync(IHardLinkCreator? hardLinkCreator)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-acquisition-{Guid.NewGuid():N}");
        var library = Path.Combine(tempRoot, "anime");
        var dictionary = Path.Combine(tempRoot, "dictionary");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(dictionary);
        Directory.CreateDirectory(Path.Combine(tempRoot, "downloads"));
        await File.WriteAllTextAsync(Path.Combine(dictionary, "jmdict-ger.tsv"), "");
        await File.WriteAllTextAsync(Path.Combine(dictionary, "jmdict-eng-common.tsv"), "");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        var root = new LibraryRoot { Name = "Anime", Path = library };
        db.LibraryRoots.Add(root);
        db.LibraryRootContentAssignments.Add(new LibraryRootContentAssignment { LibraryRootId = root.Id, ContentType = LibraryContentType.Anime });
        await db.SaveChangesAsync();

        var environment = new AnimeAcquisitionEnvironment(tempRoot, options, db, root, hardLinkCreator);
        environment.ProwlarrIndexerEntryId = Guid.NewGuid();
        await environment.services.GetRequiredService<IndexerStore>().SaveAsync(
            new IndexerEntry(
                environment.ProwlarrIndexerEntryId,
                "Prowlarr",
                IndexerType.Prowlarr,
                Enabled: true,
                Priority: 1,
                IndexerSettings.CreateDefault("http://prowlarr:9696", IndexerType.Prowlarr),
                "prowlarr-key"));
        await environment.services.GetRequiredService<DownloadClientStore>().SaveAsync(
            new DownloadClientEntry(
                Guid.NewGuid(),
                "SABnzbd",
                DownloadClientType.Sabnzbd,
                Enabled: true,
                Priority: 1,
                new DownloadClientSettings("http://sabnzbd:8080", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = "books", [MediaAcquisitionKind.Anime] = "anime" }),
                "secret-key"));

        // A simple naming profile keeps the expected library paths readable in assertions.
        var naming = environment.services.GetRequiredService<AnimeNamingProfileStore>();
        await naming.UpsertAsync(new AnimeNamingProfile(
            SimpleProfileId,
            "Simple",
            "{Series Title}",
            "Season {season:00}",
            "Specials",
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            "{Series Title} - {Air-Date} - {Episode Title}",
            "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            UseSeasonFolders: true,
            AnimeMultiEpisodeStyle.PrefixedRange,
            ReplaceIllegalCharacters: true,
            AnimeColonReplacement.Smart));
        await naming.SetDefaultAsync(SimpleProfileId);
        return environment;
    }

    /// <summary>
    /// Frieren with S01E01 on disk, an AniList match with <paramref name="episodeCount"/> episodes,
    /// monitored, and (unless <paramref name="mode"/> is null) the given management mode.
    /// </summary>
    public async Task SeedFrierenAsync(
        int episodeCount = 2,
        AnimeManagementMode? mode = AnimeManagementMode.JularrManaged,
        bool seasonFolders = true)
    {
        AddLibraryFile(seasonFolders ? ["Frieren", "Season 01", "Frieren - S01E01 - Episode 1.mkv"] : ["Frieren", "Frieren - S01E01 - Episode 1.mkv"]);
        await ScanAsync();

        var anime = await Db.Anime.AsNoTracking().SingleAsync(item => item.Key == AnimeKey);
        AnimeId = anime.Id;
        Db.AnimeMetadata.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = AniListMetadataProvider.ProviderKey,
            ExternalId = "154587",
            PreferredTitle = "Frieren",
            RomajiTitle = "Sousou no Frieren",
            EpisodeCount = episodeCount
        });
        await Db.SaveChangesAsync();

        await Scheduler.RunExclusiveAsync(
            (pipeline, token) => pipeline.UpdateAnimeSettingsAsync(anime.Id, true, false, null, [], token),
            CancellationToken.None);
        if (mode is { } managementMode)
        {
            await Ownership.UpdateAsync(state => SonarrParallelSafety.SetMode(state, AnimeKey, managementMode, DateTimeOffset.UtcNow));
        }
    }

    public string AddLibraryFile(params string[] relativeParts)
    {
        var path = Path.Combine([Root.Path, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    public async Task<ScanResult> ScanAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LibraryScanner>().ScanAsync(Root.Id, CancellationToken.None);
        Db.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Writes a completed SABnzbd job folder with one video file and returns its path.</summary>
    public string AddCompletedDownload(string jobName, string fileName)
    {
        var folder = Path.Combine(Downloads, jobName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, fileName), [1, 2, 3]);
        return folder;
    }

    /// <summary>
    /// SABnzbd reports the job of the acquisition's latest attempt as completed; the monitor
    /// projection runs and returns the completed operation (the import is not started).
    /// </summary>
    public async Task<OperationSnapshot> CompleteLatestDownloadAsync(string storagePath, SabnzbdAcquisition? acquisition = null)
    {
        acquisition ??= (await Acquisitions.LoadAsync()).Acquisitions.Single();
        var operation = (await Operations.GetAsync(acquisition.LatestAttempt!.OperationId))!;
        ReportCompletedPath(operation, storagePath);
        var result = await SabnzbdOperationProjector.ApplyAsync(
            Operations,
            await Operations.ListActiveExternalAsync(SabnzbdClient.ProviderId),
            new SabnzbdQueueSnapshot(false, null, null, []),
            Sabnzbd.History,
            DateTime.UtcNow,
            CancellationToken.None);
        return result.Completed.Single(item => item.Id == operation.Id);
    }

    /// <summary>SABnzbd's history lists the job of <paramref name="operation"/> as completed at this path.</summary>
    public void ReportCompletedPath(OperationSnapshot operation, string storagePath) =>
        Sabnzbd.History = new SabnzbdHistorySnapshot(
        [
            new SabnzbdHistoryJob(
                operation.ExternalId!,
                Path.GetFileName(storagePath),
                "Completed",
                "anime",
                storagePath,
                null,
                SabnzbdFailureKind.None,
                DateTimeOffset.UtcNow)
        ]);

    /// <summary>
    /// What the SABnzbd monitor does for a completed anime job: the shared completed-download
    /// import step, which reads the path SABnzbd reports (<paramref name="reportedPath"/>) from
    /// the job's download client, applies the Anime remote path mapping and dispatches to the
    /// Anime importer. Returns the job's import record.
    /// </summary>
    public async Task<AnimeImportRecord?> ImportCompletedAsync(OperationSnapshot download, string reportedPath)
    {
        ReportCompletedPath(download, reportedPath);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CompletedDownloadImportService>().ImportAsync(
            download,
            MediaAcquisitionKind.Anime,
            request: null,
            DateTime.UtcNow,
            CancellationToken.None);
        var record = await scope.ServiceProvider.GetRequiredService<AnimeImportStore>()
            .FindByDownloadAsync(download.Id, CancellationToken.None);
        Db.ChangeTracker.Clear();
        return record;
    }

    /// <summary>The shared import step for a completed anime job, with its result and the download's recorded import details.</summary>
    public async Task<(CompletedDownloadImportResult Result, DownloadImportDetails? Details)> ImportThroughSharedSpineAsync(
        OperationSnapshot download,
        DateTime? nowUtc = null)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CompletedDownloadImportService>().ImportAsync(
            download,
            MediaAcquisitionKind.Anime,
            request: null,
            nowUtc ?? DateTime.UtcNow,
            CancellationToken.None);
        var stored = await Operations.GetAsync(download.Id);
        Db.ChangeTracker.Clear();
        return (result, DownloadOperationDetails.TryParse(stored?.Details, out var details) ? details!.Import : null);
    }

    public AsyncServiceScope CreateScope() => services.CreateAsyncScope();

    /// <summary>The import details recorded on a download Operation.</summary>
    public async Task<DownloadImportDetails?> DownloadImportAsync(Guid downloadOperationId)
    {
        var stored = await Operations.GetAsync(downloadOperationId);
        Db.ChangeTracker.Clear();
        return DownloadOperationDetails.TryParse(stored?.Details, out var details) ? details!.Import : null;
    }

    /// <summary>Restart recovery of Anime imports, as the scheduler runs it.</summary>
    public async Task<int> RecoverImportsAsync(DateTime nowUtc)
    {
        await using var scope = services.CreateAsyncScope();
        var recovered = await scope.ServiceProvider.GetRequiredService<AnimeImportRecovery>()
            .RecoverAsync(nowUtc, CancellationToken.None);
        Db.ChangeTracker.Clear();
        return recovered;
    }

    public async Task<AnimeImportActionResult> ImportManuallyAsync(Guid recordId, string sourcePath, int season, int episode)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AnimeImportExecutor>()
            .ImportManuallyAsync(recordId, sourcePath, season, episode, CancellationToken.None);
        Db.ChangeTracker.Clear();
        return result;
    }

    public async Task<SabnzbdAcquisitionResult> StartAcquisitionAsync(
        IReadOnlyList<AnimeEpisodeKey> episodes,
        string releaseTitle)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SabnzbdAcquisitionService>().StartAsync(
            new SabnzbdAnimeAcquisitionRequest(
                AnimeKey,
                "Frieren",
                episodes,
                null,
                [new SabnzbdAnimeReleaseCandidate($"release:{releaseTitle}", releaseTitle, new Uri("https://indexer.example/a.nzb"))]),
            CancellationToken.None);
    }

    public async Task<IReadOnlyList<OperationLogEntry>> LogsAsync(string module) =>
        await Operations.ListLogsAsync(new OperationLogFilter(Module: module, Limit: 200));

    public async Task<AnimeMonitoringState> MonitoringStateAsync() => await Monitoring.LoadAsync();

    public async Task<MediaFile?> MediaFileAsync(int season, int episode)
    {
        Db.ChangeTracker.Clear();
        return await Db.MediaFiles
            .AsNoTracking()
            .Where(file => Db.Episodes.Any(item =>
                item.Id == file.EpisodeId &&
                item.AnimeId == AnimeId &&
                item.SeasonNumber == season &&
                item.Number == episode))
            .SingleOrDefaultAsync();
    }

    /// <summary>New service instances on the same data, database and library, as after a restart.</summary>
    public async Task RestartAsync()
    {
        await services.DisposeAsync();
        await Db.DisposeAsync();
        Db = new AppDbContext(Options);
        services = Build();
    }

    public static ProwlarrReleaseCandidate Release(string title, string guid, string protocol = "usenet") =>
        new(
            title,
            "Test indexer",
            1,
            protocol,
            1_200_000_000,
            null,
            null,
            DateTimeOffset.UtcNow,
            1,
            24,
            guid,
            null,
            AnimeReleaseParser.Parse(title),
            [],
            protocol == "usenet" ? new Uri($"https://indexer.example/{guid}.nzb?apikey=indexer-secret") : null,
            null);

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await Db.DisposeAsync();
        if (Directory.Exists(TempRoot))
        {
            foreach (var file in Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(TempRoot, recursive: true);
        }
    }

    private ServiceProvider Build()
    {
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        collection.AddSingleton(Db);
        collection.AddSingleton(Protection);
        collection.AddSingleton<IConfiguration>(SabnzbdTestSupport.Configuration());
        collection.AddSingleton<ISabnzbdClient>(Sabnzbd);
        collection.AddSingleton<IProwlarrClient>(Prowlarr);
        collection.AddSingleton<ISonarrObserverClient, UnusedSonarrObserverClient>();

        var acquisition = AcquisitionDirectory;
        var integrations = new DirectoryInfo(Path.Combine(DataRoot, "integrations"));
        collection.AddSingleton(new SabnzbdSettingsStore(Protection, acquisition));
        collection.AddSingleton(new SabnzbdAcquisitionStore(Protection, acquisition));
        collection.AddSingleton(new ProwlarrSettingsStore(Protection, acquisition));
        collection.AddSingleton(new IndexerStore(Protection, acquisition));
        collection.AddSingleton(new DownloadClientStore(Protection, acquisition));
        collection.AddSingleton(new AcquisitionHealthStore(acquisition));
        collection.AddSingleton<IReadOnlyDictionary<IndexerType, IIndexer>>(provider =>
            new Dictionary<IndexerType, IIndexer>
            {
                [IndexerType.Prowlarr] = new ProwlarrIndexer(provider.GetRequiredService<IProwlarrClient>())
            });
        collection.AddSingleton<IDownloadClient>(provider =>
            new SabnzbdDownloadClient(provider.GetRequiredService<ISabnzbdClient>()));
        collection.AddScoped<IndexerSearchCoordinator>();
        collection.AddScoped<DownloadClientSelector>();
        collection.AddScoped<DownloadClientSubmissionService>();
        collection.AddSingleton(new AnimeQualityProfileStore(acquisition));
        collection.AddSingleton(new AnimeMonitoringStore(DataRoot));
        collection.AddSingleton(new AnimeImportStore(acquisition));
        collection.AddSingleton(new AnimeNamingProfileStore(acquisition));
        collection.AddSingleton(new AcquisitionOwnershipStore(DataRoot));
        collection.AddSingleton(new SonarrConnectionStore(Protection));
        collection.AddSingleton<SonarrObservationService>();
        collection.AddSingleton(new AniListAccountStore(Protection, NullLogger<AniListAccountStore>.Instance, integrations));
        collection.AddSingleton(new MediaMappingReviewStore(NullLogger<MediaMappingReviewStore>.Instance, integrations));
        collection.AddSingleton(new ReadingSegmentMappingStore(NullLogger<ReadingSegmentMappingStore>.Instance, integrations));
        collection.AddSingleton(new AnimeImportSettingsStore(DataRoot));
        collection.AddSingleton(new AcquisitionPolicyStore(DataRoot));
        collection.AddSingleton(new AniListAutoMonitorSettingsStore(DataRoot));
        collection.AddSingleton<IHardLinkCreator>(HardLinkCreator);
        collection.AddSingleton<IHttpClientFactory>(new SingleHandlerHttpClientFactory(() => AniListHandler));
        collection.AddScoped<AcquisitionHistoryService>();
        collection.AddScoped<AcquisitionBackupService>(_ => new AcquisitionBackupService(DataRoot));
        collection.AddScoped<AniListAutoMonitorService>();

        collection.AddSingleton<IAnimeMetadataProvider>(AniListMetadata);
        collection.AddScoped<AnimeMetadataService>();
        collection.AddScoped<Jularr.Web.Features.MediaCore.WorkService>();
        collection.AddScoped<Jularr.Web.Features.MediaCore.WorkStructureService>();
        collection.AddScoped<Jularr.Web.Features.MediaCore.LegacyWorkBridge>();
        collection.AddScoped<AnimeAcquisitionRequestExecutor>();
        collection.AddScoped<SabnzbdConnectionResolver>();
        collection.AddScoped<SabnzbdDownloadService>();
        collection.AddScoped<SabnzbdAcquisitionService>();
        collection.AddScoped<ProwlarrAnimeSearchService>();
        collection.AddScoped<AnimeAcquisitionInventory>();
        collection.AddScoped<AnimeAcquisitionPipeline>();
        collection.AddScoped<AnimeImportExecutor>();
        collection.AddScoped<AcquisitionAccessStore>();
        collection.AddScoped<ICompletedDownloadImportAdapter>(provider => provider.GetRequiredService<AnimeImportExecutor>());
        collection.AddScoped<CompletedDownloadDispatcher>();
        collection.AddScoped<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>();
        collection.AddScoped<CompletedDownloadImportService>();
        collection.AddScoped<AnimeImportRecovery>();
        collection.AddSingleton<Jularr.Web.Features.Storage.StorageAvailabilityCoordinator>();
        collection.AddScoped<Jularr.Web.Features.Storage.LibraryRootAvailabilityService>();
        collection.AddSingleton<BackgroundJobQueue>();
        collection.AddSingleton(new MediaOptimizationJournal(Path.Combine(DataRoot, "media-optimization")));
        collection.AddSingleton<MediaOptimizationQueue>();
        collection.AddSingleton<AnimeAcquisitionScheduler>();
        collection.AddScoped<AcquisitionApiKeyService>();
        collection.AddScoped<AcquisitionApiService>();
        collection.AddHttpClient();

        var dictionary = Path.Combine(TempRoot, "dictionary");
        var mediaInventory = MediaInventoryTestSupport.Create(Options, Probe);
        collection.AddScoped(provider =>
        {
            var db = provider.GetRequiredService<AppDbContext>();
            var sonarrStore = provider.GetRequiredService<SonarrConnectionStore>();
            return new LibraryScanner(
                db,
                new SubtitleImportService(db, new VocabularyService(db, new JapaneseTermExtractor(new NoMorphology()), new JapaneseDictionary(dictionary))),
                new EmbeddedSubtitleExtractor(new MediaProcessRunner(NullLogger<MediaProcessRunner>.Instance), mediaInventory, NullLogger<EmbeddedSubtitleExtractor>.Instance),
                mediaInventory,
                new SonarrArtworkSyncService(
                    sonarrStore,
                    new SonarrArtworkImportService(db, new NoHttpClientFactory(), NullLogger<SonarrArtworkImportService>.Instance),
                    NullLogger<SonarrArtworkSyncService>.Instance),
                NullLogger<LibraryScanner>.Instance,
                canonicalVideoBackfill: new CanonicalVideoStorageBackfillService(
                    db,
                    new Jularr.Web.Features.MediaCore.LegacyWorkBridge(db, new Jularr.Web.Features.MediaCore.WorkService(db), new Jularr.Web.Features.MediaCore.WorkStructureService(db)),
                    new CanonicalMediaStorageService(db),
                    NullLogger<CanonicalVideoStorageBackfillService>.Instance));
        });

        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SingleHandlerHttpClientFactory(Func<HttpMessageHandler> handlerFactory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handlerFactory(), disposeHandler: false) { BaseAddress = new Uri("https://graphql.anilist.co/") };
    }

    // The default handler for tests that never connect AniList: any GraphQL call would be a bug.
    private sealed class NotConnectedAniListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No AniList connection is configured for this test.");
    }

    private sealed class NoMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }

    private sealed class UnusedSonarrObserverClient : ISonarrObserverClient
    {
        public Task<IReadOnlyList<SonarrObservedSeries>> GetSeriesAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedEpisodeFile>> GetEpisodeFilesAsync(SonarrConnectionSettings settings, int seriesId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedQueueItem>> GetQueueAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SonarrObservedHistoryEvent>> GetRecentHistoryAsync(SonarrConnectionSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>AniList entries by id; unknown ids are not found. Search returns nothing.</summary>
internal sealed class FakeAnimeMetadataProvider : IAnimeMetadataProvider
{
    public Dictionary<string, AnimeMetadataCandidate> Entries { get; } = new(StringComparer.Ordinal);
    public string Key => AniListMetadataProvider.ProviderKey;

    public Task<IReadOnlyList<AnimeMetadataCandidate>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AnimeMetadataCandidate>>([]);

    public Task<AnimeMetadataCandidate?> GetAsync(string externalId, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.GetValueOrDefault(externalId));

    public void Add(string id, string title, int? episodeCount, int? year = null) =>
        Entries[id] = new AnimeMetadataCandidate(
            AniListMetadataProvider.ProviderKey, id, title, title, null, null, null, null, null, "TV", "RELEASING", null, year, episodeCount, 24);
}

/// <summary>Returns the configured releases for every query and counts the queries.</summary>
internal sealed class FakeProwlarrClient : IProwlarrClient
{
    public List<ProwlarrReleaseCandidate> Releases { get; } = [];
    public List<string> Queries { get; } = [];
    public List<ProwlarrConnection> Connections { get; } = [];

    public Task<ProwlarrConnectionTestResult> TestAsync(ProwlarrConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(new ProwlarrConnectionTestResult(true, "test"));

    public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        ProwlarrConnection connection,
        ProwlarrSearchQuery search,
        CancellationToken cancellationToken)
    {
        Queries.Add(search.Query);
        Connections.Add(connection);
        return Task.FromResult<IReadOnlyList<ProwlarrReleaseCandidate>>([.. Releases]);
    }
}
