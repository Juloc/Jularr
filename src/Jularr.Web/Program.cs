using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Api;
using Jularr.Web.Features.Acquisition.AniListAutoMonitor;
using Jularr.Web.Features.Acquisition.Backup;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.History;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Policy;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.ChapterArtwork;
using Jularr.Web.Features.ClientApi;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Events;
using Jularr.Web.Frontend;
using Jularr.Web.Features.Health;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.MediaSegments;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Notifications;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.OfflineLibrary;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Pairing;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.PlaybackSessions;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Branding;
using Jularr.Web.Features.Performance;
using Jularr.Web.Features.Providers;
using Jularr.Web.Features.ReaderThemes;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Sonarr;
using Jularr.Web.Features.Statistics;
using Jularr.Web.Features.Storage;
using Jularr.Web.Features.Storage.FolderBrowse;
using Jularr.Web.Features.Storage.Insights;
using Jularr.Web.Features.Storage.Reconciliation;
using Jularr.Web.Features.StoryContext;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Infrastructure;
using Jularr.Web.Infrastructure.Ai;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;

Console.WriteLine($"[Jularr] {DateTimeOffset.UtcNow:O} Process starting.");

var builder = WebApplication.CreateBuilder(args);

// Per-media-type consumer routes (/Library, /Reading, /Novels, /Manga, /Books) answer 404 to a
// profile whose capability for that type is Hidden (#598); the navigation catalog is the route table.
builder.Services.AddRazorPages(options => options.Conventions.AddMediaTypeGates());
builder.Services.AddSignalR();
builder.Services.AddSingleton<ViteAssetManifest>();
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));

var dataProtectionDirectory = new DirectoryInfo(builder.Configuration["DataProtection:KeysDirectory"] ?? "/data/keys");
Directory.CreateDirectory(dataProtectionDirectory.FullName);
builder.Services.AddDataProtection()
    .SetApplicationName("Jularr")
    .PersistKeysToFileSystem(dataProtectionDirectory);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OperationProfileContext>();
builder.Services.AddScoped<CurrentAccountContext>();
// Unified event & notification boundary (#429): features publish through IJularrEventPublisher;
// NotificationDispatcher fans a published event out to every subscribed profile's sinks. In-app
// is the only sink today — a webhook/Home Assistant, Web Push or e-mail sink is a follow-up that
// only needs to register another INotificationSink.
builder.Services.AddScoped<EventLogStore>();
builder.Services.AddScoped<NotificationSubscriptionStore>();
builder.Services.AddScoped<NotificationStore>();
builder.Services.AddScoped<INotificationSink, InAppNotificationSink>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<IJularrEventPublisher, JularrEventPublisher>();
builder.Services.AddScoped<OwnerAuthService>();
// Per-media-type capability policy (#436): canonical JSON settings store under /data plus the
// resolution/guard service consumed by the request experience (#597), permission-derived shell
// (#598) and provider-driven discovery (#595).
builder.Services.AddSingleton(_ => new MediaCapabilityStore("/data"));
builder.Services.AddScoped<IMediaCapabilityService, MediaCapabilityService>();
// Instance-wide module switches are the top-level feature gate. Profile capabilities/settings
// only apply after the corresponding module is enabled here.
builder.Services.AddSingleton<IInstanceModuleService>(_ => new InstanceModuleStore("/data"));
// Permission-derived app shell (#598): the profile's visible media types, resolved once per request.
builder.Services.AddScoped<IAppShellService, AppShellService>();
builder.Services.AddDiscovery();
// Explainable cross-media recommendations & continuation shelves (#428): the media-neutral engine's
// composition service, rendered on the shared shelf surface by /Recommendations and Discover.
builder.Services.AddScoped<Jularr.Web.Features.Recommendations.MediaRecommendationService>();
builder.Services.AddScoped<AdminUserProgressService>();
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<AdminSessionsService>();
builder.Services.AddScoped<AdminDashboardService>();
builder.Services.AddApplicationPerformance();
builder.Services.AddSingleton<Jularr.Web.Features.Branding.InstanceBrandingStore>();
builder.Services.AddSingleton<IStackResourceSource, CgroupStackResourceSource>();
builder.Services.AddSingleton<StackResourceTelemetrySampler>();
builder.Services.AddSingleton<IStackResourceTelemetry>(services => services.GetRequiredService<StackResourceTelemetrySampler>());
builder.Services.AddHostedService(services => services.GetRequiredService<StackResourceTelemetrySampler>());
builder.Services.AddScoped<KnownDeviceRegistry>();
builder.Services.AddSingleton<SecurityEventLog>();
builder.Services.AddScoped<SystemHealthService>();
builder.Services.AddHttpClient(GitHubReleaseCheckService.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Jularr/0.1 (+https://github.com/Juloc/Jularr)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
// Singleton: caches the last GitHub release check in memory across requests (#528), never on GET.
builder.Services.AddSingleton<GitHubReleaseCheckService>();
builder.Services.AddScoped<OperationRunner>();
builder.Services.AddScoped<MediaFileReanalysisService>();
builder.Services.AddSingleton<IPasswordHasher<OwnerAccount>, PasswordHasher<OwnerAccount>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Jularr.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var accountId = OwnerAuthService.GetAccountId(context.Principal!);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                context.RejectPrincipal();
                return;
            }

            var cookieSessionVersion = OwnerAuthService.GetSessionVersion(
                context.Principal!);
            var auth = context.HttpContext.RequestServices
                .GetRequiredService<OwnerAuthService>();
            var account = await auth.GetEnabledAccountAsync(
                accountId,
                cookieSessionVersion,
                context.HttpContext.RequestAborted);

            if (account is null)
            {
                context.RejectPrincipal();
                return;
            }

            var currentName = context.Principal?.Identity?.Name;
            var currentRole = context.Principal?.FindFirstValue(ClaimTypes.Role);
            var expectedRole = account.Role.ToString();

            if (!string.Equals(currentName, account.UserName, StringComparison.Ordinal)
                || !string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
                || cookieSessionVersion != account.SessionVersion)
            {
                context.ReplacePrincipal(OwnerAuthService.CreatePrincipal(account));
                context.ShouldRenew = true;
            }
        };
        options.Events.OnRedirectToLogin = async context =>
        {
            if (ClientApiRoutes.IsClientApi(context.Request.Path) ||
                AcquisitionApiRoutes.IsAcquisitionApi(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new ClientErrorResponse(
                        "authentication_required",
                        "Authentication is required for this Jularr client API endpoint."),
                    context.HttpContext.RequestAborted);
                return;
            }

            context.Response.Redirect(context.RedirectUri);
        };
        options.Events.OnRedirectToAccessDenied = async context =>
        {
            if (ClientApiRoutes.IsClientApi(context.Request.Path) ||
                AcquisitionApiRoutes.IsAcquisitionApi(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new ClientErrorResponse(
                        "access_denied",
                        "The authenticated account is not allowed to use this endpoint."),
                    context.HttpContext.RequestAborted);
                return;
            }

            // A signed-in account without the page's policy gets 403. Redirecting to the login
            // page would send it straight back here, because login forwards signed-in accounts.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, AcquisitionApiKeyAuthenticationHandler>(
        AcquisitionApiKeyAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    JularrPolicies.Register(options);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // A client that backs off needs to know for how long (Discover pauses its follow-ups for exactly that time).
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    options.AddPolicy("wake", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 6,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    // Device-code TV pairing (#489, Features/Pairing): start/poll are anonymous (a TV has no
    // session yet), so both are scoped by IP; approve runs in an authenticated browser/app
    // session and is scoped by that account, matching "wake" above.
    options.AddPolicy("pairing-start", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    options.AddPolicy("pairing-approve", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    // Higher than "pairing-start": the TV polls roughly every DevicePairingStore.PollIntervalSeconds
    // for up to DevicePairingStore.PairingLifetime, so one pairing attempt needs ~60 polls.
    options.AddPolicy("pairing-poll", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 40,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    // Scoped by API key id when the request authenticated with one, otherwise by IP (a cookie
    // owner session sharing the browser's normal traffic does not need its own partition).
    options.AddPolicy("acquisitionApi", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("fc00::/7"));
});

builder.Services.AddSingleton<MediaProcessRunner>();
builder.Services.AddSingleton<IMediaProcessRunner>(services => services.GetRequiredService<MediaProcessRunner>());
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<CanonicalMediaStorageService>();
builder.Services.AddScoped<CanonicalVideoStorageBackfillService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Selection.InstalledVideoVersions>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Selection.ReleaseReliabilityService>();
builder.Services.AddSingleton<IMediaProbeRunner, FfprobeMediaProbeRunner>();
builder.Services.AddSingleton<MediaInventoryService>();
builder.Services.AddSingleton<IMediaContainerRemuxer, FfmpegMediaContainerRemuxer>();
builder.Services.AddSingleton(_ => new MediaOptimizationJournal("/data/media-optimization"));
builder.Services.AddSingleton<MediaOptimizationQueue>();
builder.Services.AddScoped<MediaContainerOptimizer>();
builder.Services.AddScoped<IMediaFileReplacementParticipant, AcquisitionMediaReplacementParticipant>();
builder.Services.AddHostedService<MediaOptimizationRecoveryService>();
builder.Services.AddSingleton<LibraryScanCoordinator>();
builder.Services.AddHostedService<LibraryStartupScanService>();
builder.Services.AddHostedService<LibraryWatchService>();
builder.Services.AddSingleton<StorageAvailabilityCoordinator>();
builder.Services.AddFolderBrowse("/data");
builder.Services.AddScoped<LibraryReconciliationPlanService>();
builder.Services.AddSingleton<IWakeOnLanPacketSender, UdpWakeOnLanPacketSender>();
builder.Services.AddSingleton(new StorageWakeOptions());
builder.Services.AddSingleton<StorageWakeCoordinator>();
builder.Services.AddScoped<StorageIntegrityService>();
builder.Services.AddScoped<LibraryRootRoutingService>();
// The HLS cache lives where the Admin configured it, so the layout is resolved per request instead of frozen at startup.
builder.Services.AddScoped(services => StorageCacheLayout.Default with { HlsRoot = services.GetRequiredService<Jularr.Web.Features.Playback.Transcoding.PlaybackTranscodingSettingsStore>().Current.HlsCachePath });
builder.Services.AddScoped<StorageUsageService>();
builder.Services.AddScoped<StorageCacheScanner>();
builder.Services.AddScoped<StorageCleanupService>();
builder.Services.AddScoped<LibraryRootAvailabilityService>();
builder.Services.AddScoped<MediaAvailabilityService>();
builder.Services.AddScoped<WakeOnLanService>();
builder.Services.AddScoped<SubtitleImportService>();
builder.Services.AddSingleton<EmbeddedSubtitleExtractor>();
builder.Services.AddScoped<SubtitleLanguageProfileService>();
builder.Services.AddScoped<SubtitleCompletenessService>();
builder.Services.AddSubtitleProviders();
builder.Services.AddScoped<VocabularyService>();
builder.Services.Configure<JapaneseMorphologyOptions>(builder.Configuration.GetSection(JapaneseMorphologyOptions.SectionName));
builder.Services.AddSingleton<IJapaneseMorphology, MeCabJapaneseMorphology>();
builder.Services.AddSingleton<JapaneseTermExtractor>();
builder.Services.AddSingleton<JapaneseDictionary>();
builder.Services.AddSingleton<IReviewScheduler, FsrsReviewScheduler>();
builder.Services.AddScoped<LearningService>();
builder.Services.AddSingleton<LanguageTextAnalyzer>();
builder.Services.AddScoped<LanguageInspectorService>();
builder.Services.AddScoped<EpisodePreparationService>();
builder.Services.AddScoped<LearningStatisticsService>();
builder.Services.AddSingleton<PlaybackCueProjector>();
builder.Services.AddSingleton<PlaybackPreparationTracker>();
builder.Services.AddScoped<PlaybackPreparationService>();
builder.Services.AddScoped<PlaybackService>();
Jularr.Web.Features.Playback.Decision.PlaybackDecisionRegistration.AddPlaybackDecision(builder.Services);
Jularr.Web.Features.InstantPlay.InstantPlayRegistration.AddInstantPlay(builder.Services);
// Universal media core (#592): the provider-independent work/identity model the #556 children build on.
Jularr.Web.Features.MediaCore.MediaCoreRegistration.AddMediaCore(builder.Services);
// Smart & manual collections (#427): user-curated and rule-driven cross-media shelves over works.
// Per-type media facts (#426): one registration for Collections (#427) and any other consumer.
builder.Services.AddScoped<Jularr.Web.Features.MediaFacts.MediaFactsService>();
Jularr.Web.Features.Collections.CollectionRegistration.AddCollections(builder.Services);
// Learning v3 curriculum foundation (#441): blueprint hierarchy + shared course instances + progress.
Jularr.Web.Features.Learning.Curriculum.CurriculumRegistration.AddLearningCurriculum(builder.Services);
builder.Services.Configure<MediaSegmentOptions>(builder.Configuration.GetSection(MediaSegmentOptions.SectionName));
// Single canonical opt-in: cross-episode audio fingerprint detection is CPU heavy (it decodes and
// hashes several minutes of audio per episode), so it stays off unless explicitly enabled.
var fingerprintDetectionEnabled = builder.Configuration
    .GetSection(MediaSegmentOptions.SectionName)
    .GetValue<bool>(nameof(MediaSegmentOptions.FingerprintDetectionEnabled));
builder.Services.AddSingleton<IAudioWindowDecoder, FfmpegAudioWindowDecoder>();
if (fingerprintDetectionEnabled)
{
    builder.Services.AddSingleton<IMediaSegmentDetector, AudioFingerprintMediaSegmentDetector>();
}
else
{
    builder.Services.AddSingleton<IMediaSegmentDetector, NoOpMediaSegmentDetector>();
}

builder.Services.AddSingleton<TrickplayGenerator>();
builder.Services.AddSingleton<SeasonSegmentDetectionQueue>();
builder.Services.AddScoped<MediaSegmentService>();
builder.Services.AddScoped<MediaSegmentSidecarImporter>();
builder.Services.AddScoped<VideoProgressService>();
builder.Services.AddScoped<VideoDetailQuery>();
builder.Services.AddScoped<CanonicalVideoTargetResolver>();
builder.Services.AddScoped<ActiveSessionService>();
builder.Services.AddScoped<EpisodeProgressService>();
builder.Services.AddScoped<ClientApiService>();
builder.Services.AddScoped<ClientApiOfflineService>();
builder.Services.AddSingleton<OfflinePortableRenditionService>();
builder.Services.AddScoped<ClientApiOfflineMediaPackageService>();
builder.Services.AddScoped<ClientApiOfflinePackageOptionsService>();
builder.Services.AddScoped<OfflineProgressReconciler>();
builder.Services.AddScoped<OfflineLibraryQueries>();
builder.Services.AddScoped<OfflineLibraryProgressReconciler>();
builder.Services.AddScoped<OfflineLibraryBookmarkReconciler>();
builder.Services.AddScoped<ClientApiOfflineLibraryService>();
builder.Services.AddOfflinePrefetch();
builder.Services.AddScoped<Jularr.Web.Features.Speech.TtsPreferencesService>();
builder.Services.AddSingleton(_ => new Jularr.Web.Features.Speech.SpeechModelManifestStore("/data"));
builder.Services.AddSingleton<ReaderThemeCatalog>();

// An API key travels in the query string, so the request logging of the HTTP client factory (which prints the address) stays off for TMDB.
builder.Services.AddHttpClient<TmdbDiscoveryProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).RemoveAllLoggers();
builder.Services.AddHttpClient<AniListMetadataProvider>(client =>
{
    client.BaseAddress = new Uri("https://graphql.anilist.co/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<IAnimeMetadataProvider>(
    services => services.GetRequiredService<AniListMetadataProvider>());
builder.Services.AddScoped<AnimeMetadataService>();
builder.Services.AddScoped<AnimeRepairService>();
builder.Services.AddHttpClient(Jularr.Web.Features.Artwork.AnimeArtworkLibrary.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(30));
// Persisted Work metadata and artwork (#820): the spool worker fetches through the TMDB adapter and keeps artwork only from its CDN.
builder.Services.AddHttpClient(Jularr.Web.Features.Artwork.WorkArtworkCache.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(services =>
    new Jularr.Web.Features.Artwork.WorkArtworkCache(Jularr.Web.Features.Artwork.WorkArtworkCache.DefaultRootPath, [TmdbDiscoveryProvider.ImageHost], services.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<WorkMetadataRefreshSignal>();
builder.Services.AddScoped<WorkMetadataRefreshQueue>();
builder.Services.AddScoped<WorkMetadataRefresher>();
builder.Services.AddHostedService<WorkMetadataRefreshService>();
builder.Services.AddScoped<Jularr.Web.Features.Artwork.AnimeArtworkLibrary>();
builder.Services.AddScoped<Jularr.Web.Features.Artwork.BesideMediaArtworkStore>();
builder.Services.AddScoped<Jularr.Web.Features.Artwork.BesideMediaArtworkCache>();
builder.Services.AddScoped<Jularr.Web.Features.Artwork.ReadingCoverArtwork>();
builder.Services.AddScoped<Jularr.Web.Features.Search.MediaSearchService>();

builder.Services.AddHttpClient<NcodeNovelSourceProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Jularr/0.1 (+https://github.com/Juloc/Jularr)");
});
builder.Services.AddScoped<INovelSourceProvider>(
    services => services.GetRequiredService<NcodeNovelSourceProvider>());
builder.Services.AddScoped<NovelImportService>();
builder.Services.AddSingleton<NovelVolumeAssetStore>();
builder.Services.AddScoped<NovelEpubImportService>();
builder.Services.AddScoped<NovelCatalogQueries>();
builder.Services.AddScoped<NovelProgressService>();
builder.Services.AddScoped<NovelAnnotationService>();
builder.Services.AddScoped<NovelJobs>();

builder.Services.AddHttpClient<NovelAniListProvider>(client =>
{
    client.BaseAddress = new Uri("https://graphql.anilist.co/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<INovelMetadataProvider>(
    services => services.GetRequiredService<NovelAniListProvider>());
// Reading sources (#477): settings store, health tracking and every catalog search provider.
Jularr.Web.Features.ReadingSources.ReadingSourceRegistration.AddReadingSources(builder.Services);
builder.Services.AddScoped<NovelMetadataService>();
builder.Services.AddScoped<NovelTranslationService>();
builder.Services.AddScoped<NovelMappingService>();

builder.Services.AddHttpClient<BookCatalogService>(client =>
{
    client.BaseAddress = new Uri("https://gutendex.com/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Jularr/0.1 (+https://github.com/Juloc/Jularr)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<BookSearchCoordinator>();
builder.Services.AddScoped<BookManualSearchService>();

builder.Services.AddSingleton<SabnzbdAcquisitionStore>();
builder.Services.AddHttpClient<ISabnzbdClient, SabnzbdClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<IProwlarrClient, ProwlarrClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

// Unified external-provider framework (#438): shared HTTP execution (timeouts/retries),
// rate-limit gate + pacing, response cache (stale-while-unavailable), and per-provider
// health/circuit tracking. The indexers below and the AniList limiters run on it; subtitle (#560)
// and audiobook (#440) providers adopt it via ProviderExecutor/ProviderResponseCache when they build.
builder.Services.AddProviderFramework();

// Indexers: Prowlarr and direct Newznab connections share the one canonical list. Jularr is
// usenet-only; torrent indexers (Torznab) are intentionally unsupported.
builder.Services.AddSingleton<IndexerStore>();
builder.Services.AddHttpClient<NewznabIndexer>(client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<Jularr.Web.Features.Music.MusicBrainzProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Jularr/0.1 (+https://github.com/Juloc/Jularr)");
});
builder.Services.AddScoped<Jularr.Web.Features.Music.IMusicMetadataProvider>(services => services.GetRequiredService<Jularr.Web.Features.Music.MusicBrainzProvider>());
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicLibraryService>();
builder.Services.AddSingleton<IReadOnlyDictionary<IndexerType, IIndexer>>(services =>
    new Dictionary<IndexerType, IIndexer>
    {
        [IndexerType.Prowlarr] = new ProwlarrIndexer(services.GetRequiredService<IProwlarrClient>()),
        [IndexerType.Newznab] = services.GetRequiredService<NewznabIndexer>()
    });
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Search.SearchEvidenceCache>();
builder.Services.AddScoped<IndexerSearchCoordinator>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.AcquisitionAccessStore>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.AcquisitionRequestService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.VideoRequestScopeResolver>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.VideoRequestWorkResolver>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Monitoring.VideoMonitoringService>();
builder.Services.AddScoped<Jularr.Web.Features.Library.AdminVideoMediaService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.WantedListService>();
builder.Services.AddScoped<Jularr.Web.Features.Library.AdminMediaDetailService>();
// Request experience (#597): auto-approval rules and requester-selectable quality profiles are
// configuration (JSON store under /data); the per-user history is a query over the request table.
builder.Services.AddSingleton(_ => new Jularr.Web.Features.Acquisition.Access.AcquisitionRequestSettingsStore("/data"));
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.RequestHistoryQuery>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.RequestArtworkResolver>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.RequestStatusQuery>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.Books.BookAcquisitionExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.AnimeAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor>(provider => provider.GetRequiredService<Jularr.Web.Features.Acquisition.Access.AnimeAcquisitionRequestExecutor>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IMonitoredAcquisitionExecutor>(provider => provider.GetRequiredService<Jularr.Web.Features.Acquisition.Access.AnimeAcquisitionRequestExecutor>());
builder.Services.AddScoped<Jularr.Web.Features.ReadingAcquisition.ReadingAcquisitionEngine>();
builder.Services.AddScoped<Jularr.Web.Features.ReadingAcquisition.ReadingManualSearchService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.ReadingAcquisition.MangaAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.ReadingAcquisition.LightNovelAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.VideoAcquisitionEngine>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.ManualSearch.VideoManualSearchService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.Acquisition.Access.MovieAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.Acquisition.Access.TvAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.Acquisition.Access.MovieWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.Acquisition.Access.TvWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.ReadingAcquisition.MangaWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.ReadingAcquisition.LightNovelWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.Books.BookWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedRequestHandler, Jularr.Web.Features.Music.MusicWantedRequestHandler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedSource, Jularr.Web.Features.Music.MusicWantedSource>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Wanted.UpgradeScanState>();
foreach (var upgradeKind in new[] { Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Movie, Jularr.Web.Features.Acquisition.Access.MediaAcquisitionKind.Tv })
{
    builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedSource>(services => ActivatorUtilities.CreateInstance<Jularr.Web.Features.Acquisition.Wanted.VideoUpgradeWantedSource>(services, upgradeKind));
}
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicMonitoringService>();
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicQuery>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.ManualSearch.ManualGrabCoordinator>();
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicManualSearchService>();
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Music.MusicCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Music.MusicCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Music.MusicAcquisitionEngine>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Access.IAcquisitionRequestExecutor, Jularr.Web.Features.Music.MusicAcquisitionRequestExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.ReleaseRequestTracker>();
builder.Services.AddScoped<Jularr.Web.Features.ReadingAcquisition.MangaCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.MangaCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.MangaCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.ReadingAcquisition.LightNovelCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.LightNovelCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.ReadingAcquisition.LightNovelCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Books.BookCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Books.BookCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Books.BookCompletedDownloadImportAdapter>());
// First-class video media types (#593 Movie, #594 TV): library services + shared completed-download/inbox adapters.
builder.Services.AddScoped<Jularr.Web.Features.Movies.MovieLibraryService>();
builder.Services.AddScoped<Jularr.Web.Features.Movies.MovieCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Movies.MovieCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Movies.MovieCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Tv.TvLibraryService>();
builder.Services.AddScoped<Jularr.Web.Features.Tv.TvCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Tv.TvCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Tv.TvCompletedDownloadImportAdapter>());
// First-class audiobook media type (#440): library service, per-profile progress and the shared
// completed-download/inbox adapters (bridged to the media core as a Book work with an audiobook edition).
builder.Services.AddScoped<Jularr.Web.Features.Audiobooks.AudiobookLibraryService>();
builder.Services.AddScoped<Jularr.Web.Features.Audiobooks.AudiobookProgressService>();
builder.Services.AddScoped<Jularr.Web.Features.Audiobooks.AudiobookCompletedDownloadImportAdapter>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Audiobooks.AudiobookCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.IMediaInboxImportAdapter>(services => services.GetRequiredService<Jularr.Web.Features.Audiobooks.AudiobookCompletedDownloadImportAdapter>());
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.CompletedDownloadDispatcher>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.CompletedDownloadImportService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.MediaInboxImportService>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadLocationResolver, Jularr.Web.Features.Acquisition.Import.CompletedDownloadLocationResolver>();

// Download clients: SABnzbd connections share the one canonical list (several can fail over to
// each other). Jularr is usenet-only; torrent clients (qBittorrent) are intentionally
// unsupported. The pipeline and Books submission only depend on IDownloadClient, never on
// SabnzbdDownloadClient directly.
builder.Services.AddSingleton<DownloadClientStore>();
builder.Services.AddSingleton<IDownloadClient>(services =>
    new SabnzbdDownloadClient(services.GetRequiredService<ISabnzbdClient>()));
builder.Services.AddScoped<DownloadClientSelector>();
builder.Services.AddScoped<DownloadClientSubmissionService>();

builder.Services.AddSingleton<AcquisitionHealthStore>();
builder.Services.AddHostedService<AcquisitionHealthCheckService>();

builder.Services.AddScoped<SabnzbdDownloadService>();
builder.Services.AddScoped<IOperationActions, OperationActions>();
builder.Services.AddScoped<SabnzbdAcquisitionService>();
builder.Services.AddHostedService<SabnzbdOperationMonitorService>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Wanted.WantedPassTrigger>();
builder.Services.AddHostedService<Jularr.Web.Features.Acquisition.Wanted.WantedAcquisitionService>();

builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.AnimeAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.MovieAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.TvAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.AudiobookAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.BookAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.Acquisition.Release.MusicAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.ReadingAcquisition.MangaAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.IMediaAcquisitionRegistration, Jularr.Web.Features.ReadingAcquisition.LightNovelAcquisitionRegistration>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Release.MediaAcquisitionRegistry>();
builder.Services.AddSingleton<AnimeQualityProfileStore>();
builder.Services.AddSingleton(_ => new AnimeMonitoringStore("/data"));
builder.Services.AddSingleton<AnimeImportStore>();
builder.Services.AddSingleton(_ => new AnimeImportSettingsStore("/data"));
builder.Services.AddSingleton<IHardLinkCreator, FileSystemHardLinkCreator>();
builder.Services.AddSingleton(_ => new AcquisitionPolicyStore("/data"));
builder.Services.AddSingleton(_ => new AniListAutoMonitorSettingsStore("/data"));
builder.Services.AddScoped<AniListAutoMonitorService>();
builder.Services.AddScoped<AcquisitionHistoryService>();
builder.Services.AddScoped<AcquisitionBackupService>();
builder.Services.AddScoped<AnimeAcquisitionInventory>();
builder.Services.AddScoped<AnimeAcquisitionPipeline>();
builder.Services.AddScoped<AnimeImportExecutor>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Import.ICompletedDownloadImportAdapter>(services => services.GetRequiredService<AnimeImportExecutor>());
builder.Services.AddScoped<AnimeImportRecovery>();
builder.Services.AddSingleton<AnimeAcquisitionScheduler>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Wanted.IWantedSource, AnimeWantedSource>();
Jularr.Web.Features.Calendar.ReleaseCalendarRegistration.AddReleaseCalendar(builder.Services);

builder.Services.AddScoped<AcquisitionApiKeyService>();
builder.Services.AddScoped<AcquisitionApiService>();

builder.Services.AddSingleton<SonarrConnectionStore>();
builder.Services.AddScoped<SonarrArtworkImportService>();
builder.Services.AddScoped<SonarrArtworkSyncService>();
builder.Services.AddSingleton(_ =>
    new Jularr.Web.Features.Acquisition.Ownership.AcquisitionOwnershipStore("/data"));
builder.Services.AddSingleton<ISonarrObserverClient, SonarrObserverClient>();
builder.Services.AddSingleton<ISonarrSeriesMonitoringClient, SonarrSeriesMonitoringClient>();
builder.Services.AddSingleton<SonarrObservationService>();
builder.Services.AddSingleton<SonarrMigrationService>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Naming.AnimeNamingProfileStore>();
builder.Services.AddSingleton<Jularr.Web.Features.Acquisition.Naming.AnimeRenameFileSystem>();
builder.Services.AddScoped<Jularr.Web.Features.Acquisition.Naming.AnimeRenameService>();
builder.Services.AddSingleton<Jularr.Web.Features.Naming.ReadingNamingProfileStore>();

builder.Services.AddSingleton<MediaMappingReviewStore>();
builder.Services.AddSingleton<ReadingSegmentMappingStore>();
builder.Services.AddSingleton<AniListAccountStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AniListRateLimitGate>();
builder.Services.AddTransient<AniListRateLimitHandler>();
builder.Services.AddHttpClient<AniListAccountService>(client =>
{
    client.BaseAddress = new Uri("https://graphql.anilist.co/");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).AddHttpMessageHandler<AniListRateLimitHandler>();
builder.Services.AddSingleton<AniListSyncStateStore>();
builder.Services.AddScoped<AniListSyncService>();
builder.Services.AddHostedService<AniListSyncBackgroundService>();

builder.Services.AddSingleton<ICodexAppServerLauncher, CodexAppServerProcessLauncher>();
builder.Services.AddSingleton<CodexAppServerClient>();
builder.Services.AddSingleton<CodexAppServerGateway>();
builder.Services.AddSingleton<CodexCliProvider>();
builder.Services.AddSingleton<AiProfileSettingsStore>();
builder.Services.AddSingleton<AiUsagePersistenceQueue>();
builder.Services.AddSingleton<IAiUsageSink>(services => services.GetRequiredService<AiUsagePersistenceQueue>());
builder.Services.AddHostedService<AiUsagePersistenceWorker>();
builder.Services.AddSingleton<AiUsageTracker>();
builder.Services.AddSingleton<AiActivityTracker>();
builder.Services.AddSingleton<AiActivityRunner>();
builder.Services.AddScoped<AiUsageStore>();
builder.Services.AddScoped<IAiModelCatalogStore, AiModelCatalogStore>();
builder.Services.AddScoped<AiModelCatalogService>();
builder.Services.AddHttpClient("ai-openai-compatible", client =>
{
    client.Timeout = TimeSpan.FromMinutes(4);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<ProfileAiProviderRouter>();
builder.Services.AddScoped<IAiProvider>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddScoped<IAiSentenceExplainer>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddScoped<INovelTranslator>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddScoped<IBookTranslator>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddScoped<INovelMappingSuggester>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddScoped<IStoryContextExtractor>(services => services.GetRequiredService<ProfileAiProviderRouter>());
builder.Services.AddSingleton(services => StoryContextStore.FromConfiguration(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<StoryContextSnapshotCache>();
builder.Services.AddScoped<StoryContextService>();
builder.Services.AddScoped<IAiImageGenerator, ProfileAiImageRouter>();
builder.Services.AddSingleton(services => ChapterArtworkGlobalSettingsStore.FromConfiguration(services.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped<ChapterArtworkStore>();
builder.Services.AddScoped<ChapterArtworkService>();
builder.Services.AddHostedService<ChapterArtworkAutoGenerator>();
builder.Services.AddScoped<AiSentenceExplanationService>();

builder.Services.AddSingleton<BackgroundJobQueue>();
builder.Services.AddScoped<BookTranslationJobs>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddSingleton<PlaybackJobQueue>();
builder.Services.AddHostedService<PlaybackJobWorker>();
builder.Services.AddSingleton<PlaybackSessionStore>();
builder.Services.AddSingleton<PlaybackSessionConnectionRegistry>();
builder.Services.AddSingleton<PlaybackSessionCoordinator>();
builder.Services.AddSingleton<DevicePairingStore>();
builder.Services.AddHostedService<DiscoveryBeaconService>();

// Container check (#649): the DI graph test boots this entry point with this flag so every
// registration is validated (constructor dependencies and scope lifetimes), then exits before
// touching the database or serving anything.
var containerCheckOnly = builder.Configuration.GetValue<bool>("Jularr:ContainerCheckOnly");
if (containerCheckOnly)
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateOnBuild = true;
        options.ValidateScopes = true;
    });
}

var app = builder.Build();

if (containerCheckOnly)
{
    return;
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseForwardedHeaders();
app.UseBrandedManifest();
app.UseStaticFiles();
app.UseRouting();
app.UseApplicationPerformance();
// Authentication runs first so the per-account rate-limit policies ("wake", "playbackStart", "playbackIntent", ...) see the signed-in
// account; behind it they would all fall back to the shared client IP. The anonymous policies partition by IP either way.
app.UseAuthentication();
app.UseRateLimiter();
app.UseInstanceModuleGates();
// Operations created while a signed-in account's request runs record that account as their actor
// (Admin → History); work started by the server itself has none.
app.Use(async (context, next) =>
{
    using var actor = OperationActor.Enter(context.User.FindFirstValue(ClaimTypes.NameIdentifier));
    await next();
});
app.UseAuthorization();
app.MapClientApiV1();
app.MapClientApiPlaybackPlanV1();
app.MapClientApiPlaybackIntentsV1();
app.MapAcquisitionApiV1();
app.MapClientApiOfflineV1();
app.MapClientApiOfflineMediaPackageV1();
app.MapClientApiOfflinePackagesV1();
app.MapClientApiOfflineLibraryV1();
app.MapClientApiOfflinePrefetchV1();
app.MapHub<PlaybackSessionHub>(PlaybackSessionHub.Route)
    .AllowAnonymous();
app.MapDiscoveryWellKnown();
app.MapReaderThemeCatalog();
app.MapLanguageInspector();
app.MapAiActivity();
app.MapFolderBrowse();
app.MapBranding();
app.MapRazorPages();

try
{
    Console.WriteLine($"[Jularr] {DateTimeOffset.UtcNow:O} Initializing persistent database.");
    await InitializeDatabaseAsync(
        app.Services,
        message => Console.WriteLine($"[Jularr] {DateTimeOffset.UtcNow:O} {message}"));
    Console.WriteLine($"[Jularr] {DateTimeOffset.UtcNow:O} Database ready. Starting web server.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Jularr] {DateTimeOffset.UtcNow:O} Startup database initialization failed.");
    Console.Error.WriteLine(ex);
    throw;
}

await app.RunAsync();

static async Task InitializeDatabaseAsync(
    IServiceProvider services,
    Action<string> log)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await DatabaseMigrationBridge.UpgradeAsync(db, log: log);


    if (await db.LibraryRoots.AnyAsync())
    {
        return;
    }

    var media = scope.ServiceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
    if (string.IsNullOrWhiteSpace(media.BootstrapRoot))
    {
        return;
    }

    log($"Creating bootstrap library root {media.BootstrapRoot}.");

    var bootstrapRoot = new LibraryRoot
    {
        Name = "Anime",
        Path = Path.GetFullPath(media.BootstrapRoot)
    };
    db.LibraryRoots.Add(bootstrapRoot);
    db.LibraryRootContentAssignments.Add(new Jularr.Web.Features.Library.LibraryRootContentAssignment { LibraryRootId = bootstrapRoot.Id, ContentType = Jularr.Web.Features.Library.LibraryContentType.Anime });
    await db.SaveChangesAsync();
}
