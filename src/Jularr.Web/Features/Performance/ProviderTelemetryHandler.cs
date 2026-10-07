using System.Diagnostics;
using Microsoft.Extensions.Http;

namespace Jularr.Web.Features.Performance;

/// <summary>
/// Times every call of one named HTTP client. The identity is the client's registered name (a provider adapter), never the request URL, host or
/// query, so no secret or user input can reach the telemetry or make its key set grow.
/// </summary>
public sealed class ProviderTelemetryHandler(ApplicationPerformanceTelemetry telemetry, string provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = PerformanceOutcome.Failed;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            outcome = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? PerformanceOutcome.Failed
                : PerformanceOutcome.Succeeded;
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = PerformanceOutcome.Cancelled;
            throw;
        }
        finally
        {
            telemetry.Record(PerformanceCategory.Provider, provider, Stopwatch.GetElapsedTime(started), outcome);
        }
    }
}

/// <summary>Puts <see cref="ProviderTelemetryHandler"/> in front of every named or typed HTTP client, so a new provider is measured without being touched.</summary>
public sealed class ProviderTelemetryHandlerFilter(ApplicationPerformanceTelemetry telemetry) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Insert(0, new ProviderTelemetryHandler(telemetry, builder.Name ?? "unnamed"));
    };
}
