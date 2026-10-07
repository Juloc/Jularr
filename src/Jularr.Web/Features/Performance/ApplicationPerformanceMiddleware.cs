using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.SignalR;

namespace Jularr.Web.Features.Performance;

/// <summary>
/// Times every routed request under its route template (never the concrete URL), counts requests in flight for the governor and counts refused
/// calls of rate-limited endpoints. Long-lived SignalR connections are not requests in this sense and are left out of both.
/// </summary>
public sealed class ApplicationPerformanceMiddleware(RequestDelegate next, ApplicationPerformanceTelemetry telemetry, InteractiveLoad load)
{
    public const string UnmatchedKey = "unmatched";

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<HubMetadata>() is not null || context.WebSockets.IsWebSocketRequest)
        {
            await next(context);
            return;
        }

        using var inFlight = load.Begin();
        var started = Stopwatch.GetTimestamp();
        var outcome = PerformanceOutcome.Succeeded;
        try
        {
            await next(context);
            if (context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                telemetry.RecordRateLimited(RouteKey(endpoint));
            }

            if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                outcome = PerformanceOutcome.Failed;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            outcome = PerformanceOutcome.Cancelled;
            throw;
        }
        catch
        {
            outcome = PerformanceOutcome.Failed;
            throw;
        }
        finally
        {
            telemetry.Record(PerformanceCategory.Route, $"{context.Request.Method} {RouteKey(endpoint)}", Stopwatch.GetElapsedTime(started), outcome);
        }
    }

    /// <summary>The route template of the matched endpoint; anything unmatched shares one identity so a scanner cannot create keys.</summary>
    public static string RouteKey(Endpoint? endpoint) => (endpoint as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } pattern ? pattern : UnmatchedKey;
}

public static class ApplicationPerformanceExtensions
{
    /// <summary>The telemetry aggregate, the interactive-load counter, the background governor, the provider timing filter and the database diagnostics.</summary>
    public static IServiceCollection AddApplicationPerformance(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ApplicationPerformanceTelemetry>();
        services.AddSingleton<InteractiveLoad>();
        services.AddSingleton<BackgroundWorkGovernor>();
        services.AddSingleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, ProviderTelemetryHandlerFilter>();
        services.AddScoped<DatabaseDiagnosticsService>();
        return services;
    }

    /// <summary>Placed after routing so the endpoint is known; the measurement therefore includes authentication, rate limiting and the handler.</summary>
    public static IApplicationBuilder UseApplicationPerformance(this IApplicationBuilder app) => app.UseMiddleware<ApplicationPerformanceMiddleware>();
}
