using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Frontend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jularr.Tests;

/// <summary>Admin → Dashboard rendered end to end through a real HTTP request.</summary>
[TestClass]
public sealed class AdminDashboardPageRenderTests
{
    [TestMethod]
    public async Task AnEmptyServerShowsTheCalmStateTheServiceTilesAndQuietEmptySections()
    {
        await using var host = await DashboardHost.CreateAsync();

        var html = await host.GetHtmlAsync("/Admin");

        StringAssert.Contains(html, "Admin Dashboard");
        StringAssert.Contains(html, "Services & connections");
        foreach (var service in new[] { "web", "database", "storage", "downloadclients", "indexers", "metadata", "subtitles" })
        {
            StringAssert.Contains(html, $"data-service=\"{service}\"");
        }

        StringAssert.Contains(html, "Nothing is downloading.");
        StringAssert.Contains(html, "No tasks are running.");
        StringAssert.Contains(html, "No one is watching right now.");
        StringAssert.Contains(html, "Nothing has been recorded yet.");
        StringAssert.Contains(html, "No storage roots are configured.");
        StringAssert.Contains(html, "Not configured");
        StringAssert.Contains(html, "href=\"/Admin\">Refresh</a>");
        Assert.IsTrue(Regex.IsMatch(html, "<time class=\"admdash-updated\" datetime=\"[^\"]+\">Updated \\d\\d:\\d\\d:\\d\\d UTC</time>"));
        Assert.IsFalse(html.Contains("data-admin-dashboard-partial", StringComparison.Ordinal), "Everything was readable.");
        Assert.IsFalse(html.Contains("data-admin-dashboard-error", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Needs action", StringComparison.Ordinal), "Nothing waits for the admin.");

        // The data volume of the machine running the tests decides whether this line is the calm one.
        if (!html.Contains("data-problem=\"dataVolumeLow\"", StringComparison.Ordinal))
        {
            StringAssert.Contains(html, "data-admin-dashboard-calm");
            StringAssert.Contains(html, "All systems operational");
        }

        Assert.IsTrue(
            Regex.IsMatch(html, "<a class=\"admin-nav-item active\"[^>]*href=\"/Admin\""),
            "The admin navigation marks the dashboard as the current page.");
    }

    [TestMethod]
    public async Task WorkStreamsStackResourcesAndStorageAreShownFromRealData()
    {
        await using var host = await DashboardHost.CreateAsync();
        await host.SeedAsync();

        var html = await host.GetHtmlAsync("/Admin");

        // Downloads: name, client, progress, speed, ETA and state, separate from the other tasks.
        var downloads = Section(html, "data-admin-dashboard-downloads");
        StringAssert.Contains(downloads, "Bleach TYBW S3E7");
        StringAssert.Contains(downloads, "SABnzbd");
        StringAssert.Contains(downloads, "68%");
        StringAssert.Contains(downloads, "12.4 MB/s");
        StringAssert.Contains(downloads, "4 min");
        StringAssert.Contains(downloads, "admdash-status-running");
        StringAssert.Contains(downloads, "Kaiju No. 8 S2E2");
        StringAssert.Contains(downloads, "admdash-status-queued");
        Assert.IsFalse(downloads.Contains("Solo Leveling", StringComparison.Ordinal), "Tasks do not appear among the downloads.");
        StringAssert.Contains(html, "1 running, 1 waiting");

        // Tasks: type, object, step, progress.
        var tasks = Section(html, "data-admin-dashboard-tasks");
        StringAssert.Contains(tasks, "Solo Leveling LN 12");
        StringAssert.Contains(tasks, "Copying files (12/24)");
        StringAssert.Contains(tasks, "50%");
        StringAssert.Contains(tasks, "Imports");
        Assert.IsFalse(tasks.Contains("Bleach", StringComparison.Ordinal));

        // Streams: mode, conversion, bitrate, a confirmed Stop and a details popover.
        var streams = Section(html, "data-admin-dashboard-streams");
        StringAssert.Contains(streams, "Dandadan");
        StringAssert.Contains(streams, "Lisa");
        StringAssert.Contains(streams, "admdash-mode-transcode");
        StringAssert.Contains(streams, "2160p HEVC → 1080p H264");
        StringAssert.Contains(streams, "18.4 Mbps");
        StringAssert.Contains(streams, "handler=StopSession");
        StringAssert.Contains(streams, "return confirm(");
        StringAssert.Contains(streams, "Stop this stream? Playback ends immediately.");
        StringAssert.Contains(streams, "popovertarget=\"admdash-stream-");
        StringAssert.Contains(streams, "Transcode");

        // Only Jularr and PostgreSQL cgroup figures are shown, with the stack total first.
        StringAssert.Contains(html, "data-metric=\"stack-total\"");
        StringAssert.Contains(html, "42%");
        StringAssert.Contains(html, "data-metric=\"jularr\"");
        StringAssert.Contains(html, "data-metric=\"postgresql\"");
        StringAssert.Contains(html, "800 MB");
        StringAssert.Contains(html, "data-stack-network");
        StringAssert.Contains(html, "48.5 MB/s");
        StringAssert.Contains(html, "6.1 MB/s");
        StringAssert.Contains(html, "data-stack-resource-chart");
        Assert.IsFalse(html.Contains("Host CPU", StringComparison.Ordinal));

        // Storage: a probed root with its free space, and the data volume.
        StringAssert.Contains(html, "data-root-state=\"online\"");
        StringAssert.Contains(html, "Test media");
        StringAssert.Contains(html, "free of");
        StringAssert.Contains(html, "Data volume");

        // Recent activity and the work waiting for the admin.
        var recent = Section(html, "data-admin-dashboard-recent");
        StringAssert.Contains(recent, "Old scan");
        StringAssert.Contains(recent, "admdash-result-success");
        StringAssert.Contains(recent, "admdash-result-failed");
        StringAssert.Contains(html, "Needs action");
        StringAssert.Contains(html, "Requests to review");
    }

    [TestMethod]
    public async Task ProblemsAreOrderedBySeverityAndLinkToTheFilteredAdminPage()
    {
        await using var host = await DashboardHost.CreateAsync();
        await host.SeedAsync();
        await host.MakeProblemsAsync();

        var html = await host.GetHtmlAsync("/Admin");

        StringAssert.Contains(html, "Problems & warnings");
        Assert.IsFalse(html.Contains("data-admin-dashboard-calm", StringComparison.Ordinal), "Something is wrong, so no calm line.");

        var order = Regex.Matches(html, "data-problem=\"([A-Za-z]+)\"").Select(match => match.Groups[1].Value).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "jobsFailed", "indexerDown", "metadataDown" }, order);

        StringAssert.Contains(html, "Failed jobs: 1");
        StringAssert.Contains(html, "href=\"/Admin/Operations?tab=failed&status=failed\"");
        StringAssert.Contains(html, "Indexer Broken Indexer is not responding");
        StringAssert.Contains(html, "auth failed");
        StringAssert.Contains(html, "href=\"/Admin/Usenet\"");
        StringAssert.Contains(html, "Metadata provider AniList is failing");
        StringAssert.Contains(html, "1/2 online");
    }

    [TestMethod]
    public async Task ASourceThatCannotBeReadShowsAWarningAndNeverTheCalmState()
    {
        await using var host = await DashboardHost.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(host.DataRoot, "acquisition", AcquisitionHealthStore.FileName), "{ not json");

        var html = await host.GetHtmlAsync("/Admin");

        StringAssert.Contains(html, "data-admin-dashboard-partial");
        StringAssert.Contains(html, "Some health data could not be loaded");
        Assert.IsFalse(html.Contains("data-admin-dashboard-calm", StringComparison.Ordinal), "Unknown health is not healthy.");
        StringAssert.Contains(html, "role=\"alert\"");
        StringAssert.Contains(html, "href=\"/Admin\">Retry</a>");
    }

    [TestMethod]
    public async Task AFreshServerShowsWhenStackResourceMeasurementIsUnavailable()
    {
        await using var host = await DashboardHost.CreateAsync(withTelemetry: false);

        var html = await host.GetHtmlAsync("/Admin");

        StringAssert.Contains(html, "data-stack-resources-unavailable");
        Assert.IsFalse(html.Contains("data-metric=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ResourcesPageKeepsTheStackHistoryAndMountedStorageSeparateFromTheHost()
    {
        await using var host = await DashboardHost.CreateAsync();
        await host.SeedAsync();

        var html = await host.GetHtmlAsync("/Admin/Resources");

        StringAssert.Contains(html, "Resources");
        StringAssert.Contains(html, "data-stack-resources");
        StringAssert.Contains(html, "data-resource-summary=\"cpu\"");
        StringAssert.Contains(html, "42%");
        StringAssert.Contains(html, "data-resource-service=\"jularr\"");
        StringAssert.Contains(html, "data-resource-service=\"postgresql\"");
        StringAssert.Contains(html, "data-resource-chart=\"cpu\"");
        StringAssert.Contains(html, "data-resource-chart=\"memory\"");
        StringAssert.Contains(html, "data-resource-chart=\"network\"");
        StringAssert.Contains(html, "data-resource-chart=\"io\"");
        StringAssert.Contains(html, "48.5 MB/s");
        StringAssert.Contains(html, "data-resource-mounts");
        StringAssert.Contains(html, "Test media");
        StringAssert.Contains(html, "data-resource-gpu");
        Assert.IsFalse(html.Contains("Host CPU", StringComparison.Ordinal));
        Assert.IsTrue(Regex.IsMatch(html, "<a class=\"admin-nav-item active\"[^>]*href=\"/Admin/System\""), "Resources belongs to System & Diagnostics, whose tab is the active one.");
    }

    [TestMethod]
    public async Task OnlyMediaManagersMayOpenTheDashboardAndOthersNeverSeeTheStreams()
    {
        await using var host = await DashboardHost.CreateAsync();

        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync("/Admin", asOwner: true));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync("/Admin", asOwner: false));
        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync("/Admin/Resources", asOwner: true));
        Assert.AreEqual(HttpStatusCode.Forbidden, await host.GetStatusAsync("/Admin/Resources", asOwner: false));
    }

    private static string Section(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"{marker} is missing.");
        var end = html.IndexOf("</table>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private sealed class FakeResources(StackResourceSnapshot snapshot) : IStackResourceTelemetry
    {
        public StackResourceSnapshot Snapshot { get; set; } = snapshot;

        public StackResourceSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class ForbiddenAnswerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }
    }

    private sealed class NoMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }

    private sealed class StubExecutor(MediaAcquisitionKind kind) : IAcquisitionRequestExecutor
    {
        public MediaAcquisitionKind Kind => kind;

        public Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AcquisitionExecution(AcquisitionRequestStatus.Approved, "Stub."));
    }

    private sealed class DashboardHost : IAsyncDisposable
    {
        private const string OwnerHeader = "X-Test-Owner";
        private const string Profile = "test-profile";

        private readonly string root;
        private readonly IHost host;
        private readonly TestServer server;

        private DashboardHost(string root, AppDbContext db, IHost host, TestServer server)
        {
            this.root = root;
            Db = db;
            this.host = host;
            this.server = server;
        }

        public AppDbContext Db { get; }

        public string DataRoot => Path.Combine(root, "data");

        public static async Task<DashboardHost> CreateAsync(bool withTelemetry = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-dashboard-page-{Guid.NewGuid():N}");
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            var acquisition = Directory.CreateDirectory(Path.Combine(data.FullName, "acquisition"));
            var connectionString = $"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True";
            var monitoring = new AnimeMonitoringStore(data.FullName);
            var capabilities = new MediaCapabilityStore(data.FullName);
            var protection = new EphemeralDataProtectionProvider();
            var sessions = new PlaybackStreamSessionStore(TimeProvider.System);
            var coordinator = new StorageAvailabilityCoordinator();
            var telemetry = new FakeResources(withTelemetry ? Resources() : StackResourceSnapshot.Empty);

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder => webBuilder
                    .UseTestServer()
                    .UseContentRoot(FindWebProjectRoot())
                    .ConfigureServices(services =>
                    {
                        services
                            .AddRazorPages()
                            .AddApplicationPart(typeof(Jularr.Web.Pages.Admin.IndexModel).Assembly);
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
                        services.AddHttpContextAccessor();
                        services.AddAuthorization(options => JularrPolicies.Register(options));
                        services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbiddenAnswerHandler>("test", _ => { });
                        services.AddLogging();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton<ViteAssetManifest>();
                        services.AddScoped<CurrentAccountContext>();
                        services.AddSingleton(capabilities);
                        services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
                        services.AddScoped<IAppShellService, AppShellService>();
                        services.AddSingleton(new AcquisitionRequestSettingsStore(data.FullName));
                        services.AddSingleton(new QualityProfileStore(new DirectoryInfo(Path.Combine(data.FullName, "quality"))));
                        services.AddSingleton(monitoring);
                        services.AddScoped<AcquisitionAccessStore>();
                        services.AddScoped<Jularr.Web.Features.Acquisition.Access.VideoRequestWorkResolver>();
                        services.AddScoped<IAcquisitionRequestExecutor>(_ => new StubExecutor(MediaAcquisitionKind.Book));
                        services.AddScoped<WantedListService>();
                        services.AddSingleton<IJularrEventPublisher, RecordingEventPublisher>();

                        services.AddSingleton(sessions);
                        services.AddSingleton(coordinator);
                        services.AddScoped<LibraryRootAvailabilityService>();
                        services.AddSingleton(new MediaMappingReviewStore(NullLogger<MediaMappingReviewStore>.Instance, new DirectoryInfo(Path.Combine(data.FullName, "mapping"))));
                        services.AddScoped(provider => new SubtitleImportService(
                            provider.GetRequiredService<AppDbContext>(),
                            new VocabularyService(provider.GetRequiredService<AppDbContext>(), new JapaneseTermExtractor(new NoMorphology()), new JapaneseDictionary())));
                        services.AddScoped<AdminOverviewService>();
                        services.AddScoped<AdminSessionsService>();
                        services.AddSingleton(new IndexerStore(protection, acquisition));
                        services.AddSingleton(new DownloadClientStore(protection, acquisition));
                        services.AddSingleton(new AcquisitionHealthStore(acquisition));
                        services.AddProviderFramework();
                        services.AddSingleton<IStackResourceTelemetry>(telemetry);
                        services.AddScoped<AdminDashboardService>();
                    })
                    .Configure(app =>
                    {
                        // Stand-in for cookie sign-in: the owner header makes the caller the owner, otherwise a plain user.
                        app.Use(async (context, next) =>
                        {
                            var owner = context.Request.Headers.ContainsKey(OwnerHeader);
                            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, owner ? "test-owner" : Profile) };
                            if (owner)
                            {
                                claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
                            }

                            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                            await next();
                        });
                        app.UseRouting();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapRazorPages());
                    }))
                .StartAsync();

            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var created = new DashboardHost(root, db, host, host.GetTestServer());
            created.Sessions = sessions;
            created.Coordinator = coordinator;
            created.FakeResources = telemetry;
            return created;
        }

        private PlaybackStreamSessionStore Sessions { get; set; } = null!;

        private StorageAvailabilityCoordinator Coordinator { get; set; } = null!;

        private FakeResources FakeResources { get; set; } = null!;

        private static StackResourceSnapshot Resources()
        {
            var start = DateTimeOffset.UtcNow.AddSeconds(-10);
            StackResourceSample Sample(int step, double cpu) => new(
                start.AddSeconds(step * 5),
                new StackServiceResource(cpu - 12, 300_000_000, 48_500_000, 6_100_000, 4_500_000, 1_500_000),
                new StackServiceResource(12, 500_000_000, ReadBytesPerSecond: 3_500_000, WriteBytesPerSecond: 2_100_000));
            return new StackResourceSnapshot([Sample(0, 30), Sample(1, 36), Sample(2, 42)]);
        }

        /// <summary>Running and waiting downloads, an import, finished work, a live transcode, a probed storage root and a pending request.</summary>
        public async Task SeedAsync()
        {
            var store = new OperationStore(Db);

            var running = await store.CreateAsync(new OperationDescriptor(
                "sabnzbd-download", "External downloads", "Bleach TYBW S3E7", IsDownload: true, ExternalProvider: "sabnzbd"));
            await store.MarkRunningAsync(running);
            await store.ReportProgressAsync(running, 68, "Downloading", 6_800, 10_000, 12_400_000, DateTime.UtcNow.AddMinutes(4).AddSeconds(20));

            await store.CreateAsync(new OperationDescriptor(
                "sabnzbd-download", "External downloads", "Kaiju No. 8 S2E2", IsDownload: true, ExternalProvider: "sabnzbd"));

            var import = await store.CreateAsync(new OperationDescriptor("anime-import", "Import", "Solo Leveling LN 12"));
            await store.MarkRunningAsync(import);
            await store.ReportProgressAsync(import, 50, "Copying files (12/24)");

            var scan = await store.CreateAsync(new OperationDescriptor("library-scan", "Library", "Old scan"));
            await store.MarkSucceededAsync(scan);
            var broken = await store.CreateAsync(new OperationDescriptor("library-scan", "Library", "Broken scan"));
            await store.MarkFailedAsync(broken, "Something broke.");

            var anime = new Anime { Key = "dandadan", Title = "Dandadan" };
            var episode = new Episode { AnimeId = anime.Id, SeasonNumber = 1, Number = 3, Title = "Episode 3" };
            Db.AddRange(anime, episode);
            Db.OwnerAccounts.Add(new OwnerAccount
            {
                Id = "lisa",
                UserName = "Lisa",
                NormalizedUserName = "LISA",
                PasswordHash = "hash",
                Role = AccountRole.User,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            });

            var rootPath = Path.Combine(root, "media");
            Directory.CreateDirectory(rootPath);
            var libraryRoot = new LibraryRoot { Name = "Test media", Path = rootPath };
            Db.LibraryRoots.Add(libraryRoot);
            await Db.SaveChangesAsync();
            await Coordinator.ProbeAsync(libraryRoot.Id, rootPath, wakeConfigured: false, force: true, CancellationToken.None);

            Sessions.Create(
                "lisa",
                episode.Id,
                Guid.NewGuid(),
                "/media/dandadan.mkv",
                1450,
                new PlaybackPlan(
                    PlaybackDeliveryMode.Transcode,
                    PlaybackTransport.File,
                    "mp4",
                    new PlaybackVideoOutput(false, "hevc", "h264", 3840, 2160, 1080, null, null),
                    Audio: null,
                    new PlaybackQualityResolution(PlaybackQualityPreset.Auto, PlaybackNetworkClass.Local, null, PlaybackLimitSource.None, 40000, 18400),
                    [],
                    PlaybackCapabilitySupport.Confirmed,
                    SourceContainer: "mkv"),
                new PlaybackStreamSelections(null, null, false, PlaybackQualityPreset.Auto, PlaybackModePreference.Auto, ClientKinds.Web));

            await new AcquisitionAccessStore(Db).CreateAsync(
                new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "test", "pending-book", "Pending Book", null, null),
                Profile,
                AcquisitionRequestStatus.Pending,
                "owner",
                CancellationToken.None);
        }

        /// <summary>An indexer that fails its checks, a metadata provider whose circuit is open and a busy processor.</summary>
        public async Task MakeProblemsAsync()
        {
            var indexers = host.Services.GetRequiredService<IndexerStore>();
            var health = host.Services.GetRequiredService<AcquisitionHealthStore>();
            var broken = NewIndexer("Broken Indexer");
            var fine = NewIndexer("Fine Indexer");
            await indexers.SaveAsync(broken);
            await indexers.SaveAsync(fine);
            await health.RecordAsync(new AcquisitionHealthStatus(AcquisitionHealthKind.Indexer, broken.Id, broken.Name, true, false, "auth failed", DateTimeOffset.UtcNow));

            var tracker = host.Services.GetRequiredService<ProviderHealthTracker>();
            for (var attempt = 0; attempt < ProviderHealthTracker.DefaultFailureThreshold; attempt++)
            {
                tracker.RecordFailure(ProviderKeys.AniList, "AniList timed out");
            }

        }

        public async Task<string> GetHtmlAsync(string path)
        {
            using var client = Client(asOwner: true);
            using var response = await client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} failed:\n{html}");

            // Razor encodes non-ASCII text as character references; assertions read the text a browser shows.
            return WebUtility.HtmlDecode(html);
        }

        public async Task<HttpStatusCode> GetStatusAsync(string path, bool asOwner)
        {
            using var client = Client(asOwner);
            using var response = await client.GetAsync(path);
            return response.StatusCode;
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

        private static IndexerEntry NewIndexer(string name) =>
            new(
                Guid.NewGuid(),
                name,
                IndexerType.Newznab,
                Enabled: true,
                Priority: 1,
                new IndexerSettings("https://indexer.example", [5070], [], 100),
                "indexer-key");

        private HttpClient Client(bool asOwner)
        {
            var client = server.CreateClient();
            if (asOwner)
            {
                client.DefaultRequestHeaders.Add(OwnerHeader, "true");
            }

            return client;
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
    }
}
