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
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Igdb;
using Jularr.Integrations.Logic.Libretro;
using Jularr.Integrations.Logic.MusicBrainz;
using Jularr.Integrations.Logic.Common;
using Jularr.Integrations.Logic.Gutenberg;
using Jularr.Integrations.Logic.Opds;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Integrations.Logic.MediaServer;
using Jularr.Integrations.Logic.Plex;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Servarr;
using Jularr.Integrations.Logic.Tmdb;
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

            // Keep keys outside releases.
            if (configuration["DataProtection:KeyDirectory"] is { Length: > 0 } keyDirectory)
            {
                if (!Path.IsPathFullyQualified(keyDirectory))
                {
                    throw new InvalidOperationException("DataProtection:KeyDirectory must be an absolute persistent path.");
                }

                var protection = services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
                if (OperatingSystem.IsWindows())
                {
                    protection.ProtectKeysWithDpapi();
                }
            }

            services.Configure<AuthenticationOptions>(configuration.GetSection("Authentication"));
            services.Configure<StorageOptions>(configuration.GetSection("Storage"));
            services.AddSingleton(new NasMountClient { SocketPath = configuration["Storage:MountHelperSocket"] });
            services.Configure<MediaOptions>(configuration.GetSection("Media"));
            services.AddSingleton(new MediaProbeClient { ExecutablePath = configuration["Media:FfprobePath"] });
            services.AddSingleton(new MediaTranscoder { ExecutablePath = configuration["Media:FfmpegPath"] });
            services.AddSingleton(new ArchiveTools { Par2Path = configuration["Usenet:Par2Path"], SevenZipPath = configuration["Usenet:SevenZipPath"] });

            // Backups reach the same database.
            var target = new NpgsqlConnectionStringBuilder(connectionString);
            services.Configure<BackupOptions>(options =>
            {
                configuration.GetSection("Backup").Bind(options);
                options.KeyDirectory = configuration["DataProtection:KeyDirectory"];
            });
            services.AddSingleton(new PostgresBackupTools
            {
                PgDumpPath = configuration["Backup:PgDumpPath"],
                PgRestorePath = configuration["Backup:PgRestorePath"],
                Connection = new PostgresConnection(target.Host ?? "localhost", target.Port, target.Database ?? "", target.Username ?? "", target.Password)
            });
            return RegisterServices(services, [typeof(Service).Assembly]);
        }

        internal static IServiceCollection AddJularrServices(this IServiceCollection services, NpgsqlDataSource database, params Assembly[] assemblies)
        {
            // Tests own isolated data sources.
            services.AddSingleton(new SqlDatabase(database));
            return RegisterServices(services, assemblies);
        }

        private static IServiceCollection RegisterServices(IServiceCollection services, Assembly[] assemblies)
        {
            // Recent warnings and errors for the Owner's system page.
            services.TryAddSingleton<RecentLogs>();
            services.TryAddSingleton<ServiceTimings>();
            services.TryAddSingleton<ResourceHistory>();
            services.AddHostedService(provider => provider.GetRequiredService<ResourceHistory>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RecentLogs>(provider => provider.GetRequiredService<RecentLogs>()));

            // Discover concrete services.
            var serviceTypes = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsVisible && !type.IsAbstract && !type.ContainsGenericParameters && typeof(Service).IsAssignableFrom(type))
                .Distinct().ToArray();

            // Register shared infrastructure.
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<NasMountClient>();
            services.TryAddSingleton<MediaProbeClient>();
            services.TryAddSingleton<WakeOnLanClient>();
            services.TryAddSingleton<MediaTranscoder>();
            services.TryAddSingleton<PlaybackTranscodes>();
            services.TryAddTransient<PlaybackSubtitles>();
            services.TryAddTransient<WantedAcquisition>();
            services.TryAddTransient<WorkMetadata>();
            services.TryAddTransient<PlexServer>();
            services.TryAddTransient<MediaServers>();
            services.TryAddTransient<LibraryReplacements>();
            services.TryAddSingleton<DevicePairingAttempts>();
            services.TryAddTransient<BookMetadata>();
            services.TryAddTransient<GameRomIdentification>();
            services.TryAddTransient<AniListMetadata>();
            services.TryAddTransient<AniListMatcher>();
            services.TryAddTransient<MusicMetadata>();
            services.TryAddTransient<GameMetadata>();
            services.TryAddTransient<DirectSources>();
            services.TryAddTransient<SubtitleProviders>();
            services.TryAddTransient<DownloadImport>();
            services.TryAddSingleton<UsenetDownloader>();
            services.TryAddSingleton<ArchiveTools>();
            services.TryAddSingleton<PostgresBackupTools>();
            services.TryAddTransient<UsenetJob>();
            services.TryAddSingleton<ISourceGateResolver, NoSourceGateResolver>();
            services.TryAddTransient<ReadingContent>();
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
                .Validate(options => !options.PlexRegistrationEnabled || options.PlexLoginEnabled
                    && options.PlexUiLocaleId > 0 && options.PlexRegistrationLibraryKeys.Length > 0
                    && options.PlexRegistrationLibraryKeys.All(key => key.Length > 0 && key.All(char.IsAsciiDigit))
                    && options.PlexServerIdentifier.Length > 0 && options.PlexServerUri is { IsAbsoluteUri: true } uri
                    && uri.Scheme == "https" && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0
                    && uri.Query.Length == 0 && uri.Fragment.Length == 0, "Plex registration requires a configured HTTPS server and libraries.")
                .ValidateOnStart();

            // Harden session cookies.
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.EventsType = typeof(SessionCookieEvents);
                options.SlidingExpiration = false;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
            });

            // Bound external communication; pasted source links reach public addresses only.
            AddClient<PlexClient>(services, 10, 1);
            AddClient<GutenbergClient>(services, 60, null, agent: true);
            AddClient<OpdsClient>(services, 60, null, agent: true);
            AddClient<AnnasArchiveClient>(services, 60, null, agent: true, handler: PublicNetwork.CreateHandler);
            AddClient<OpenLibraryClient>(services, 15, 4, agent: true);
            AddClient<AniListClient>(services, 15, 4, agent: true);
            AddClient<MusicBrainzClient>(services, 20, 4, agent: true);
            AddClient<IgdbClient>(services, 20, 8, agent: true);
            AddClient<LibretroClient>(services, 60, 16, agent: true);
            AddClient<SynologyClient>(services, 30, 4, handler: SynologyClient.CreateHandler);
            AddClient<ServarrApiClient>(services, 60, 32);
            AddClient<PlexServerClient>(services, 20, 16);
            AddClient<MediaServerClient>(services, 30, 32);
            AddClient<NewznabClient>(services, 30, 16);
            AddClient<TmdbClient>(services, 15, 4, baseAddress: new Uri("https://api.themoviedb.org/3/"));
            AddClient<SabnzbdClient>(services, 15, 16);
            AddClient<OpenSubtitlesClient>(services, 30, 12, agent: true);
            AddClient<SubDlClient>(services, 30, 12, agent: true);

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
                    route.RequireRateLimiting("public").WithMetadata(new RequestSizeLimitAttribute(16 * 1024));
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
        private static void AddClient<TClient>(IServiceCollection services, int timeoutSeconds, int? bufferMegabytes, bool agent = false, Func<HttpMessageHandler>? handler = null,
            Uri? baseAddress = null) where TClient : class =>
            services.AddHttpClient<TClient>(http =>
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

        private static Delegate CreateParameterHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http,
                [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                if (service.OperationType != ServiceOperationType.Read)
                {
                    await antiforgery.ValidateRequestAsync(http);
                }

                var result = await service.ExecuteAsync(parameters, default!, cancellationToken);

                // Authorized files stream after the service committed.
                if (result is IFileOutput file)
                {
                    http.Response.Headers.CacheControl = "private,no-store";
                    return (IResult)Results.File(file.FilePath, file.ContentType, enableRangeProcessing: true);
                }

                if (result is IBinaryOutput binary)
                {
                    http.Response.Headers.CacheControl = "private,max-age=3600";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
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
                await antiforgery.ValidateRequestAsync(http);
                var result = await service.ExecuteAsync(parameters, data, cancellationToken);
                await ApplyAuthenticationAsync(http, result, antiforgery);
                return Results.Ok<object>(result);
            };

        private static Delegate CreateUploadHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http, [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(http);
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

        // Razor shares committed auth effects.
        public static async Task ApplyAuthenticationAsync(HttpContext http, IServiceOutput result, IAntiforgery antiforgery)
        {
            http.Response.Headers.CacheControl = "no-store";

            // Clear only after committed logout.
            if (result is ISessionEndResult { EndsCurrentSession: true })
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                http.User = new ClaimsPrincipal(new ClaimsIdentity());
                http.Response.Cookies.Delete(AuthenticationNonceCookieName, new CookieOptions { Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });
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
            }

            if (result is IAuthenticationChallengeResult challenge)
            {
                http.Response.Cookies.Append(AuthenticationNonceCookieName, challenge.BrowserNonce, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = challenge.ExpiresAt
                });
            }
        }

        private sealed record ServiceCatalog(Type[] ServiceTypes);
    }
}
