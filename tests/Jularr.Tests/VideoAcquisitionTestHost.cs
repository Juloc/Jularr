using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
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
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The shared Movie/TV acquisition test host: a real database, a fake indexer and SABnzbd, the shared request service, Wanted pass and
/// executors, signed in as the owner. Tests drive the same code paths the Admin pages call.
/// </summary>
internal sealed class VideoAcquisitionTestHost : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private VideoAcquisitionTestHost(
        SabnzbdTestEnvironment environment,
        ServiceProvider services,
        IServiceCollection descriptors,
        RecordingVideoImporter importer,
        MediaAcquisitionKind kind,
        Work work,
        string tmdbId,
        FixedVideoIndexer indexer,
        Guid? episodeId,
        Guid? secondEpisodeId)
    {
        TmdbId = tmdbId;
        Indexer = indexer;
        Environment = environment;
        this.services = services;
        Descriptors = descriptors;
        Importer = importer;
        Kind = kind;
        Work = work;
        EpisodeId = episodeId;
        SecondEpisodeId = secondEpisodeId;
    }

    public SabnzbdTestEnvironment Environment { get; }
    public RecordingVideoImporter Importer { get; }
    public MediaAcquisitionKind Kind { get; }
    public Work Work { get; }
    public string TmdbId { get; }
    public FixedVideoIndexer Indexer { get; }
    public Guid? EpisodeId { get; }
    public Guid? SecondEpisodeId { get; }
    internal static ClaimsPrincipal OwnerPrincipal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, AccountRoles.Owner)], "test"));

    public IServiceProvider Services => services;

    /// <summary>The registrations behind <see cref="Get{T}"/>, for hosts that serve the same services over HTTP.</summary>
    public IServiceCollection Descriptors { get; }
    public OperationStore Operations => new(Environment.Db);
    public T Get<T>() where T : notnull => services.GetRequiredService<T>();
    public AcquisitionAccessStore Requests => new(Environment.Db);

    internal static ProwlarrReleaseCandidate Candidate(string title) =>
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

    public static async Task<VideoAcquisitionTestHost> CreateAsync(
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
        var indexer = new FixedVideoIndexer(candidates);
        var coordinator = new IndexerSearchCoordinator(
            new Dictionary<IndexerType, IIndexer>
            {
                [IndexerType.Newznab] = indexer
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
            .AddSingleton(new CurrentAccountContext(new FixedAccessor(new DefaultHttpContext { User = OwnerPrincipal() })))
            .AddSingleton<ReleaseRequestTracker>()
            .AddSingleton<VideoAcquisitionEngine>()
            .AddSingleton<IJularrEventPublisher, RecordingEventPublisher>()
            .AddSingleton<IMediaCapabilityService>(
                new MediaCapabilityService(new MediaCapabilityStore(directory.FullName)))
            .AddSingleton(new AcquisitionRequestSettingsStore(directory.FullName))
            .AddSingleton<AcquisitionRequestService>()
            .AddSingleton(new AnimeMonitoringStore(directory.FullName))
            .AddSingleton<VideoRequestWorkResolver>()
            .AddSingleton<VideoRequestScopeResolver>()
            .AddSingleton<VideoMonitoringService>()
            .AddSingleton<AdminVideoMediaService>()
            .AddSingleton<VideoManualSearchService>()
            .AddSingleton<WantedListService>()
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

        return new VideoAcquisitionTestHost(environment, services.BuildServiceProvider(), services, importer, kind, work, tmdbId, indexer, episode?.Id, secondEpisode?.Id);
    }

    /// <summary>Creates the approved request of the host's Work without running the Wanted pass, so a test can change monitoring before the first search.</summary>
    public Task<AcquisitionRequest> CreateApprovedAsync(VideoRequestPayload? payload = null) =>
        Requests.CreateAsync(
            new AcquisitionRequestDraft(Kind, "tmdb", TmdbId, Work.CanonicalTitle, null, null, payload?.Serialize()),
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

    /// <summary>
    /// Puts the Series on a custom selection the way an Admin edit stores one (a new scope revision and a fresh search; what the selection
    /// would include but the list left out is excluded), so a test can start from the state a Request-dialog request has.
    /// </summary>
    public async Task SetCustomScopeAsync(IReadOnlyCollection<Guid> seasonIds, IReadOnlyCollection<Guid> episodeIds, bool monitorFuture)
    {
        var open = await Get<VideoMonitoringService>().FindOpenRequestAsync(Kind, Work.Id, CancellationToken.None) ?? await CreateApprovedAsync();
        var chosen = await Get<VideoRequestScopeResolver>().BuildTvPayloadAsync(Work, new VideoRequestScopeChoice(VideoRequestScope.Custom, seasonIds, episodeIds, monitorFuture), CancellationToken.None);
        var now = DateTime.UtcNow;
        var selection = new VideoRequestSelection(chosen with { MonitorFutureFromUtc = now }, now);
        var checkedEpisodes = episodeIds.ToHashSet();
        var episodes = await Environment.Db.WorkEpisodes.AsNoTracking().Where(x => x.WorkId == Work.Id).ToListAsync();
        var excluded = episodes.Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt) && !checkedEpisodes.Contains(x.Id)).Select(x => x.Id).ToArray();
        VideoRequestPayload Current(string? stored) => VideoRequestPayload.Parse(stored) ?? VideoRequestPayload.Default(Kind, Work.Id, Work.CanonicalTitle, Work.Year);
        var patched = await Requests.PatchPayloadAsync(
            open.Id,
            stored => (Current(stored) with
            {
                Monitored = true,
                Scope = chosen.Scope,
                SelectedEpisodeIds = chosen.SelectedEpisodeIds,
                SelectedSeasonIds = chosen.SelectedSeasonIds,
                MonitorFuture = chosen.MonitorFuture,
                MonitorFutureFromUtc = now,
                ExcludedEpisodeIds = excluded,
                ExcludedSeasonIds = [],
                ScopeRevision = Current(stored).ScopeRevision + 1,
                Searches = 0,
                NextSearchUtc = null,
                LastProblem = null
            }).Serialize(),
            open.Status,
            open.Status,
            open.Status == AcquisitionRequestStatus.Approved ? VideoMonitoringService.MonitoringChanged : null,
            CancellationToken.None);
        Assert.IsTrue(patched, "The request changed while the test set it up.");
    }

    public async Task<AcquisitionRequest> StartAsync(VideoRequestPayload? payload = null)
    {
        var created = await CreateApprovedAsync(payload);
        await ProcessAsync(DateTime.UtcNow);
        return await GetAsync(created.Id);
    }

    /// <summary>Adds an episode to the host's Series; a null air date means long aired.</summary>
    public async Task<WorkEpisode> AddEpisodeAsync(int season, int number, DateTime? airedAt = null, Guid? seasonId = null)
    {
        var episode = new WorkEpisode { WorkId = Work.Id, SeasonId = seasonId, SeasonNumber = season, EpisodeNumber = number, AiredAt = airedAt ?? DateTime.UtcNow.AddDays(-30) };
        Environment.Db.WorkEpisodes.Add(episode);
        await Environment.Db.SaveChangesAsync();
        return episode;
    }

    /// <summary>Attaches one playable file to the Movie (null episode) or to an episode, as an import would.</summary>
    public async Task AttachFileAsync(Guid? workEpisodeId, string name = "file.mkv", long sizeBytes = 1024)
    {
        var db = Environment.Db;
        var root = await db.LibraryRoots.FirstOrDefaultAsync() ?? db.LibraryRoots.Add(new LibraryRoot { Name = "Video test", Path = "/library" }).Entity;
        var version = new WorkVersion { WorkId = Work.Id, VersionKey = $"test:{Guid.NewGuid():N}", Source = "test" };
        var asset = new MediaAsset { WorkId = Work.Id, WorkEpisodeId = workEpisodeId, WorkVersionId = version.Id, Kind = MediaAssetKind.Video };
        db.AddRange(version, asset, new StoredFile { MediaAssetId = asset.Id, LibraryRootId = root.Id, Path = $"/library/{name}", SizeBytes = sizeBytes, LastWriteTimeUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds another Work of the host's media type with its provider identity, for tests that need several titles in one list.</summary>
    public async Task<Work> AddWorkAsync(string title, string tmdbId)
    {
        var work = new Work { MediaType = Work.MediaType, CanonicalTitle = title, Year = 2020 };
        Environment.Db.Works.Add(work);
        Environment.Db.WorkExternalIdentities.Add(new WorkExternalIdentity { WorkId = work.Id, MediaType = work.MediaType, Provider = "tmdb", ExternalId = tmdbId, IsPrimary = true, Evidence = "test" });
        await Environment.Db.SaveChangesAsync();
        return work;
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

// A plain property: HttpContextAccessor keeps its context in an AsyncLocal that does not survive the async setup method.
internal sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = context;
}

internal sealed class FixedVideoIndexer(IReadOnlyList<ProwlarrReleaseCandidate> releases) : IIndexer
{
    /// <summary>Lets a test hold a search open, to change something while a Wanted pass is running.</summary>
    public Func<Task>? OnSearch { get; set; }

    public IndexerType Type => IndexerType.Newznab;

    public Task<IndexerConnectionTestResult> TestAsync(
        IndexerEntry entry,
        CancellationToken cancellationToken) =>
        Task.FromResult(new IndexerConnectionTestResult(true));

    public async Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(
        IndexerEntry entry,
        IndexerSearchQuery query,
        CancellationToken cancellationToken)
    {
        if (OnSearch is { } gate)
        {
            await gate();
        }

        return releases;
    }
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
        var payload = VideoRequestPayload.Parse(acquisition.PayloadJson)
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

        // The real Movie/TV adapters return no result address, so the request keeps the canonical page its execution set.
        return CompletedDownloadImportResult.Completed("Imported canonical video.", resultUrl: null);
    }
}
