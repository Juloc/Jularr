using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.AniListAutoMonitor;
using Jularr.Web.Features.Acquisition.Backup;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Storage.FolderBrowse;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Frontend;
using Jularr.Web.Pages.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Renders real Jularr.Web Razor Pages end to end (real routing, tag helpers, the compiled
/// _ManageSheet partial) through a real HTTP request, so tests can assert on the exact HTML a
/// browser would receive for an owner vs. a normal user (#519, part of epic #510). Everything
/// registered here is the same concrete production service Program.cs wires for these pages,
/// minus the pieces that need a real network/root filesystem (AniList's HttpClient throws if a
/// test path ever calls out; a GET never should).
/// </summary>
internal sealed class ManageSheetPageTestHost : IAsyncDisposable
{
    public const string OwnerRoleHeader = "X-Test-Owner";
    public const string MediaManagerRoleHeader = "X-Test-Media-Manager";

    private readonly string root;
    private readonly IHost host;
    private readonly TestServer server;

    private ManageSheetPageTestHost(string root, AppDbContext db, IHost host, TestServer server, InstanceModuleStore modules)
    {
        this.root = root;
        Db = db;
        this.host = host;
        this.server = server;
        Modules = modules;
    }

    public AppDbContext Db { get; }

    /// <summary>The instance module switches the pages read.</summary>
    public InstanceModuleStore Modules { get; }

    /// <summary>The host's services, to seed the stores a page reads.</summary>
    public IServiceProvider Services => host.Services;

    /// <param name="sharedDatabasePath">The database path of another test host: both then serve the same PostgreSQL database, as one install does.</param>
    public static async Task<ManageSheetPageTestHost> CreateAsync(string? sharedDatabasePath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-manage-sheet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dataDirectory = new DirectoryInfo(Path.Combine(root, "data"));
        dataDirectory.Create();
        var modules = new InstanceModuleStore(dataDirectory.FullName);
        var databasePath = sharedDatabasePath ?? Path.Combine(root, "jularr.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=True";

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(AnimeModel).Assembly);

                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        // The shell sidebar derives its media-type destinations from the profile's
                        // capabilities (#598); the policy defaults to "everything visible".
                        services.AddSingleton(new MediaCapabilityStore(dataDirectory.FullName));
                        services.AddSingleton<IInstanceModuleService>(modules);
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddScoped<OperationRunner>();
                        services.AddScoped<EpisodeProgressService>();
                        services.AddScoped<VideoProgressService>();
                        services.AddScoped<CanonicalVideoTargetResolver>();
                        services.AddScoped<LegacyWorkBridge>();
                        services.AddScoped<WorkService>();
                        services.AddScoped<WorkStructureService>();
                        services.AddScoped<FranchiseStore>();
                        services.AddScoped<MediaRelationStore>();
                        // The Library page's Collections view lists the profile's collections.
                        services.AddScoped<Jularr.Web.Features.MediaFacts.MediaFactsService>();
                        services.AddCollections();
                        // AnimeModel's GET only reads existing franchise membership (#425); it
                        // never triggers a refresh, so the relation source and rate limiter below
                        // are unused placeholders, same reasoning as the AniList HttpClient below.
                        services.AddScoped<IFranchiseRelationSource, NoopFranchiseRelationSource>();
                        services.AddSingleton<AniListRateLimitGate>();
                        services.AddScoped<AniListRequestLimiter>();
                        services.AddSingleton<FranchiseRefreshSignal>();
                        services.AddScoped<FranchiseService>();
                        // The "Upcoming releases" view component every consumer detail page
                        // includes; no IReleaseEventSource is registered, so it just renders empty.
                        services.AddScoped<ReleaseCalendarService>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddScoped<AnimeMetadataService>();
                        // The detail page's watchlist toggle, related works and open-request state.
                        services.AddScoped<WatchlistStore>();
                        services.AddScoped<WatchlistLibraryResolver>();
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<AniListAccountService>();

                        var protectionProvider = new EphemeralDataProtectionProvider();
                        services.AddSingleton(new AniListAccountStore(
                            protectionProvider,
                            NullLogger<AniListAccountStore>.Instance,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "anilist"))));
                        services.AddSingleton(new MediaMappingReviewStore(
                            NullLogger<MediaMappingReviewStore>.Instance,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "mapping-review"))));
                        services.AddSingleton(new ReadingSegmentMappingStore(
                            NullLogger<ReadingSegmentMappingStore>.Instance,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "reading-segments"))));

                        // AnimeModel's GET never reaches the network (progress summary is
                        // local-only; see the comment on ExternalProgress in Anime.cshtml.cs), so
                        // a handler that fails loudly on any actual request is safer than
                        // silently hanging.
                        services.AddSingleton(new HttpClient(new NeverCallHandler())
                        {
                            BaseAddress = new Uri("https://graphql.anilist.co/")
                        });

                        // The owner-only _AnimeAcquisitionPanel (part of the Manage sheet's
                        // Acquisition group) reads AnimeAcquisitionPipeline.GetAnimePanelAsync,
                        // a read-only status view. Nothing is configured (no indexers/download
                        // clients/Sonarr connection), so every store below is real but empty, and
                        // logging is a no-op sink (no provider attached).
                        services.AddLogging();
                        services.AddSingleton<IReadOnlyDictionary<IndexerType, IIndexer>>(
                            new Dictionary<IndexerType, IIndexer>());
                        services.AddSingleton(new IndexerStore(
                            protectionProvider,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "indexers"))));
                        services.AddSingleton(new AcquisitionHealthStore(
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "acquisition-health"))));
                        services.AddSingleton(new SonarrConnectionStore(
                            protectionProvider,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "sonarr"))));
                        services.AddSingleton(new AnimeImportSettingsStore(
                            Path.Combine(dataDirectory.FullName, "import-settings")));
                        services.AddSingleton(new DownloadClientStore(
                            protectionProvider,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "download-clients"))));
                        services.AddSingleton(new AnimeMonitoringStore(
                            Path.Combine(dataDirectory.FullName, "monitoring")));
                        services.AddSingleton(new AcquisitionOwnershipStore(
                            Path.Combine(dataDirectory.FullName, "ownership")));
                        services.AddSingleton(new AnimeQualityProfileStore(
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "quality-profiles"))));
                        services.AddSingleton(new AnimeImportStore(
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "anime-imports"))));
                        services.AddSingleton(new SabnzbdAcquisitionStore(
                            protectionProvider,
                            new DirectoryInfo(Path.Combine(dataDirectory.FullName, "sabnzbd-acquisitions"))));
                        services.AddScoped<ISonarrObserverClient, SonarrObserverClient>();
                        services.AddScoped<ISabnzbdClient>(_ => new SabnzbdClient(
                            new HttpClient(new NeverCallHandler())));
                        services.AddScoped<IDownloadClient, SabnzbdDownloadClient>();
                        services.AddScoped<DownloadClientSelector>();
                        services.AddScoped<DownloadClientSubmissionService>();
                        services.AddScoped<SabnzbdDownloadService>();
                        services.AddScoped<SabnzbdAcquisitionService>();
                        services.AddScoped<SonarrObservationService>();
                        services.AddScoped<IndexerSearchCoordinator>();
                        services.AddScoped<AnimeAcquisitionInventory>();
                        services.AddScoped<Jularr.Web.Features.Storage.LibraryRootRoutingService>();
                        services.AddScoped<AcquisitionHistoryService>();
                        services.AddMonitoringForTests();
                        services.AddScoped<AnimeAcquisitionPipeline>();
                        // The Settings → Acquisition page (#389) lists the per-media-type remote
                        // path mappings; its other panels read the same empty stores.
                        services.AddSingleton(new AniListAutoMonitorSettingsStore(
                            Path.Combine(dataDirectory.FullName, "anilist-auto-monitor")));
                        services.AddScoped(_ => new AcquisitionBackupService(
                            Path.Combine(dataDirectory.FullName, "acquisition-backup")));
                        services.AddScoped<Jularr.Web.Features.Storage.LibraryRootRoutingService>();
                        services.AddScoped<MediaInboxImportService>();
                        // Admin → Storage: usage and cache cleanup read the same inventory and cache folders as in production.
                        services.AddSingleton<Jularr.Web.Features.Storage.StorageAvailabilityCoordinator>();
                        services.AddScoped<Jularr.Web.Features.Storage.StorageIntegrityService>();
                        services.AddSingleton(new Jularr.Web.Features.Storage.Insights.StorageCacheLayout(
                            Path.Combine(dataDirectory.FullName, "cache", "playback"),
                            Path.Combine(dataDirectory.FullName, "cache", "hls"),
                            Path.Combine(dataDirectory.FullName, "cache", "trickplay"),
                            Path.Combine(dataDirectory.FullName, "cache", "artwork"),
                            Path.Combine(dataDirectory.FullName, "cache", "fingerprints"),
                            Path.Combine(dataDirectory.FullName, "cache", "manga")));
                        services.AddScoped<Jularr.Web.Features.Storage.LibraryRootAvailabilityService>();
                        services.AddScoped<Jularr.Web.Features.Storage.Insights.StorageUsageService>();
                        services.AddScoped<Jularr.Web.Features.Storage.Insights.StorageCacheScanner>();
                        services.AddScoped<Jularr.Web.Features.Storage.Insights.StorageCleanupService>();
                        // The same folder browser Program.cs registers; the page reads its checks.
                        services.AddFolderBrowse(dataDirectory.FullName);
                        services.AddHttpClient();
                    })
                    .Configure(app =>
                    {
                        // Stand-in for real cookie authentication: the test picks owner vs.
                        // normal user with a header instead of signing in, everything downstream
                        // (CurrentAccountContext, the page views' Model.IsOwner) behaves exactly
                        // like a real signed-in request.
                        app.Use(async (context, next) =>
                        {
                            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-profile") };
                            if (context.Request.Headers.ContainsKey(OwnerRoleHeader))
                            {
                                claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
                            }

                            if (context.Request.Headers.ContainsKey(MediaManagerRoleHeader))
                            {
                                claims.Add(new Claim(ClaimTypes.Role, AccountRoles.MediaManager));
                            }

                            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                            await next();
                        });
                        app.UseRouting();
                        // Pages that carry a role policy (Settings → Acquisition) need the
                        // authorization middleware; the roles come from the simulated account.
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapRazorPages());
                    });
            })
            .StartAsync();

        var server = host.GetTestServer();

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options);
        await DatabaseMigrationBridge.UpgradeAsync(db);

        return new ManageSheetPageTestHost(root, db, host, server, modules);
    }

    /// <param name="asOwner">Adds the owner role claim to the simulated signed-in account.</param>
    /// <param name="asMediaManager">Adds the media-manager role claim (ignored when <paramref name="asOwner"/>).</param>
    public async Task<string> GetHtmlAsync(string path, bool asOwner, bool asMediaManager = false)
    {
        using var client = server.CreateClient();
        if (asOwner)
        {
            client.DefaultRequestHeaders.Add(OwnerRoleHeader, "true");
        }
        else if (asMediaManager)
        {
            client.DefaultRequestHeaders.Add(MediaManagerRoleHeader, "true");
        }

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(
            System.Net.HttpStatusCode.OK,
            response.StatusCode,
            $"GET {path} (owner={asOwner}) failed:\n{html}");
        return html;
    }

    public async Task<Anime> AddAnimeAsync(string title)
    {
        var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
        Db.Add(anime);
        await Db.SaveChangesAsync();
        return anime;
    }

    public async Task AddAnimeMetadataMatchAsync(Anime anime)
    {
        Db.Add(new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = "anilist",
            ExternalId = "1",
            PreferredTitle = anime.Title
        });
        await Db.SaveChangesAsync();
    }

    public async Task<Episode> AddEpisodeAsync(Anime anime, int season, int number)
    {
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = season,
            Number = number,
            Title = $"Episode {number}"
        };
        Db.Add(episode);
        await Db.SaveChangesAsync();
        return episode;
    }

    public async ValueTask DisposeAsync()
    {
        server.Dispose();
        await host.StopAsync();
        host.Dispose();
        await Db.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Jularr.Web");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    private sealed class NeverCallHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Unexpected outbound request to {request.RequestUri} during a manage-sheet render test.");
    }

    private sealed class NoopFranchiseRelationSource : IFranchiseRelationSource
    {
        public Task<AniListRelatedMedia> GetRelatedAsync(WatchlistIdentity work, CancellationToken cancellationToken) =>
            Task.FromResult(new AniListRelatedMedia(null, []));
    }
}
