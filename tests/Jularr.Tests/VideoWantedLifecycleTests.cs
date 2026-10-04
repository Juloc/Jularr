using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Video;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Tv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// #812 acceptance spine: provider-resolved canonical Movie/TV Work -> shared Wanted/indexer/download
/// -> completed-download dispatcher -> canonical video import. No media-specific scheduler is involved.
/// </summary>
[TestClass]
public sealed class VideoWantedLifecycleTests
{
    [TestMethod]
    public async Task MovieRequestSearchesDownloadsImportsAndKeepsOneCanonicalWork()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            Candidate("Inception.2010.1080p.BluRay.x264-GROUP", "movie-release"));
        var work = await host.CreateMovieAsync("27205", "Inception", 2010);
        var request = await host.CreateApprovedAsync(
            MediaAcquisitionKind.Movie,
            "27205",
            "Inception");

        await host.ProcessAsync(DateTime.UtcNow);
        var downloading = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, downloading.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.IsNotNull(downloading.OperationId);

        var folder = host.Download("Inception.2010");
        File.WriteAllText(
            Path.Combine(folder, "Inception.2010.1080p.BluRay.x264-GROUP.mkv"),
            "video");
        await host.CompleteAsync(downloading.OperationId!.Value, "movies", folder);
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));

        var completed = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status);
        Assert.AreEqual("/Library", completed.ResultUrl);
        Assert.AreEqual(1, await host.Environment.Db.Works.CountAsync());
        Assert.AreEqual(work.Id, (await host.Environment.Db.Works.SingleAsync()).Id);
        Assert.AreEqual(1, await host.Environment.Db.Movies.CountAsync());
        Assert.AreEqual(1, await host.Environment.Db.MediaAssets.CountAsync(x =>
            x.WorkId == work.Id && x.Kind == MediaAssetKind.Video && x.WorkEpisodeId == null));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task TvRequestImportsCurrentEpisodeThenStaysApprovedForFutureEpisode()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Tv,
            Candidate("Breaking.Bad.S01E01.1080p.WEB-DL.x264-GROUP", "tv-release"));
        var work = await host.CreateSeriesAsync("1396", "Breaking Bad", 2008);
        var structure = new WorkStructureService(host.Environment.Db);
        var season = await structure.AddOrUpdateSeasonAsync(work.Id, 1, "Season 1", CancellationToken.None);
        var episode1 = await structure.AddOrUpdateEpisodeAsync(
            work.Id, 1, 1, null, false, "Pilot", DateTime.UtcNow.AddDays(-30), season.Id, CancellationToken.None);
        await structure.AddOrUpdateEpisodeAsync(
            work.Id, 1, 2, null, false, "Cat's in the Bag...", DateTime.UtcNow.AddDays(14), season.Id, CancellationToken.None);

        var request = await host.CreateApprovedAsync(
            MediaAcquisitionKind.Tv,
            "1396",
            "Breaking Bad");

        await host.ProcessAsync(DateTime.UtcNow);
        var downloading = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, downloading.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);

        var folder = host.Download("Breaking.Bad.S01E01");
        File.WriteAllText(
            Path.Combine(folder, "Breaking.Bad.S01E01.1080p.WEB-DL.x264-GROUP.mkv"),
            "video");
        await host.CompleteAsync(downloading.OperationId!.Value, "tv", folder);
        var now = DateTime.UtcNow.AddMinutes(1);
        await host.ProcessAsync(now);

        var monitoring = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, monitoring.Status);
        Assert.AreEqual("/Library", monitoring.ResultUrl);
        StringAssert.Contains(monitoring.StatusMessage, "Monitoring future episodes");
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "Future episodes must not be grabbed before their air date.");
        Assert.AreEqual(1, await host.Environment.Db.Works.CountAsync());
        Assert.AreEqual(work.Id, (await host.Environment.Db.Works.SingleAsync()).Id);
        Assert.AreEqual(1, await host.Environment.Db.TvSeries.CountAsync());
        Assert.AreEqual(2, await host.Environment.Db.WorkEpisodes.CountAsync(x => x.WorkId == work.Id));
        Assert.AreEqual(1, await host.Environment.Db.MediaAssets.CountAsync(x =>
            x.WorkId == work.Id && x.WorkEpisodeId == episode1.Id && x.Kind == MediaAssetKind.Video));

        var payload = VideoAcquisitionEngine.ReadPersistedPayload(monitoring);
        Assert.IsTrue(payload.HasFutureIntent);
        Assert.IsTrue(payload.NextSearchUtc > now);
    }

    [TestMethod]
    public async Task FailedMovieDownloadContinuesWithTheNextUntriedRelease()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            Candidate("Inception.2010.1080p.BluRay.x264-GROUP", "first"),
            Candidate("Inception.2010.1080p.WEB-DL.x264-OTHER", "second"));
        await host.CreateMovieAsync("27205", "Inception", 2010);
        var request = await host.CreateApprovedAsync(MediaAcquisitionKind.Movie, "27205", "Inception");

        await host.ProcessAsync(DateTime.UtcNow);
        var first = await host.GetAsync(request.Id);
        var firstOperation = first.OperationId!.Value;
        await host.Operations.MarkFailedAsync(firstOperation, "Out of retention");
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));

        var second = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, second.Status);
        Assert.AreNotEqual(firstOperation, second.OperationId);
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count);
        StringAssert.Contains(second.StatusMessage, "Out of retention");

        await host.Operations.MarkFailedAsync(second.OperationId!.Value, "Broken archive");
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(2));
        var waiting = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status);
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count, "Tried releases are not submitted again.");
    }

    [TestMethod]
    public async Task CancelledMovieDownloadDoesNotGrabTheNextRelease()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            Candidate("Inception.2010.1080p.BluRay.x264-GROUP", "first"),
            Candidate("Inception.2010.1080p.WEB-DL.x264-OTHER", "second"));
        await host.CreateMovieAsync("27205", "Inception", 2010);
        var request = await host.CreateApprovedAsync(MediaAcquisitionKind.Movie, "27205", "Inception");

        await host.ProcessAsync(DateTime.UtcNow);
        var downloading = await host.GetAsync(request.Id);
        await host.Operations.MarkCancelledAsync(
            downloading.OperationId!.Value,
            "Cancelled by owner.");
        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));
        await host.ProcessAsync(DateTime.UtcNow.AddDays(1));

        var cancelled = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, cancelled.Status);
        Assert.AreEqual(WantedAcquisitionService.CancelledMessage, cancelled.StatusMessage);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    private static ProwlarrReleaseCandidate Candidate(string title, string identity) =>
        new(
            title,
            "Test indexer",
            1,
            "usenet",
            8L * 1024 * 1024 * 1024,
            null,
            null,
            DateTimeOffset.UtcNow,
            1,
            1,
            identity,
            null,
            ReleaseParser.Parse(title),
            [],
            new Uri($"https://indexer.invalid/download/{identity}.nzb"),
            null);

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly string downloadsRoot;

        private Host(
            SabnzbdTestEnvironment environment,
            ServiceProvider services,
            AnimeImportSettingsStore importSettings,
            string downloadsRoot)
        {
            Environment = environment;
            this.services = services;
            ImportSettings = importSettings;
            this.downloadsRoot = downloadsRoot;
        }

        public SabnzbdTestEnvironment Environment { get; }
        public AnimeImportSettingsStore ImportSettings { get; }
        public AcquisitionAccessStore Requests => new(Environment.Db);
        public OperationStore Operations => new(Environment.Db);

        public static async Task<Host> CreateAsync(
            MediaAcquisitionKind kind,
            params ProwlarrReleaseCandidate[] releases)
        {
            var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
            var root = environment.Directory.FullName;

            var clientStore = environment.NewDownloadClientStore();
            var clientEntry = (await clientStore.LoadAllAsync()).Single();
            var categories = clientEntry.Settings.Categories.ToDictionary(x => x.Key, x => x.Value);
            categories[MediaAcquisitionKind.Movie] = "movies";
            categories[MediaAcquisitionKind.Tv] = "tv";
            await clientStore.SaveAsync(
                clientEntry with
                {
                    Settings = new DownloadClientSettings(clientEntry.Settings.BaseUrl, categories)
                });

            var indexerStore = new IndexerStore(environment.Protection, environment.Directory);
            await indexerStore.SaveAsync(
                new IndexerEntry(
                    Guid.NewGuid(),
                    "Test indexer",
                    IndexerType.Newznab,
                    Enabled: true,
                    Priority: 1,
                    new IndexerSettings("https://indexer.invalid", [2000, 5000], [], 100),
                    "indexer-key"));
            var coordinator = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer>
                {
                    [IndexerType.Newznab] = new FixedIndexer(releases)
                },
                indexerStore,
                new AcquisitionHealthStore(environment.Directory),
                NullLogger<IndexerSearchCoordinator>.Instance);

            var registry = new MediaAcquisitionRegistry(
            [
                new MovieAcquisitionRegistration(),
                new TvAcquisitionRegistration()
            ]);
            var qualityDirectory = Directory.CreateDirectory(Path.Combine(root, "acquisition"));
            var quality = new QualityProfileStore(qualityDirectory, registry);
            var importSettings = new AnimeImportSettingsStore(root);
            var libraryRoot = Path.Combine(root, "library");
            Directory.CreateDirectory(libraryRoot);
            await importSettings.UpdateAsync(state => state with
            {
                DefaultImportMode = ImportMode.Copy,
                MediaLibraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>
                {
                    [MediaAcquisitionKind.Movie] = new(Path.Combine(libraryRoot, "movies")),
                    [MediaAcquisitionKind.Tv] = new(Path.Combine(libraryRoot, "tv"))
                }
            });

            var works = new WorkService(environment.Db);
            var structure = new WorkStructureService(environment.Db);
            var bridge = new LegacyWorkBridge(environment.Db, works, structure);
            var canonicalStorage = new CanonicalMediaStorageService(environment.Db);
            var movieImporter = new MovieCompletedDownloadImportAdapter(
                new MovieLibraryService(environment.Db, bridge),
                registry,
                importSettings,
                new FileSystemHardLinkCreator(),
                NullLogger<MovieCompletedDownloadImportAdapter>.Instance,
                canonicalStorage);
            var tvImporter = new TvCompletedDownloadImportAdapter(
                new TvLibraryService(environment.Db, bridge, structure),
                registry,
                importSettings,
                new FileSystemHardLinkCreator(),
                NullLogger<TvCompletedDownloadImportAdapter>.Instance,
                canonicalStorage);

            var account = AcquisitionAccessFixture.Account("owner", AccountRole.Owner);
            var provider = new ServiceCollection()
                .AddSingleton(environment.Db)
                .AddSingleton(TimeProvider.System)
                .AddSingleton(registry)
                .AddSingleton(quality)
                .AddSingleton(coordinator)
                .AddSingleton(clientStore)
                .AddSingleton(_ => environment.NewSubmissionService())
                .AddSingleton<IDownloadClient>(new SabnzbdDownloadClient(environment.Client))
                .AddSingleton(importSettings)
                .AddSingleton(new VideoMonitoringStores(root))
                .AddSingleton(_ => new AcquisitionAccessStore(environment.Db))
                .AddSingleton(account)
                .AddSingleton<Jularr.Web.Features.Events.IJularrEventPublisher, RecordingEventPublisher>()
                .AddSingleton<IMediaCapabilityService>(new MediaCapabilityService(new MediaCapabilityStore(root)))
                .AddSingleton(new AcquisitionRequestSettingsStore(root))
                .AddSingleton<ReleaseRequestTracker>()
                .AddSingleton<VideoAcquisitionEngine>()
                .AddSingleton<IAcquisitionRequestExecutor, MovieAcquisitionRequestExecutor>()
                .AddSingleton<IAcquisitionRequestExecutor, TvAcquisitionRequestExecutor>()
                .AddSingleton<AcquisitionRequestService>()
                .AddSingleton<IWantedRequestHandler, MovieWantedRequestHandler>()
                .AddSingleton<IWantedRequestHandler, TvWantedRequestHandler>()
                .AddSingleton<ICompletedDownloadImportAdapter>(movieImporter)
                .AddSingleton<ICompletedDownloadImportAdapter>(tvImporter)
                .AddSingleton<CompletedDownloadDispatcher>()
                .AddSingleton<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>()
                .AddSingleton<CompletedDownloadImportService>()
                .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider();

            return new Host(
                environment,
                provider,
                importSettings,
                Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName);
        }

        public async Task<Work> CreateMovieAsync(string tmdbId, string title, int year)
        {
            var works = new WorkService(Environment.Db);
            return await works.EnsureWorkByExternalIdentityAsync(
                WorkMediaType.Movie,
                "tmdb",
                tmdbId,
                title,
                year,
                CancellationToken.None);
        }

        public async Task<Work> CreateSeriesAsync(string tmdbId, string title, int year)
        {
            var works = new WorkService(Environment.Db);
            return await works.EnsureWorkByExternalIdentityAsync(
                WorkMediaType.Series,
                "tmdb",
                tmdbId,
                title,
                year,
                CancellationToken.None);
        }

        public Task<AcquisitionRequest> CreateApprovedAsync(
            MediaAcquisitionKind kind,
            string externalId,
            string title,
            AcquisitionRequestOptions? options = null) =>
            Requests.CreateAsync(
                new AcquisitionRequestDraft(
                    kind,
                    "tmdb",
                    externalId,
                    title,
                    null,
                    null,
                    PayloadJson: options?.Validate().ToPayloadJson()),
                "owner",
                AcquisitionRequestStatus.Approved,
                "owner",
                CancellationToken.None);

        public Task<int> ProcessAsync(DateTime nowUtc) =>
            WantedAcquisitionService.ProcessOnceAsync(
                services,
                nowUtc,
                CancellationToken.None);

        public async Task<AcquisitionRequest> GetAsync(Guid id) =>
            (await Requests.GetAsync(id, CancellationToken.None))!;

        public string Download(string name)
        {
            var path = Path.Combine(downloadsRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task CompleteAsync(Guid operationId, string category, string path)
        {
            var operation = (await Operations.GetAsync(operationId))!;
            Environment.Client.History = new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob(
                    operation.ExternalId!,
                    Path.GetFileName(path),
                    "Completed",
                    category,
                    path,
                    null,
                    SabnzbdFailureKind.None,
                    DateTimeOffset.UtcNow)
            ]);
            await Operations.MarkSucceededAsync(operationId, "Downloaded.");
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Environment.DisposeAsync();
        }
    }

    private sealed class FixedIndexer(IReadOnlyList<ProwlarrReleaseCandidate> releases) : IIndexer
    {
        public IndexerType Type => IndexerType.Newznab;

        public Task<IndexerConnectionTestResult> TestAsync(
            IndexerEntry entry,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IndexerConnectionTestResult(true));

        public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
            IndexerEntry entry,
            IndexerSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(releases);
    }
}
