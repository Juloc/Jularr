using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Plex;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    public static class ServiceApi
    {
        public const string AuthenticationNonceCookieName = "jularr.auth_nonce";

        public static IServiceCollection AddJularrServices(this IServiceCollection services, NpgsqlDataSource database, params Assembly[] assemblies)
        {
            var serviceTypes = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsVisible && !type.IsAbstract && !type.ContainsGenericParameters && typeof(Service).IsAssignableFrom(type))
                .Distinct().ToArray();
            services.AddSingleton(database);
            services.AddSingleton(new ServiceCatalog(serviceTypes));
            services.AddHttpContextAccessor();
            services.AddAntiforgery();
            services.AddProblemDetails();
            services.AddExceptionHandler<ServiceExceptionHandler>();
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            services.AddScoped<IServiceGate, ServiceGate>();
            services.AddScoped<SessionAuthentication>();
            services.AddScoped<SessionCookieEvents>();
            services.AddDataProtection();
            services.AddOptions<AuthenticationOptions>()
                .Validate(options => options.SessionLifetimeHours is >= 1 and <= 720, "Session lifetime must be 1-720 hours.")
                .Validate(options => !options.PlexRegistrationEnabled || options.PlexLoginEnabled
                    && options.PlexUiLocaleId > 0 && options.PlexRegistrationLibraryKeys.Length > 0
                    && options.PlexRegistrationLibraryKeys.All(key => key.Length > 0 && key.All(char.IsAsciiDigit))
                    && options.PlexServerIdentifier.Length > 0 && options.PlexServerUri is { IsAbsoluteUri: true } uri
                    && uri.Scheme == "https" && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0
                    && uri.Query.Length == 0 && uri.Fragment.Length == 0, "Plex registration requires a configured HTTPS server and libraries.")
                .ValidateOnStart();
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.EventsType = typeof(SessionCookieEvents);
                options.SlidingExpiration = false;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
            });
            services.AddHttpClient<PlexClient>(http =>
            {
                http.Timeout = TimeSpan.FromSeconds(10);
                http.MaxResponseContentBufferSize = 1024 * 1024;
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                .RemoveAllLoggers();
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
                    Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "rate_limit_exceeded")
                        .ExecuteAsync(context.HttpContext));
            });
            services.AddScoped<ServiceRuntime>();
            foreach (var type in serviceTypes)
            {
                services.AddScoped(type, provider =>
                {
                    var service = (Service)ActivatorUtilities.CreateInstance(provider, type);
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

                var factory = typeof(ServiceApi).GetMethod(noData ? nameof(CreateParameterHandler) : nameof(CreateBodyHandler), BindingFlags.Static | BindingFlags.NonPublic)!;
                var handler = (Delegate)factory.MakeGenericMethod([type, .. arguments]).Invoke(null, null)!;
                var route = endpoints.MapMethods(endpoint.Route, [method], handler)
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
            }

            return endpoints;
        }

        private static Delegate CreateParameterHandler<TService, TParameters, TData>() where TService : Service<TParameters, TData> =>
            async ([AsParameters] TParameters parameters, [FromServices] TService service, HttpContext http,
                [FromServices] IAntiforgery antiforgery, CancellationToken cancellationToken) =>
            {
                if (service.OperationType != ServiceOperationType.Read)
                {
                    await antiforgery.ValidateRequestAsync(http);
                }

                var result = await service.ExecuteAsync(parameters, default!, cancellationToken);
                if (service.OperationType == ServiceOperationType.Read)
                {
                    http.Response.Headers["RequestVerificationToken"] = antiforgery.GetAndStoreTokens(http).RequestToken;
                }

                await ApplyAuthenticationAsync(http, result, antiforgery);
                return Results.Ok<object>(result);
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

        // Razor callers use the same committed authentication result without an HTTP round-trip.
        public static async Task ApplyAuthenticationAsync(HttpContext http, IServiceOutput result, IAntiforgery antiforgery)
        {
            http.Response.Headers.CacheControl = "no-store";

            // Session effects run only after the service transaction has committed.
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
