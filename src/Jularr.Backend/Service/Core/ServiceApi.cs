using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Logic.AniBridge;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Browser;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.Libretro;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Gutenberg;
using Jularr.Integrations.Logic.AcoustId;
using Jularr.Integrations.Logic.Hardcover;
using Jularr.Integrations.Logic.Opds;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.MediaServer;
using Jularr.Integrations.Logic.Plex;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Narou;
using Jularr.Integrations.Logic.Nyaa;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Logic.Servarr;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Logic.Tvdb;
using Jularr.Integrations.Logic.Usenet;
using Jularr.Backend.Service.Core.Localization;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.User.Reading.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Backend.Service.Public.Localization.V1;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Jularr.Integrations.Data.Database;
using Jularr.Integrations.Logic.Database;
using Jularr.Integrations.Logic.Subtitles;
using Jularr.Integrations.Logic.Synology;
using Jularr.Integrations.Logic.Ocr;
using Jularr.Integrations.Logic.Translation;
using Microsoft.Extensions.Logging;
using Npgsql;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Public.Authentication.V1;

namespace Jularr.Backend.Service.Core
{
    public static class ServiceApi
    {
        public const string AuthenticationNonceCookieName = "jularr.auth_nonce";
        public const string DevicePairingCookieName = "jularr.device_pairing";
        public const string DevicePairingRateLimit = "device-pairing";
        // A cast device fetches a segment every few seconds, and several at once after a seek.
        public const string CastRateLimit = "cast";
        public const string DeviceApprovalRateLimit = "device-approval";
        public const string SessionScheme = "jularr.session";
        public const string DeviceBearerScheme = "jularr.device";
        public const string DeviceTokenHeader = "Jularr-Device-Token";
        public const string AdminPolicyName = "jularr.admin";
        public const string StorageReadPolicyName = "storage.read";
        public const string RulesReadPolicyName = "rules.read";
        public const string MediaReadPolicyName = "media.read";
        public const string RequestsReadPolicyName = "requests.read";
        public const string WantedReadPolicyName = "wanted.read";
        public const string AcquisitionManagePolicyName = "acquisition.manage";
        public const string LocalizationReadPolicyName = "localization.read";
        public const string AccountsReadPolicyName = "accounts.read";
        public const string ProvidersReadPolicyName = "providers.read";
        public const string InstanceSettingsReadPolicyName = "instance.settings.read";
        public const string BackupReadPolicyName = "backup.read";

        public static IServiceCollection AddJularrServices(this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            // Own the database lifetime.
            var connectionString = configuration["JULARR_DB"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("JULARR_DB must be configured explicitly.");
            }

            services.AddSingleton(_ => new SqlDatabase(new NpgsqlDataSourceBuilder(connectionString).Build()));
            services.AddSingleton(provider => new DatabaseBaseline(connectionString, provider.GetRequiredService<IOptions<BackupOptions>>().Value, provider.GetRequiredService<PostgresBackupTools>()));
            services.AddHostedService(provider => provider.GetRequiredService<DatabaseBaseline>());

            // Persistent state lives under one folder, /data in the image; every feature works without further settings.
            var data = configuration["DataPath"] is { Length: > 0 } dataPath ? dataPath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jularr");
            string Folder(string? configured, string name) => configured is { Length: > 0 } ? configured : Path.Combine(data, name);
            var keyDirectory = Folder(configuration["DataProtection:KeyDirectory"], "keys");
            if (!Path.IsPathFullyQualified(data) || !Path.IsPathFullyQualified(keyDirectory))
            {
                throw new InvalidOperationException("DataPath and DataProtection:KeyDirectory must be absolute persistent paths.");
            }

            var protection = services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
            if (OperatingSystem.IsWindows())
            {
                protection.ProtectKeysWithDpapi();
            }

            services.Configure<AuthenticationOptions>(configuration.GetSection("Authentication"));
            services.Configure<LocalTranslationOptions>(configuration.GetSection("LocalTranslation"));
            services.Configure<StorageOptions>(configuration.GetSection("Storage"));
            services.Configure<MediaOptions>(options =>
            {
                configuration.GetSection("Media").Bind(options);
                options.TrickplayPath = Folder(options.TrickplayPath, "trickplay");
                options.TranslationPath = Folder(options.TranslationPath, "translation");
                options.OcrPath = Folder(options.OcrPath, "ocr");
                options.SpeechPath = Folder(options.SpeechPath, "speech");
                options.CoversPath = Folder(options.CoversPath, "covers");
            });
            services.Configure<BrowserOptions>(options =>
            {
                configuration.GetSection("Browser").Bind(options);
                options.ProfilePath = Folder(options.ProfilePath, "browser");
            });
            services.Configure<VpnOptions>(options =>
            {
                configuration.GetSection("Vpn").Bind(options);
                options.WorkPath = Folder(options.WorkPath, "vpn");
            });

            // Metadata mirrors.
            var providers = (configuration.GetSection("Providers").Get<ProviderOptions>() ?? new ProviderOptions()).Validated();
            services.AddSingleton(new MediaProbeClient { ExecutablePath = Executables.Resolve(configuration["Media:FfprobePath"], "ffprobe") });
            services.AddSingleton(new MediaTranscoder { ExecutablePath = Executables.Resolve(configuration["Media:FfmpegPath"], "ffmpeg") });
            services.AddSingleton(new JxlEncoder { ExecutablePath = Executables.Resolve(configuration["Media:CjxlPath"], "cjxl") });
            services.AddHostedService<JxlStartupCheck>();
            services.AddSingleton(new AudioFingerprinter { ExecutablePath = Executables.Resolve(configuration["Media:FpcalcPath"], "fpcalc") });
            services.AddSingleton(new ArchiveTools
            {
                Par2Path = Executables.Resolve(configuration["Usenet:Par2Path"], "par2"),
                SevenZipPath = Executables.Resolve(configuration["Usenet:SevenZipPath"], "7zz", "7z")
            });

            // Backups reach the same database.
            var target = new NpgsqlConnectionStringBuilder(connectionString);
            services.Configure<BackupOptions>(options =>
            {
                configuration.GetSection("Backup").Bind(options);
                options.Path = Folder(options.Path, "backups");
                options.KeyDirectory = keyDirectory;
            });
            services.AddSingleton(new PostgresBackupTools
            {
                PgDumpPath = Executables.Resolve(configuration["Backup:PgDumpPath"], "pg_dump"),
                PgRestorePath = Executables.Resolve(configuration["Backup:PgRestorePath"], "pg_restore"),
                Connection = new PostgresConnection(target.Host ?? "localhost", target.Port, target.Database ?? "", target.Username ?? "", target.Password)
            });
            return RegisterServices(services, [typeof(Service).Assembly], providers);
        }

        internal static IServiceCollection AddJularrServices(this IServiceCollection services, NpgsqlDataSource database, params Assembly[] assemblies)
        {
            // Tests own isolated data sources.
            services.AddSingleton(new SqlDatabase(database));
            return RegisterServices(services, assemblies, new ProviderOptions());
        }

        private static IServiceCollection RegisterServices(IServiceCollection services, Assembly[] assemblies, ProviderOptions providers)
        {
            // Recent warnings and errors for the Owner's system page.
            services.TryAddSingleton<AcquisitionSearchSignal>();
            services.TryAddSingleton<LiveChanges>();
            services.TryAddSingleton<WorkSignals>();
            services.TryAddSingleton<SearchRunCancellation>();
            services.TryAddSingleton<UserActivity>();
            services.TryAddSingleton<WorkerJobs>();
            services.TryAddSingleton<WorkerJobRunner>();
            services.TryAddSingleton<RecentLogs>();
            services.TryAddSingleton<ServiceTimings>();
            services.TryAddSingleton<ProviderObservations>();
            services.TryAddSingleton(new NewznabGate(NewznabGate.DefaultSpacing));
            services.TryAddSingleton<HealthChecks>();
            services.TryAddSingleton<ResourceHistory>();
            services.AddHostedService(provider => provider.GetRequiredService<ResourceHistory>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RecentLogs>(provider => provider.GetRequiredService<RecentLogs>()));

            // Discover concrete services.
            var serviceTypes = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsVisible && !type.IsAbstract && !type.ContainsGenericParameters && typeof(Service).IsAssignableFrom(type))
                .Distinct().ToArray();

            // Register shared infrastructure.
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<MediaProbeClient>();
            services.TryAddSingleton<WakeOnLanClient>();
            services.TryAddSingleton<SmbShares>();
            services.TryAddSingleton<StorageStreamServer>();
            services.TryAddSingleton<StorageAccess>();
            services.TryAddSingleton<DownloadStorage>();
            services.TryAddSingleton<MediaTranscoder>();
            services.TryAddSingleton<JxlEncoder>();
            services.TryAddSingleton<TranscodeBenchmarkClient>();
            services.TryAddSingleton<TranscodeHardware>();
            services.TryAddSingleton<AudioFingerprinter>();
            services.TryAddSingleton<PlaybackTranscodes>();
            services.TryAddSingleton<PlaybackCastTokens>();
            services.TryAddTransient<PlaybackSubtitles>();
            services.TryAddTransient<PlaybackFonts>();
            services.TryAddSingleton<SubtitleTranslations>();
            services.TryAddTransient<WantedAcquisition>();
            services.TryAddTransient<MetadataCatalog>();
            services.TryAddSingleton<ExternalFeedCache>();
            services.TryAddTransient<MediaImportPlanner>();
            services.TryAddTransient<Jularr.Backend.Service.Admin.Media.V1.MediaImportProcessor>();
            services.TryAddTransient<WorkMetadata>();
            services.TryAddTransient<TvdbMetadata>();
            services.TryAddSingleton<TvdbConnectionState>();
            services.TryAddSingleton<PlexLibraries>();
            services.TryAddTransient<PlexServer>();
            services.TryAddTransient<MediaServers>();
            services.TryAddTransient<LibraryReplacements>();
            services.TryAddTransient<BookMetadata>();
            services.TryAddTransient<GameRomIdentification>();
            services.TryAddTransient<MusicTagIdentification>();
            services.TryAddTransient<AniListMetadata>();
            services.TryAddTransient<NarouMetadata>();
            services.TryAddTransient<AniListMatcher>();
            services.TryAddTransient<AniListAccounts>();
            services.TryAddTransient<MusicMetadata>();
            services.TryAddTransient<GameMetadata>();
            services.TryAddTransient<DirectSources>();
            services.TryAddTransient<NyaaSources>();
            services.TryAddTransient<SubtitleProviders>();
            services.TryAddTransient<Jularr.Backend.Service.Admin.Acquisition.V1.AcquisitionConnectionProbe>();
            services.TryAddTransient<HealthProbes>();
            services.TryAddTransient<SubtitleFiles>();
            services.TryAddTransient<DownloadImport>();
            services.TryAddSingleton<UsenetDownloader>();
            services.TryAddSingleton<DownloadSpeeds>();
            services.TryAddSingleton<DirectDownloadCleanup>();
            services.TryAddSingleton<ArchiveTools>();
            services.TryAddSingleton<PostgresBackupTools>();
            services.TryAddTransient<UsenetJob>();
            services.TryAddTransient<TorrentJob>();
            services.TryAddSingleton<ISourceGateResolver, NoSourceGateResolver>();
            services.TryAddSingleton(provider => new BrowserPackages(provider.GetRequiredService<IOptions<BrowserOptions>>().Value));
            services.AddSingleton<SourceBrowser>(provider => new SourceBrowser(provider.GetRequiredService<IOptions<BrowserOptions>>().Value,
                provider.GetRequiredService<BrowserPackages>()));
            services.AddSingleton<ISourceBrowser>(provider => provider.GetRequiredService<SourceBrowser>());
            services.AddSingleton<IBrowserInteraction>(provider => provider.GetRequiredService<SourceBrowser>());
            services.TryAddSingleton<BrowserAssistance>();
            services.TryAddSingleton<VpnTunnels>();
            services.TryAddTransient<ReadingContent>();
            services.TryAddTransient<ReadingDerivation>();
            services.TryAddSingleton<ReadingActivity>();
            services.TryAddSingleton<TranslationFiles>();
            services.TryAddSingleton<LocalTranslationGate>();
            services.TryAddSingleton<BookTranslationProgress>();
            services.TryAddSingleton<LocalTranslationMachines>();
            services.TryAddSingleton<LocalTranslationDownloads>();
            services.AddHostedService(provider => provider.GetRequiredService<LocalTranslationDownloads>());
            services.AddHttpClient<OllamaLocalModels>(http => http.Timeout = TimeSpan.FromMinutes(65))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            services.TryAddSingleton<SpeechFiles>();
            services.AddHttpClient<Jularr.Integrations.Logic.Speech.SpeechCatalogClient>(http => http.Timeout = TimeSpan.FromSeconds(25));
            services.AddHttpClient<Jularr.Integrations.Logic.Speech.SpeechModelClient>(http => http.Timeout = TimeSpan.FromMinutes(5));
            services.TryAddSingleton<IPageRecognizer, OcrFiles>();
            services.AddMemoryCache(options => options.SizeLimit = 20_000);
            services.TryAddSingleton<UiLocalizationCache>();
            services.AddSingleton(new ServiceCatalog(serviceTypes));
            services.AddHttpContextAccessor();
            services.AddAntiforgery();
            services.AddProblemDetails();
            services.AddExceptionHandler<ServiceExceptionHandler>();
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
            services.AddScoped<IServiceGate, ServiceGate>();
            services.AddScoped(provider => new SessionAuthentication(provider.GetRequiredService<SqlDatabase>().DataSource));
            services.AddScoped<SessionCookieEvents>();
            services.AddSingleton<TrustedProxies>();
            services.AddHostedService(provider => provider.GetRequiredService<TrustedProxies>());
            services.AddSingleton<DeviceTokenFailures>();

            // Token requests never read cookies.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, DeviceBearerHandler>(DeviceBearerScheme, null)
                .AddPolicyScheme(SessionScheme, null, options => options.ForwardDefaultSelector = http =>
                    http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? DeviceBearerScheme : CookieAuthenticationDefaults.AuthenticationScheme);
            services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
            {
                if (options.SchemeMap.ContainsKey(CookieAuthenticationDefaults.AuthenticationScheme))
                {
                    options.DefaultScheme = SessionScheme;
                }
            });
            services.AddDataProtection().SetApplicationName("Jularr");

            // Razor consumes verified service grants.
            services.AddRouting();
            services.AddAuthorization(options =>
            {
                foreach (var (policyName, permission) in new[] { (AdminPolicyName, "admin.access"), (StorageReadPolicyName, "storage.read"), (RulesReadPolicyName, "rules.read"),
                    (MediaReadPolicyName, "media.read"), (RequestsReadPolicyName, "requests.read"), (WantedReadPolicyName, "wanted.read"), (AcquisitionManagePolicyName, "acquisition.manage"),
                    (LocalizationReadPolicyName, "localization.read"), (AccountsReadPolicyName, "accounts.read"), (ProvidersReadPolicyName, "providers.read"),
                    (InstanceSettingsReadPolicyName, "instance.settings.read"), (BackupReadPolicyName, "backup.read") })
                {
                    options.AddPolicy(policyName, policy => policy.RequireAuthenticatedUser().RequireAssertion(async authorization =>
                    {
                        if (authorization.Resource is not HttpContext http)
                        {
                            return false;
                        }

                        var authentication = await http.RequestServices.GetRequiredService<ReadAuthenticationV1>()
                            .ExecuteAsync<AuthenticationResultV1>(cancellationToken: http.RequestAborted);
                        return authentication.Permissions.Contains(permission, StringComparer.Ordinal);
                    }));
                }
            });

            // Validate trusted auth configuration.
            services.AddOptions<AuthenticationOptions>()
                .Validate(options => options.SessionLifetimeHours is >= 1 and <= 720, "Session lifetime must be 1-720 hours.")
                .ValidateOnStart();

            // Harden session cookies.
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.EventsType = typeof(SessionCookieEvents);
                options.SlidingExpiration = false;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Strict;
            });

            // Bound external communication; pasted source links reach public addresses only.
            AddClient<PlexClient>(services, 10, 1);
            AddClient<GutenbergClient>(services, 60, null, agent: true);
            AddClient<MozillaTranslationsClient>(services, 600, null, agent: true);
            AddClient<OcrModelClient>(services, 600, null, agent: true, baseAddress: providers.OcrModels,
                handler: () => new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false });
            AddClient<OpdsClient>(services, 60, null, agent: true);
            services.TryAddSingleton<AnnasArchiveMirrors>();
            AddClient<AnnasArchiveClient>(services, 60, null, agent: true, handler: PublicNetwork.CreateHandler);
            AddClient<OpenLibraryClient>(services, 15, 4, agent: true, baseAddress: providers.OpenLibrary, provider: "openlibrary");
            AddClient<HardcoverClient>(services, 15, 4, agent: true, baseAddress: providers.Hardcover, provider: "hardcover");
            AddClient<AcoustIdClient>(services, 15, 1, agent: true, baseAddress: providers.AcoustId, provider: "acoustid");
            services.TryAddSingleton(providers);
            AddClient<AniListClient>(services, 15, 4, agent: true, baseAddress: providers.AniList, provider: "anilist");
            AddClient<AniBridgeClient>(services, 60, null, agent: true, baseAddress: providers.AnimeMappings,
                handler: () => new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3, UseCookies = false });
            AddClient<MusicBrainzClient>(services, 20, 4, agent: true, baseAddress: providers.MusicBrainz, provider: "musicbrainz");
            AddClient<IgdbClient>(services, 20, 8, agent: true, provider: "igdb");
            AddClient<LibretroClient>(services, 60, 16, agent: true);
            AddClient<SynologyClient>(services, 30, 4, handler: SynologyClient.CreateHandler);
            AddClient<ServarrApiClient>(services, 60, 32);
            AddClient<PlexServerClient>(services, 20, 16);
            AddClient<MediaServerClient>(services, 30, 32);
            AddClient<NewznabClient>(services, 30, 16);
            AddClient<TmdbClient>(services, 15, 4, baseAddress: providers.Tmdb, provider: "tmdb");
            AddClient<TvdbClient>(services, 15, 4, baseAddress: providers.Tvdb, provider: "tvdb");
            AddClient<SabnzbdClient>(services, 15, 16);
            AddClient<QbittorrentClient>(services, 15, 16);
            AddClient<OpenSubtitlesClient>(services, 30, 12, agent: true);
            AddClient<SubDlClient>(services, 30, 12, agent: true);
    AddClient<NyaaClient>(services, 30, 16, agent: true, handler: PublicNetwork.CreateHandler);
            AddClient<NarouClient>(services, 60, null, agent: true, handler: PublicNetwork.CreateHandler);

            // Covers come from the providers' image hosts only, over public addresses.
            var covers = new CoverMirrors(new Dictionary<string, Uri>(providers.Images, StringComparer.OrdinalIgnoreCase));
            services.AddSingleton(covers);
            AddClient<CoverClient>(services, 30, null, agent: true, handler: () => CoverClient.CreateHandler(covers));
            services.TryAddTransient<CoverStore>();

            // Throttle public requests.
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("public", http => RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                // A TV page asks every few seconds until a phone confirms it; several may share one address.
                options.AddPolicy(CastRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                options.AddPolicy(DevicePairingRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                // Codes checked from one address, whichever accounts it uses.
                options.AddPolicy(DeviceApprovalRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                options.AddPolicy("companion-claim", http => RateLimitPartition.GetFixedWindowLimiter(
                    DeviceTokenFailures.Key(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                options.AddPolicy("companion-command", http => RateLimitPartition.GetFixedWindowLimiter(
                    DeviceTokenFailures.Key(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                options.AddPolicy("companion-read", http => RateLimitPartition.GetFixedWindowLimiter(
                    DeviceTokenFailures.Key(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
                options.OnRejected = (context, token) => new ValueTask(
                    Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "rate_limit_exceeded",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "rate_limit_exceeded",
                            ["resourceKey"] = ServiceErrorResources.GetKey("rate_limit_exceeded"),
                            ["traceId"] = context.HttpContext.TraceIdentifier
                        })
                        .ExecuteAsync(context.HttpContext));
            });

            // Bind each service runtime.
            services.AddScoped(provider => new ServiceRuntime(provider.GetRequiredService<SqlDatabase>().DataSource, provider.GetRequiredService<IServiceGate>(),
                provider.GetRequiredService<ServiceTimings>()));
            foreach (var type in serviceTypes)
            {
                // Compile internal constructor activation.
                var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
                var providerParameter = Expression.Parameter(typeof(IServiceProvider), "provider");
                var dependencies = constructor.GetParameters().Select(parameter => Expression.Convert(
                    Expression.Call(typeof(ServiceProviderServiceExtensions), nameof(ServiceProviderServiceExtensions.GetRequiredService),
                        Type.EmptyTypes, providerParameter, Expression.Constant(parameter.ParameterType)), parameter.ParameterType));
                var create = Expression.Lambda<Func<IServiceProvider, Service>>(Expression.New(constructor, dependencies), providerParameter).Compile();

                // Bind the authorized runtime.
                services.AddScoped(type, provider =>
                {
                    var service = create(provider);
                    service.BindRuntime(provider.GetRequiredService<ServiceRuntime>());
                    return service;
                });
            }

            return services;
        }

        public static IEndpointRouteBuilder MapJularrServices(this IEndpointRouteBuilder endpoints)
        {
            var catalog = endpoints.ServiceProvider.GetRequiredService<ServiceCatalog>();
            using var scope = endpoints.ServiceProvider.CreateScope();
            var routes = new HashSet<(string Method, string Route)>();
            foreach (var type in catalog.ServiceTypes)
            {
                var definition = (IServiceDefinition)scope.ServiceProvider.GetRequiredService(type);
                definition.ValidateDefinition();
                var endpoint = type.GetCustomAttribute<ServiceEndpointAttribute>();
                if (endpoint is null)
                {
                    continue;
                }

                var method = definition.OperationType switch
                {
                    ServiceOperationType.Read => "GET",
                    ServiceOperationType.Create or ServiceOperationType.Execute => "POST",
                    ServiceOperationType.Update => "PUT",
                    ServiceOperationType.Delete => "DELETE",
                    _ => throw new InvalidOperationException("Unknown service operation.")
                };
                if (definition.ServiceArea == ServiceArea.System || endpoint.Method != method
                    || !endpoint.Route.StartsWith("/api/", StringComparison.Ordinal)
                    || !routes.Add((method, endpoint.Route.ToUpperInvariant())))
                {
                    throw new InvalidOperationException("Invalid or duplicate service endpoint declaration.");
                }

                var contract = type.BaseType;
                while (contract is not null && (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(Service<,>)))
                {
                    contract = contract.BaseType;
                }

                var arguments = contract?.GetGenericArguments() ?? throw new InvalidOperationException("An API service must use the shared service base.");
                var noData = arguments[1] == typeof(NoData);
                if (!typeof(BaseData).IsAssignableFrom(arguments[0])
                    || !noData && !typeof(BaseData).IsAssignableFrom(arguments[1])
                    || !typeof(BaseData).IsAssignableFrom(definition.DefaultResultType))
                {
                    throw new InvalidOperationException("API parameters, data and results must use BaseData contracts.");
                }

                if (definition.OperationType == ServiceOperationType.Read && !noData)
                {
                    throw new InvalidOperationException("A read endpoint cannot require a request body.");
                }

                // Bind the shared HTTP adapter.
                var upload = !noData && FormDataBinder.HasFile(arguments[1]);
                var factory = typeof(ServiceApi).GetMethod(noData ? nameof(CreateParameterHandler) : upload ? nameof(CreateUploadHandler) : nameof(CreateBodyHandler),
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                var handler = (Delegate)factory.MakeGenericMethod([type, .. arguments]).Invoke(null, null)!;
                // Files also answer HEAD, so clients can check them against their own cache.
                string[] methods = method == "GET" && typeof(IFileOutput).IsAssignableFrom(definition.DefaultResultType) ? ["GET", "HEAD"] : [method];
                var route = endpoints.MapMethods(endpoint.Route, methods, handler)
                    .WithName(type.FullName!)
                    .WithMetadata(endpoint)
                    .Produces(StatusCodes.Status200OK, definition.DefaultResultType)
                    .ProducesProblem(StatusCodes.Status400BadRequest)
                    .ProducesProblem(StatusCodes.Status401Unauthorized)
                    .ProducesProblem(StatusCodes.Status403Forbidden)
                    .ProducesProblem(StatusCodes.Status404NotFound);

                if (definition.ServiceArea == ServiceArea.Public)
                {
                    route.RequireRateLimiting(endpoint.RateLimit ?? "public").WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
                }
                else if (endpoint.RateLimit is { } policy)
                {
                    route.RequireRateLimiting(policy);
                }

                // Uploads may be as large as their file field allows.
                if (upload)
                {
                    var limit = FormDataBinder.RequestLimit(arguments[1]);
                    route.WithMetadata(new RequestSizeLimitAttribute(limit), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = limit });
                }
            }

            return endpoints;
        }

        // No automatic redirects, cookies or request logs.
        // A metadata provider's client also reports how its requests ended, for the health check.
        private static void AddClient<TClient>(IServiceCollection services, int timeoutSeconds, int? bufferMegabytes, bool agent = false, Func<HttpMessageHandler>? handler = null,
            Uri? baseAddress = null, string? provider = null) where TClient : class
        {
            var client = services.AddHttpClient<TClient>(http =>
            {
                http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                http.BaseAddress = baseAddress;
                if (bufferMegabytes is { } megabytes)
                {
                    http.MaxResponseContentBufferSize = megabytes * 1024L * 1024;
                }

                if (agent)
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Jularr/1.0 (+https://github.com/Juloc/Jularr)");
                }
            }).ConfigurePrimaryHttpMessageHandler(handler ?? (() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })).RemoveAllLoggers();
            if (provider is not null)
            {
                client.AddHttpMessageHandler(services => new ProviderObservationHandler(services.GetRequiredService<ProviderObservations>(), provider));
            }
        }

        private static Delegate CreateParameterHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http,
                [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                if (service.OperationType != ServiceOperationType.Read && !IsAppRequest(http))
                {
                    await antiforgery.ValidateRequestAsync(http);
                }

                var result = await service.ExecuteAsync(parameters, default!, cancellationToken);

                // A cast device plays from another origin.
                if (result is ICrossOriginOutput)
                {
                    http.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    http.Response.Headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges";
                }

                // Authorized files stream after the service committed.
                if (result is ICacheableFileOutput stored)
                {
                    http.Response.Headers.CacheControl = stored.IsVersioned ? "private,max-age=31536000,immutable"
                        : stored.MaxAge is { } age ? "private,max-age=" + ((long)age.TotalSeconds).ToString(global::System.Globalization.CultureInfo.InvariantCulture) : "private,no-cache";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
                    http.Response.Headers.Vary = "Accept";
                    return Results.Stream(stored.Content, stored.ContentType, null, stored.LastModified, new Microsoft.Net.Http.Headers.EntityTagHeaderValue(stored.ETag), true);
                }

                if (result is IFileOutput file)
                {
                    http.Response.Headers.CacheControl = file is IImmutableFileOutput ? "private,max-age=31536000,immutable" : "private,no-store";
                    return (IResult)Results.Stream(file.Content, file.ContentType, enableRangeProcessing: true);
                }

                if (result is IDownloadOutput download)
                {
                    http.Response.Headers.CacheControl = "private,no-store";
                    return Results.File(download.Bytes, download.ContentType, download.FileName);
                }

                if (result is IBinaryOutput binary)
                {
                    http.Response.Headers.CacheControl = "private,max-age=3600";
                    http.Response.Headers.XContentTypeOptions = "nosniff";

                    // A vector picture shown on its own runs no script.
                    if (binary.ContentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
                    {
                        http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:";
                    }

                    return Results.Bytes(binary.Bytes, binary.ContentType);
                }

                if (result is ITextOutput text)
                {
                    http.Response.Headers.CacheControl = "private,no-store";
                    return Results.Text(text.Text, text.ContentType);
                }

                if (service.OperationType == ServiceOperationType.Read)
                {
                    http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
                }

                await ApplyAuthenticationAsync(http, result, antiforgery);
                return (IResult)Results.Ok<object>(result);
            };

        private static Delegate CreateBodyHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromBody] TData data, [FromServices] TService service,
                HttpContext http, [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                if (!IsAppRequest(http))
                {
                    await antiforgery.ValidateRequestAsync(http);
                }

                var result = await service.ExecuteAsync(parameters, data, cancellationToken);
                await ApplyAuthenticationAsync(http, result, antiforgery);
                return Results.Ok<object>(result);
            };

        private static Delegate CreateUploadHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http, [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                if (!IsAppRequest(http))
                {
                    await antiforgery.ValidateRequestAsync(http);
                }

                if (!http.Request.HasFormContentType)
                {
                    throw new BadHttpRequestException("A form with the file is required.");
                }

                await service.AuthorizeAsync(parameters, cancellationToken);

                var data = FormDataBinder.Bind<TData>(await http.Request.ReadFormAsync(cancellationToken));
                var result = await service.ExecuteAsync(parameters, data, cancellationToken);
                await ApplyAuthenticationAsync(http, result, antiforgery);
                return Results.Ok<object>(result);
            };

        // No cookies, no cross-site forgery.
        internal static bool IsAppRequest(HttpContext http) =>
            http.User.Identity?.AuthenticationType == DeviceBearerScheme || http.GetEndpoint()?.Metadata.GetMetadata<ServiceEndpointAttribute>()?.ForApps == true;

        // Razor shares committed auth effects.
        public static async Task ApplyAuthenticationAsync(HttpContext http, IServiceOutput result, IAntiforgery antiforgery)
        {
            http.Response.Headers.CacheControl = "no-store";

            // Apps receive tokens, not cookies.
            if (IsAppRequest(http))
            {
                if (result is ISessionResult { Session: { } appSession })
                {
                    http.Response.Headers[DeviceTokenHeader] = DeviceBearerHandler.Token(appSession);
                }

                return;
            }

            // Clear only after committed logout.
            if (result is ISessionEndResult { EndsCurrentSession: true })
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                http.User = new ClaimsPrincipal(new ClaimsIdentity());
                http.Response.Cookies.Delete(AuthenticationNonceCookieName, new CookieOptions { Path = "/", Secure = http.Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Strict });
                http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
            }

            // Publish the committed session.
            if (result is ISessionResult { Session: { } session })
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString(CultureInfo.InvariantCulture)),
                    new Claim(ServiceGate.SessionClaimType, session.PublicId.ToString("D")),
                    new Claim(ServiceGate.SessionRevisionClaimType, session.RotationRevision.ToString(CultureInfo.InvariantCulture)),
                    new Claim(ServiceGate.SessionTokenClaimType, session.Token)], CookieAuthenticationDefaults.AuthenticationScheme));
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                    new AuthenticationProperties { ExpiresUtc = session.ExpiresAt, IsPersistent = true });
                http.User = principal;
                http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
                BrowserDevice.Issue(http);
            }

            if (result is IAuthenticationChallengeResult challenge)
            {
                http.Response.Cookies.Append(AuthenticationNonceCookieName, challenge.BrowserNonce, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = challenge.ExpiresAt
                });
            }
        }

        private sealed record ServiceCatalog(Type[] ServiceTypes);
    }
}
