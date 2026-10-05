using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class VideoAcquisitionRequestExecutorTests
{
    [TestMethod]
    public async Task MovieUsesSharedWantedDownloadAndCompletesThroughImporter()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP");

        var request = await host.StartAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.IsNotNull(request.OperationId);
        Assert.AreEqual(MediaAcquisitionKind.Movie, host.OperationDetails(request).MediaKind);

        host.CompleteInSabnzbd(request, "/downloads/movies/Dune.2021");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var completed = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, completed.Status);
        Assert.AreEqual(1, host.Importer.Imports);
        Assert.IsTrue(await host.HasPlayableAsync(workEpisodeId: null));
        StringAssert.StartsWith(completed.ResultUrl, "/Search?q=");
    }

    [TestMethod]
    public async Task TvDefaultScopeAcquiresCurrentEpisodeAndKeepsFutureMonitoring()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E01.1080p.WEB-DL.x264-GROUP",
            addEpisode: true);

        var request = await host.StartAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        var payload = VideoAcquisitionEngine.ReadPayload(request)
            ?? throw new AssertFailedException("Expected video request payload.");
        Assert.AreEqual(VideoRequestScope.AllCurrentAndFuture, payload.Scope);
        Assert.IsTrue(payload.MonitorFuture);
        Assert.AreEqual(host.EpisodeId, payload.ActiveWorkEpisodeId);

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E01");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var monitoring = await host.GetAsync(request.Id);
        Assert.AreEqual(
            AcquisitionRequestStatus.Approved,
            monitoring.Status,
            "A TV request with All current + future stays as the durable future-monitor owner.");
        Assert.AreEqual(1, host.Importer.Imports);
        Assert.IsTrue(await host.HasPlayableAsync(host.EpisodeId));
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count, "Future monitoring must not duplicate the imported episode.");

        var storedPayload = VideoAcquisitionEngine.ReadPayload(monitoring)
            ?? throw new AssertFailedException("Expected persisted TV monitoring payload.");
        Assert.IsNotNull(storedPayload.NextSearchUtc);
        Assert.IsNull(storedPayload.ActiveWorkEpisodeId);
    }

    [TestMethod]
    public async Task TvCustomScopeSearchesOnlyTheSelectedCanonicalEpisode()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [host.SecondEpisodeId!.Value],
            MonitorFuture: false);

        var request = await host.StartAsync(custom);
        var payload = VideoAcquisitionEngine.ReadPayload(request)
            ?? throw new AssertFailedException("Expected video request payload.");

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(host.SecondEpisodeId, payload.ActiveWorkEpisodeId);
        Assert.AreEqual(2, payload.ActiveEpisodeNumber);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    [TestMethod]
    public async Task TvCustomSeasonScopeKeepsFutureEpisodesInsideSelectedCanonicalSeason()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E01.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var season = new WorkSeason { WorkId = host.Work.Id, SeasonNumber = 1 };
        host.Environment.Db.WorkSeasons.Add(season);
        var episodes = await host.Environment.Db.WorkEpisodes
            .Where(x => x.WorkId == host.Work.Id)
            .OrderBy(x => x.EpisodeNumber)
            .ToListAsync();
        foreach (var episode in episodes)
        {
            episode.SeasonId = season.Id;
        }

        episodes[1].AiredAt = DateTime.UtcNow.AddDays(2);
        await host.Environment.Db.SaveChangesAsync();

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [],
            MonitorFuture: false,
            SelectedSeasonIds: [season.Id]);

        var request = await host.StartAsync(custom);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);
        Assert.AreEqual(host.EpisodeId, VideoAcquisitionEngine.ReadPayload(request)?.ActiveWorkEpisodeId);

        host.CompleteInSabnzbd(request, "/downloads/tv/Severance.S01E01");
        await host.Operations.MarkSucceededAsync(request.OperationId!.Value, "Downloaded.");
        await host.ProcessAsync(DateTime.UtcNow);

        var monitoring = await host.GetAsync(request.Id);
        var payload = VideoAcquisitionEngine.ReadPayload(monitoring)
            ?? throw new AssertFailedException("Expected video request payload.");
        Assert.AreEqual(AcquisitionRequestStatus.Approved, monitoring.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
        Assert.IsTrue(await host.HasPlayableAsync(host.EpisodeId));
        CollectionAssert.Contains(payload.SelectedSeasonIds!, season.Id);
        Assert.IsNotNull(payload.NextSearchUtc);
        Assert.IsNull(payload.ActiveWorkEpisodeId);
    }

    [TestMethod]
    public async Task TvCustomScopeKeepsSelectedFutureEpisodeOpenWithoutFutureMonitoring()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Tv,
            "Severance",
            2022,
            "95396",
            "Severance.S01E02.1080p.WEB-DL.x264-GROUP",
            addEpisode: true,
            addSecondEpisode: true);

        var selectedEpisodeId = host.SecondEpisodeId!.Value;
        var selectedEpisode = await host.Environment.Db.WorkEpisodes.SingleAsync(x => x.Id == selectedEpisodeId);
        selectedEpisode.AiredAt = DateTime.UtcNow.AddDays(2);
        await host.Environment.Db.SaveChangesAsync();

        var custom = new VideoRequestPayload(
            host.Work.Id,
            host.Work.CanonicalTitle,
            host.Work.Year,
            VideoRequestScope.Custom,
            [selectedEpisodeId],
            MonitorFuture: false);

        var request = await host.StartAsync(custom);
        var payload = VideoAcquisitionEngine.ReadPayload(request)
            ?? throw new AssertFailedException("Expected video request payload.");

        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status);
        Assert.AreEqual(0, host.Environment.Client.Grabs.Count);
        Assert.IsNotNull(payload.NextSearchUtc);
        Assert.IsNull(payload.ActiveWorkEpisodeId);
        StringAssert.Contains(request.StatusMessage ?? "", "Waiting for the next requested TV episode");
    }

    [TestMethod]
    public async Task FailedMovieDownloadRetriesTheNextUntriedReleaseOnlyOnce()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP",
            "Dune.2021.1080p.BluRay.x264-SECOND");

        var request = await host.StartAsync();
        var firstOperation = request.OperationId!.Value;

        await host.Operations.MarkFailedAsync(firstOperation, "Out of retention");
        await host.ProcessAsync(DateTime.UtcNow);

        var retried = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status);
        Assert.AreNotEqual(firstOperation, retried.OperationId);
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count);

        await host.ProcessAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(2, host.Environment.Client.Grabs.Count, "An active retry must not submit the same request again.");
    }

    [TestMethod]
    public async Task CancelledMovieDownloadNeverGrabsAReplacement()
    {
        await using var host = await Host.CreateAsync(
            MediaAcquisitionKind.Movie,
            "Dune",
            2021,
            "438631",
            "Dune.2021.1080p.WEB-DL.x264-GROUP",
            "Dune.2021.1080p.BluRay.x264-SECOND");

        var request = await host.StartAsync();
        await host.Operations.MarkCancelledAsync(request.OperationId!.Value, "Cancelled by owner.");
        await host.ProcessAsync(DateTime.UtcNow);
        await host.ProcessAsync(DateTime.UtcNow.AddDays(2));

        var failed = await host.GetAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, failed.Status);
        Assert.AreEqual(1, host.Environment.Client.Grabs.Count);
    }

    // A plain property: HttpContextAccessor keeps its context in an AsyncLocal that does not survive the async setup method.
    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static HttpContext OwnerRequest() =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, AccountRoles.Owner)], "test"))
        };

    private static ProwlarrReleaseCandidate Candidate(string title) =>
        new(
            title,
            "Video test indexer",
            1,
            "usenet",
            2L * 1024 * 1024 * 1024,
            null,
            null,
            DateTimeOffset.UtcNow,
            0,
            1,
            title,
            null,
            AnimeReleaseParser.Parse(title),
            [],
            new Uri($"https://indexer.invalid/download/{Uri.EscapeDataString(title)}"),
            null);

    internal sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly IServiceCollection descriptors;

        private Host(
            SabnzbdTestEnvironment environment,
            ServiceProvider services,
            IServiceCollection descriptors,
            RecordingVideoImporter importer,
            MediaAcquisitionKind kind,
            Work work,
            Guid? episodeId,
            Guid? secondEpisodeId)
        {
            Environment = environment;
            this.services = services;
            this.descriptors = descriptors;
            Importer = importer;
            Kind = kind;
            Work = work;
            EpisodeId = episodeId;
            SecondEpisodeId = secondEpisodeId;
        }

        public SabnzbdTestEnvironment Environment { get; }

        /// <summary>The registrations behind <see cref="Get{T}"/>, for hosts that serve the same services over HTTP.</summary>
        public IServiceCollection Descriptors => descriptors;

        public T Get<T>() where T : notnull => services.GetRequiredService<T>();
        public RecordingVideoImporter Importer { get; }
        public MediaAcquisitionKind Kind { get; }
        public Work Work { get; }
        public Guid? EpisodeId { get; }
        public Guid? SecondEpisodeId { get; }
        public OperationStore Operations => new(Environment.Db);
        public AcquisitionAccessStore Requests => new(Environment.Db);

        public static async Task<Host> CreateAsync(
            MediaAcquisitionKind kind,
            string title,
            int year,
            string tmdbId,
            string firstRelease,
            string? secondRelease = null,
            bool addEpisode = false,
            bool addSecondEpisode = false,
            IReadOnlyList<string>? moreReleases = null)
        {
            var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
            var directory = environment.Directory;
            var db = environment.Db;

            var work = new Work
            {
                MediaType = kind == MediaAcquisitionKind.Movie ? WorkMediaType.Movie : WorkMediaType.Series,
                CanonicalTitle = title,
                Year = year
            };
            db.Works.Add(work);
            db.WorkExternalIdentities.Add(new WorkExternalIdentity
            {
                WorkId = work.Id,
                MediaType = work.MediaType,
                Provider = "tmdb",
                ExternalId = tmdbId,
                IsPrimary = true,
                Evidence = "test"
            });

            WorkEpisode? episode = null;
            WorkEpisode? secondEpisode = null;
            if (addEpisode)
            {
                episode = new WorkEpisode
                {
                    WorkId = work.Id,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    AiredAt = DateTime.UtcNow.AddDays(-7)
                };
                db.WorkEpisodes.Add(episode);
            }

            if (addSecondEpisode)
            {
                secondEpisode = new WorkEpisode
                {
                    WorkId = work.Id,
                    SeasonNumber = 1,
                    EpisodeNumber = 2,
                    AiredAt = DateTime.UtcNow.AddDays(-6)
                };
                db.WorkEpisodes.Add(secondEpisode);
            }

            await db.SaveChangesAsync();

            var indexerStore = new IndexerStore(environment.Protection, directory);
            await indexerStore.SaveAsync(new IndexerEntry(
                Guid.NewGuid(),
                "Video test indexer",
                IndexerType.Newznab,
                Enabled: true,
                Priority: 1,
                new IndexerSettings("https://indexer.invalid", [2000, 5000], [], 100),
                "indexer-key"));

            var candidates = new[] { firstRelease, secondRelease }.Concat(moreReleases ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Candidate(x!))
                .ToArray();
            var coordinator = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer>
                {
                    [IndexerType.Newznab] = new FixedIndexer(candidates)
                },
                indexerStore,
                new AcquisitionHealthStore(directory),
                NullLogger<IndexerSearchCoordinator>.Instance);

            var registry = new MediaAcquisitionRegistry(
                [new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);
            var importer = new RecordingVideoImporter(db, kind);
            var services = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton(TimeProvider.System)
                .AddSingleton(coordinator)
                .AddSingleton(registry)
                .AddSingleton(new QualityProfileStore(
                    new DirectoryInfo(Path.Combine(directory.FullName, "quality-profiles")),
                    registry))
                .AddSingleton(_ => environment.NewDownloadClientStore())
                .AddSingleton(_ => environment.NewSubmissionService())
                .AddSingleton<IDownloadClient>(new SabnzbdDownloadClient(environment.Client))
                .AddSingleton(new AnimeImportSettingsStore(directory.FullName))
                .AddSingleton(_ => new AcquisitionAccessStore(db))
                .AddSingleton(new CurrentAccountContext(new FixedAccessor(OwnerRequest())))
                .AddSingleton<ReleaseRequestTracker>()
                .AddSingleton(new VideoAcquisitionMonitoringStores(directory.FullName))
                .AddSingleton<VideoAcquisitionEngine>()
                .AddSingleton<VideoManualSearchService>()
                .AddSingleton<IJularrEventPublisher, RecordingEventPublisher>()
                .AddSingleton<IMediaCapabilityService>(
                    new MediaCapabilityService(new MediaCapabilityStore(directory.FullName)))
                .AddSingleton(new AcquisitionRequestSettingsStore(directory.FullName))
                .AddSingleton<AcquisitionRequestService>()
                .AddSingleton<ICompletedDownloadImportAdapter>(importer)
                .AddSingleton<CompletedDownloadDispatcher>()
                .AddSingleton<CompletedDownloadImportService>()
                .AddSingleton<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>()
                .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

            if (kind == MediaAcquisitionKind.Movie)
            {
                services
                    .AddSingleton<IAcquisitionRequestExecutor, MovieAcquisitionRequestExecutor>()
                    .AddSingleton<IWantedRequestHandler, MovieWantedRequestHandler>();
            }
            else
            {
                services
                    .AddSingleton<IAcquisitionRequestExecutor, TvAcquisitionRequestExecutor>()
                    .AddSingleton<IWantedRequestHandler, TvWantedRequestHandler>();
            }

            return new Host(environment, services.BuildServiceProvider(), services, importer, kind, work, episode?.Id, secondEpisode?.Id);
        }

        /// <summary>An approved request that no scheduler pass has searched yet, as Manual Search finds it.</summary>
        public async Task<AcquisitionRequest> CreateApprovedAsync(VideoRequestPayload? payload = null) =>
            await Requests.CreateAsync(
                new AcquisitionRequestDraft(
                    Kind,
                    "tmdb",
                    Kind == MediaAcquisitionKind.Movie ? "438631" : "95396",
                    Work.CanonicalTitle,
                    null,
                    null,
                    payload?.Serialize()),
                "owner",
                AcquisitionRequestStatus.Approved,
                "owner",
                CancellationToken.None);

        public async Task<AcquisitionRequest> StartAsync(VideoRequestPayload? payload = null)
        {
            var created = await CreateApprovedAsync(payload);
            await ProcessAsync(DateTime.UtcNow);
            return await GetAsync(created.Id);
        }

        public Task<int> ProcessAsync(DateTime nowUtc) =>
            WantedAcquisitionService.ProcessOnceAsync(services, nowUtc, CancellationToken.None);

        public async Task<AcquisitionRequest> GetAsync(Guid id) =>
            (await Requests.GetAsync(id, CancellationToken.None))!;

        public DownloadOperationDetails OperationDetails(AcquisitionRequest request)
        {
            var operation = Operations.GetAsync(request.OperationId!.Value).GetAwaiter().GetResult()!;
            Assert.IsTrue(DownloadOperationDetails.TryParse(operation.Details, out var details));
            return details!;
        }

        public void CompleteInSabnzbd(AcquisitionRequest request, string storagePath)
        {
            var operation = Operations.GetAsync(request.OperationId!.Value).GetAwaiter().GetResult()!;
            Environment.Client.History = new SabnzbdHistorySnapshot(
            [
                new SabnzbdHistoryJob(
                    operation.ExternalId!,
                    Path.GetFileName(storagePath),
                    "Completed",
                    null,
                    storagePath,
                    null,
                    SabnzbdFailureKind.None,
                    DateTimeOffset.UtcNow)
            ]);
        }

        public async Task<bool> HasPlayableAsync(Guid? workEpisodeId) =>
            await Environment.Db.MediaAssets.AsNoTracking()
                .AnyAsync(x => x.WorkId == Work.Id
                               && x.WorkEpisodeId == workEpisodeId
                               && x.Kind == MediaAssetKind.Video
                               && Environment.Db.StoredFiles.Any(file => file.MediaAssetId == x.Id));

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

    internal sealed class RecordingVideoImporter(
        AppDbContext db,
        MediaAcquisitionKind kind) : ICompletedDownloadImportAdapter
    {
        public MediaAcquisitionKind Kind => kind;
        public int Imports { get; private set; }

        public async Task<CompletedDownloadImportResult> ImportAsync(
            CompletedDownloadImportRequest request,
            CancellationToken cancellationToken)
        {
            Imports++;
            var acquisition = request.Request ?? throw new AssertFailedException("Expected request-backed import.");
            var payload = VideoAcquisitionEngine.ReadPayload(acquisition)
                ?? throw new AssertFailedException("Expected canonical video request payload.");

            var root = await db.LibraryRoots.FirstOrDefaultAsync(cancellationToken);
            if (root is null)
            {
                root = new LibraryRoot { Name = "Video test", Path = "/library" };
                db.LibraryRoots.Add(root);
            }

            var version = new WorkVersion
            {
                WorkId = payload.WorkId,
                VersionKey = $"test:{Guid.NewGuid():N}",
                UnitKey = payload.ActiveSeasonNumber is int season && payload.ActiveEpisodeNumber is int episode
                    ? $"S{season:D2}E{episode:D2}"
                    : null,
                Source = "test"
            };
            var asset = new MediaAsset
            {
                WorkId = payload.WorkId,
                WorkEpisodeId = payload.ActiveWorkEpisodeId,
                WorkVersionId = version.Id,
                Kind = MediaAssetKind.Video
            };
            var file = new StoredFile
            {
                MediaAssetId = asset.Id,
                LibraryRootId = root.Id,
                Path = $"/library/{Guid.NewGuid():N}.mkv",
                SizeBytes = 1024,
                LastWriteTimeUtc = DateTime.UtcNow
            };
            db.AddRange(version, asset, file);
            await db.SaveChangesAsync(cancellationToken);

            return CompletedDownloadImportResult.Completed(
                "Imported canonical video.",
                $"/Search?q={Uri.EscapeDataString(payload.Title)}");
        }
    }
}
