using Jularr.Web.Features.Vocabulary;
using Microsoft.AspNetCore.Antiforgery;

namespace Jularr.Web.Features.Learning.LanguageAssistance;

/// <summary>
/// JSON endpoints behind <c>wwwroot/js/language-inspector.js</c>. They require
/// a signed-in profile and the antiforgery request token that the
/// <c>_LanguageInspector</c> partial renders into its configuration.
/// </summary>
public static class LanguageInspectorEndpoints
{
    public const string BasePath = "/api/language-inspector";

    public static IEndpointRouteBuilder MapLanguageInspector(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(BasePath)
            .RequireAuthorization();

        group.MapPost("/inspect", (
                LanguageInspectRequest request,
                LanguageInspectorService service,
                IAntiforgery antiforgery,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            RunAsync(
                httpContext,
                antiforgery,
                () => service.InspectAsync(request, cancellationToken)));

        group.MapPost("/state", (
                LanguageWordStateRequest request,
                LanguageInspectorService service,
                IAntiforgery antiforgery,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            RunAsync(
                httpContext,
                antiforgery,
                () => service.SetStateAsync(request, cancellationToken)));

        group.MapPost("/explain", (
                LanguageExplainRequest request,
                LanguageInspectorService service,
                IAntiforgery antiforgery,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            RunAsync(
                httpContext,
                antiforgery,
                () => service.ExplainAsync(request, cancellationToken)));

        return endpoints;
    }

    private static async Task<IResult> RunAsync<T>(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        Func<Task<T>> action)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        if (!await antiforgery.IsRequestValidAsync(httpContext))
        {
            return Error(StatusCodes.Status400BadRequest, "The request token is invalid. Reload the page.");
        }

        try
        {
            return Results.Ok(await action());
        }
        catch (LanguageAssistanceDeniedException exception)
        {
            return Error(StatusCodes.Status403Forbidden, exception.Message);
        }
        catch (JapaneseAnalysisUnavailableException exception)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Error(StatusCodes.Status404NotFound, exception.Message);
        }
        catch (ArgumentException exception)
        {
            var message = exception.ParamName is { } parameter
                ? exception.Message.Replace($" (Parameter '{parameter}')", "", StringComparison.Ordinal)
                : exception.Message;
            return Error(StatusCodes.Status400BadRequest, message);
        }
        catch (InvalidOperationException exception)
        {
            return Error(StatusCodes.Status409Conflict, exception.Message);
        }
        catch (HttpRequestException)
        {
            return Error(StatusCodes.Status502BadGateway, "The AI provider is not reachable.");
        }
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);
}
