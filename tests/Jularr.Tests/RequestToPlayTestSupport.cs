using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Movies;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Tv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DiscoverIndexModel = Jularr.Web.Pages.IndexModel;

namespace Jularr.Tests;

/// <summary>A clock the test moves, so future-airing episodes and search backoffs become due without waiting.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public DateTime UtcNow => _now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>TMDB over a stubbed HTTP handler: the only provider the Movie and TV vertical talks to.</summary>
internal sealed class FakeTmdb : HttpMessageHandler
{
    private readonly Dictionary<string, string> responses = [];

    public List<string> Requests { get; } = [];

    public void AddMovie(int id, string title, DateTime released) =>
        responses[$"/3/movie/{id}"] = JsonSerializer.Serialize(new { title, original_title = title, original_language = "en", release_date = Day(released) });

    public void AddSeries(int id, string name, DateTime firstAired, params (int Season, (int Episode, DateTime Aired)[] Episodes)[] seasons)
    {
        responses[$"/3/tv/{id}"] = JsonSerializer.Serialize(new
        {
            name,
            original_name = name,
            original_language = "en",
            first_air_date = Day(firstAired),
            seasons = seasons.Select(season => new { season_number = season.Season, name = $"Season {season.Season}" })
        });
        foreach (var season in seasons)
        {
            responses[$"/3/tv/{id}/season/{season.Season}"] = JsonSerializer.Serialize(new
            {
                episodes = season.Episodes.Select(episode => new { episode_number = episode.Episode, name = $"Episode {episode.Episode}", air_date = Day(episode.Aired) })
            });
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(path);
        return Task.FromResult(responses.TryGetValue(path, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>A Usenet indexer whose releases the test changes between searches.</summary>
internal sealed class ScriptedIndexer : IIndexer
{
    public List<ProwlarrReleaseCandidate> Releases { get; } = [];

    public int Searches { get; private set; }

    public IndexerType Type => IndexerType.Newznab;

    public void Publish(string title) =>
        Releases.Add(new ProwlarrReleaseCandidate(
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
            null));

    public Task<IndexerConnectionTestResult> TestAsync(IndexerEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(new IndexerConnectionTestResult(true));

    public Task<IReadOnlyList<ProwlarrReleaseCandidate>> SearchAsync(IndexerEntry entry, IndexerSearchQuery query, CancellationToken cancellationToken)
    {
        Searches++;
        return Task.FromResult<IReadOnlyList<ProwlarrReleaseCandidate>>([.. Releases]);
    }
}

/// <summary>
/// The Movie or TV request-to-play vertical on one PostgreSQL database: the Discover page and the shared request service, the
/// Wanted pass with the real indexer coordinator, download-client submission, SABnzbd monitor projection and completed-download
/// importer, and (over the same database) the real Movie/Series detail pages, Watch player and client video API. Only the outside
/// world is faked: TMDB, the indexer, SABnzbd, ffprobe and the files under a temp directory. <see cref="RestartAsync"/> builds
/// every service and database context again, as after a process restart.
/// </summary>
internal sealed class VideoRequestToPlayWorld : IAsyncDisposable
{
    public const string Owner = "owner";

    private readonly SabnzbdTestEnvironment _environment;
    private readonly DbContextOptions<AppDbContext> _options;
    private ServiceProvider _services = null!;

    private VideoRequestToPlayWorld(SabnzbdTestEnvironment environment, VideoDetailPageTestHost pages, MediaAcquisitionKind kind, FakeTmdb tmdb)
    {
        _environment = environment;
        Pages = pages;
        Kind = kind;
        Tmdb = tmdb;
        DatabasePath = Path.Combine(environment.Directory.FullName, "jularr.db");
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={DatabasePath};Foreign Keys=True").Options;
        Db = new AppDbContext(_options);
    }

    public MediaAcquisitionKind Kind { get; }
    public FakeTmdb Tmdb { get; }
    public VideoDetailPageTestHost Pages { get; }
    public AppDbContext Db { get; private set; }
    public string DatabasePath { get; }
    public ScriptedIndexer Indexer { get; } = new();
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public FakeSabnzbdClient Sabnzbd => _environment.Client;
    public string LibraryRoot => Path.Combine(_environment.Directory.FullName, "library");
    public string Downloads => Path.Combine(_environment.Directory.FullName, "downloads");
    public OperationStore Operations => new(Db);
    public IServiceProvider Services => _services;
    public AcquisitionAccessStore Requests => new(Db);

    public static async Task<VideoRequestToPlayWorld> CreateAsync(MediaAcquisitionKind kind, FakeTmdb tmdb)
    {
        var environment = await SabnzbdTestSupport.CreateEnvironmentAsync();
        var pages = await VideoDetailPageTestHost.CreateAsync(Path.Combine(environment.Directory.FullName, "jularr.db"));
        // Every imported file is a playable H.264/AAC video to ffprobe, as the real probe would read it from the file.
        pages.Probe.DefaultResult = new MediaProbeRun(MediaProbeRunStatus.Completed, MediaProbeFixtures.H264Stereo);
        var world = new VideoRequestToPlayWorld(environment, pages, kind, tmdb);
        Directory.CreateDirectory(world.LibraryRoot);
        Directory.CreateDirectory(world.Downloads);
        await new IndexerStore(environment.Protection, environment.Directory).SaveAsync(new IndexerEntry(
            Guid.NewGuid(),
            "Video test indexer",
            IndexerType.Newznab,
            Enabled: true,
            Priority: 1,
            new IndexerSettings("https://indexer.invalid", [2000, 5000], [], 100),
            "indexer-key"));
        await MovieTvImportTests.RoutingWithDefaultAsync(world.Db, kind == MediaAcquisitionKind.Movie ? LibraryContentType.Movie : LibraryContentType.Tv, world.LibraryRoot);
        world._services = world.Build();
        return world;
    }

    /// <summary>Every service and database context is built again over the same database, files and download client, as after a restart.</summary>
    public async Task RestartAsync()
    {
        await _services.DisposeAsync();
        await Db.DisposeAsync();
        Db = new AppDbContext(_options);
        _services = Build();
    }

    /// <summary>The Request dialog of the Discover page for a signed-in profile: the one entry point of every request.</summary>
    public DiscoverIndexModel DiscoverPage(string profileId = Owner, AccountRole role = AccountRole.Owner) =>
        DiscoverPageFactory.Create(Db, DiscoverPageFactory.Tmdb(Db, Tmdb), _services.GetServices<IAcquisitionRequestExecutor>(), Pages.Capabilities, _environment.Directory.FullName, profileId, role);

    /// <summary>The dialog resolving a card: the canonical Work (and for a series its seasons and episodes) exists afterwards.</summary>
    public async Task<DiscoverRequestSettingsView> ResolveAsync(string externalId, string profileId = Owner, AccountRole role = AccountRole.Owner)
    {
        var category = Kind == MediaAcquisitionKind.Movie ? "movie" : "tv";
        var result = await DiscoverPage(profileId, role).OnPostResolveAsync(category, "tmdb", externalId, CancellationToken.None);
        return (DiscoverRequestSettingsView)((PartialViewResult)result).ViewData.Model!;
    }

    /// <summary>Submits the Request dialog of a card exactly as the browser posts it.</summary>
    public async Task<IActionResult> PostRequestAsync(
        string externalId,
        string title,
        string? scope = null,
        Guid[]? seasonIds = null,
        Guid[]? episodeIds = null,
        bool monitorFuture = false,
        string profileId = Owner,
        AccountRole role = AccountRole.Owner) =>
        await DiscoverPage(profileId, role).OnPostRequestAsync(
            new DiscoverRequestForm
            {
                Category = Kind == MediaAcquisitionKind.Movie ? "movie" : "tv",
                Provider = "tmdb",
                ExternalId = externalId,
                Title = title,
                Scope = scope,
                SeasonIds = seasonIds ?? [],
                EpisodeIds = episodeIds ?? [],
                MonitorFuture = monitorFuture
            },
            CancellationToken.None);

    public async Task<AcquisitionRequest> RequestAsync(
        string externalId,
        string title,
        string? scope = null,
        Guid[]? seasonIds = null,
        Guid[]? episodeIds = null,
        bool monitorFuture = false)
    {
        var result = await PostRequestAsync(externalId, title, scope, seasonIds, episodeIds, monitorFuture);
        return ((DiscoverRequestResultView)((PartialViewResult)result).ViewData.Model!).Request;
    }

    /// <summary>The owner approves a waiting request; it runs through the same executor path as an auto-approved one.</summary>
    public async Task<AcquisitionRequest> ApproveAsync(Guid id) => await _services.GetRequiredService<AcquisitionRequestService>().ApproveAsync(id, CancellationToken.None);

    /// <summary>One pass of the Wanted scheduler at the world's clock.</summary>
    public Task<int> WantedPassAsync() => WantedAcquisitionService.ProcessOnceAsync(_services, Clock.UtcNow, CancellationToken.None);

    public async Task<AcquisitionRequest> GetRequestAsync(Guid id) => (await Requests.GetAsync(id, CancellationToken.None))!;

    /// <summary>SABnzbd reports the request's download as running at <paramref name="percent"/>; the monitor projection runs.</summary>
    public async Task ReportDownloadProgressAsync(AcquisitionRequest request, double percent)
    {
        var operation = (await Operations.GetAsync(request.OperationId!.Value))!;
        Sabnzbd.Queue = new SabnzbdQueueSnapshot(false, null, null, [new SabnzbdQueueJob(operation.ExternalId!, "job", "Downloading", null, percent, null, 1_000_000, (long)(1_000_000 * (100 - percent) / 100))]);
        await ProjectDownloadsAsync();
    }

    /// <summary>
    /// SABnzbd finished the request's download into a real folder holding the given files; the monitor projection marks the
    /// download operation succeeded. The importer is not run: that is the Wanted pass's job.
    /// </summary>
    public Task<string> CompleteDownloadAsync(AcquisitionRequest request, string jobName, params string[] fileNames) =>
        CompleteDownloadAsync(request, jobName, [1, 2, 3, 4], fileNames);

    /// <summary>The same download whose files hold <paramref name="content"/>, so two releases of one title differ in size as real ones do.</summary>
    public async Task<string> CompleteDownloadAsync(AcquisitionRequest request, string jobName, byte[] content, params string[] fileNames)
    {
        var folder = Path.Combine(Downloads, jobName);
        Directory.CreateDirectory(folder);
        foreach (var fileName in fileNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, fileName), content);
        }

        var operation = (await Operations.GetAsync(request.OperationId!.Value))!;
        Sabnzbd.Queue = new SabnzbdQueueSnapshot(false, null, null, []);
        Sabnzbd.History = new SabnzbdHistorySnapshot([new SabnzbdHistoryJob(operation.ExternalId!, jobName, "Completed", null, folder, null, SabnzbdFailureKind.None, DateTimeOffset.UtcNow)]);
        await ProjectDownloadsAsync();
        return folder;
    }

    /// <summary>The background media inventory analyses every imported file once, as a library scan does, without any page being opened.</summary>
    public async Task AnalyzeLibraryFilesAsync()
    {
        await using var scope = Pages.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<MediaInventoryService>();
        foreach (var fileId in await Db.StoredFiles.AsNoTracking().Select(x => x.Id).ToListAsync())
        {
            Assert.IsNotNull(await inventory.EnsureAnalyzedAsync(fileId, CancellationToken.None), "The fake probe must describe every imported file.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Pages.DisposeAsync();
        await _services.DisposeAsync();
        await Db.DisposeAsync();
        await _environment.DisposeAsync();
    }

    private async Task ProjectDownloadsAsync() =>
        await SabnzbdOperationProjector.ApplyAsync(Operations, await Operations.ListActiveExternalAsync(SabnzbdClient.ProviderId), Sabnzbd.Queue, Sabnzbd.History, Clock.UtcNow, CancellationToken.None);

    private ServiceProvider Build()
    {
        var directory = _environment.Directory;
        var registry = new MediaAcquisitionRegistry([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration()]);
        var health = new AcquisitionHealthStore(directory);
        var coordinator = new IndexerSearchCoordinator(
            new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = Indexer },
            new IndexerStore(_environment.Protection, directory),
            health,
            NullLogger<IndexerSearchCoordinator>.Instance);
        var downloadClients = _environment.NewDownloadClientStore();
        var downloadClient = new SabnzbdDownloadClient(Sabnzbd);
        var bridge = new LegacyWorkBridge(Db, new WorkService(Db), new WorkStructureService(Db));
        var routing = new LibraryRootRoutingService(Db);
        var availability = new LibraryRootAvailabilityService(Db, new StorageAvailabilityCoordinator());
        var hardLinks = new FileSystemHardLinkCreator();
        var canonicalStorage = new CanonicalMediaStorageService(Db);
        var profileStore = new QualityProfileStore(new DirectoryInfo(Path.Combine(directory.FullName, "quality-profiles")), registry);
        var installed = new InstalledVideoVersions(canonicalStorage, registry, profileStore);
        ICompletedDownloadImportAdapter importer;
        if (Kind == MediaAcquisitionKind.Movie)
        {
            var movies = new MovieLibraryService(Db, bridge);
            importer = new MovieCompletedDownloadImportAdapter(movies, registry, routing, availability, hardLinks, NullLogger<MovieCompletedDownloadImportAdapter>.Instance, canonicalStorage, installed);
        }
        else
        {
            var tv = new TvLibraryService(Db, bridge, new WorkStructureService(Db));
            importer = new TvCompletedDownloadImportAdapter(tv, registry, routing, availability, hardLinks, NullLogger<TvCompletedDownloadImportAdapter>.Instance, canonicalStorage, installed);
        }

        var collection = new ServiceCollection()
            .AddSingleton(Db)
            .AddSingleton<TimeProvider>(Clock)
            .AddSingleton(coordinator)
            .AddSingleton(registry)
            .AddSingleton(profileStore)
            .AddSingleton(installed)
            .AddSingleton<IWantedSource>(new VideoUpgradeWantedSource(Kind, Db, new AcquisitionAccessStore(Db), installed, profileStore, new UpgradeScanState()))
            .AddSingleton(downloadClients)
            .AddSingleton(new DownloadClientSubmissionService(downloadClient, new DownloadClientSelector(downloadClients, health), Db, NullLogger<DownloadClientSubmissionService>.Instance))
            .AddSingleton<IDownloadClient>(downloadClient)
            .AddSingleton(new AnimeImportSettingsStore(directory.FullName))
            .AddSingleton(new AcquisitionAccessStore(Db))
            .AddSingleton(AcquisitionAccessFixture.Account(Owner, AccountRole.Owner))
            .AddSingleton<ReleaseRequestTracker>()
            .AddSingleton<VideoRequestWorkResolver>()
            .AddSingleton<VideoAcquisitionEngine>()
            .AddSingleton<IJularrEventPublisher, RecordingEventPublisher>()
            .AddSingleton<IMediaCapabilityService>(new MediaCapabilityService(Pages.Capabilities))
            .AddSingleton(new AcquisitionRequestSettingsStore(directory.FullName))
            .AddSingleton<AcquisitionRequestService>()
            .AddSingleton<ICompletedDownloadImportAdapter>(importer)
            .AddSingleton<CompletedDownloadDispatcher>()
            .AddSingleton<CompletedDownloadImportService>()
            .AddSingleton<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>()
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        if (Kind == MediaAcquisitionKind.Movie)
        {
            collection.AddSingleton<IAcquisitionRequestExecutor, MovieAcquisitionRequestExecutor>().AddSingleton<IWantedRequestHandler, MovieWantedRequestHandler>();
        }
        else
        {
            collection.AddSingleton<IAcquisitionRequestExecutor, TvAcquisitionRequestExecutor>().AddSingleton<IWantedRequestHandler, TvWantedRequestHandler>();
        }

        return collection.BuildServiceProvider();
    }
}

/// <summary>Assertions every request-to-play scenario shares, so Anime, Movie and TV are held to the same contract.</summary>
internal static class RequestToPlayAssert
{
    /// <summary>Like <see cref="StringAssert.Contains(string, string)"/>, but a failing page reports only the part the test looked for.</summary>
    public static void Contains(string? text, string expected, string? message = null) =>
        Assert.IsTrue(text?.Contains(expected, StringComparison.Ordinal) == true, $"Expected '{expected}' in a {text?.Length ?? 0}-character response. {message}");

    /// <summary>The request in the owner's Admin queue lands in the tab its status belongs to, with the status name the pages show.</summary>
    public static void AdminQueueProjectsTheRequest(AcquisitionRequest request)
    {
        var page = AdminRequestQuery.Build([request], new AdminRequestFilter(AdminRequestTab.All), new Dictionary<string, string>());
        Assert.AreEqual(request.Id, Assert.ContainsSingle(page.Items).Id);
        Assert.AreEqual(1, page.TabCounts[AdminRequestQuery.TabOf(request.Status)], $"{request.Kind} request {request.Status} must be counted in its lifecycle tab.");
        Assert.AreEqual(request.Status, AdminRequestQuery.TryParseStatus(AcquisitionAccessNames.Status(request.Status)));
    }

    /// <summary>What the consumer's live request card reads from the backend: status name, percent derived from the operation and the result destination.</summary>
    public static async Task<JsonElement> ConsumerStatusAsync(DiscoverIndexModel page, Guid requestId)
    {
        var result = await page.OnGetRequestStatusAsync(requestId, CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(((JsonResult)result).Value));
        return document.RootElement.Clone();
    }

    /// <summary>No page, response or model carries a host path: media is addressed by Work, episode and file ids only.</summary>
    public static void NoHostPathIn(string text, params string[] roots)
    {
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            Assert.IsFalse(text.Contains(root, StringComparison.OrdinalIgnoreCase), $"Host path '{root}' leaked into the response.");
            Assert.IsFalse(text.Contains(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase), $"Host path '{root}' leaked into the response.");
        }
    }
}

/// <summary>The Discover page model on a real database with the real request service: the one entry point of every request.</summary>
internal static class DiscoverPageFactory
{
    public static TmdbDiscoveryProvider Tmdb(AppDbContext db, HttpMessageHandler handler)
    {
        var clock = TimeProvider.System;
        var works = new WorkService(db);
        var structure = new WorkStructureService(db);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
        var health = new ProviderHealthTracker(clock);
        var executor = new ProviderExecutor(new ProviderRateLimiter(), health, clock, NullLogger<ProviderExecutor>.Instance);
        var metadata = new Jularr.Web.Features.Metadata.WorkMetadataRefreshQueue(new WorkMetadataStore(db), new Jularr.Web.Features.Metadata.WorkMetadataRefreshSignal(), clock);
        return new TmdbDiscoveryProvider(http, TmdbTestSupport.Credentials(), executor, health, new ProviderResponseCache(clock), works, structure, db, new LegacyWorkBridge(db, works, structure), metadata);
    }

    /// <param name="tmdb">Only Movie and TV requests resolve through TMDB; the Anime request does not need it.</param>
    public static DiscoverIndexModel Create(
        AppDbContext db,
        TmdbDiscoveryProvider? tmdb,
        IEnumerable<IAcquisitionRequestExecutor> executors,
        MediaCapabilityStore capabilities,
        string settingsDirectory,
        string profileId,
        AccountRole role)
    {
        var account = AcquisitionAccessFixture.Account(profileId, role);
        var settings = new AcquisitionRequestSettingsStore(settingsDirectory);
        var store = new AcquisitionAccessStore(db);
        var requests = new AcquisitionRequestService(store, executors, account, new MediaCapabilityService(capabilities), settings, new RecordingEventPublisher(), NullLogger<AcquisitionRequestService>.Instance);
        var scopes = new VideoRequestScopeResolver(db);
        var page = new DiscoverIndexModel(null!, null!, tmdb!, db, null!, null!, account, null!, requests, store, scopes, null!, null!, null!, null!, null!, NullLogger<DiscoverIndexModel>.Instance);
        var requestServices = new ServiceCollection().AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>().BuildServiceProvider();
        page.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext { User = AcquisitionAccessFixture.Principal(profileId, role), RequestServices = requestServices },
            ViewData = new ViewDataDictionary<DiscoverIndexModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        };
        return page;
    }
}
