using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Shell;

namespace Jularr.Web.Features.ClientApi;

/// <summary>An explicit playback intent. A Movie targets its Work; a Series targets one episode, or no episode for the next required one.</summary>
public sealed record ClientPlaybackIntentRequest(ClientVideoTarget? Target);

/// <param name="Target">The unit to play or the one being acquired; for a Series asked for its next episode this names the episode that was chosen.</param>
/// <param name="RequestId">The canonical request the intent created or attached to; read its state with <c>GET requests/{requestId}</c>.</param>
public sealed record ClientPlaybackIntentResponse(PlaybackIntentOutcome Outcome, ClientVideoTarget? Target, Guid? RequestId, ConsumerAcquisitionView? Acquisition);

/// <summary>The consumer state of one request for one unit of it: <see cref="ConsumerAcquisitionView"/> and nothing technical.</summary>
public sealed record ClientRequestStatusResponse(Guid RequestId, ClientVideoTarget? Target, ConsumerAcquisitionView Acquisition);

/// <summary>
/// Reads the bounded body of a playback intent before anything else touches it: a body is a pair of ids, so more than
/// <see cref="ClientApiPlaybackIntentEndpoints.MaxBodyBytes"/> bytes is refused with 413 without being parsed. The parsed request is handed
/// to the access filter and the handler through <see cref="HttpContext.Items"/>.
/// </summary>
public sealed class ClientPlaybackIntentBodyFilter : IEndpointFilter
{
    public const string ItemKey = "playback-intent-request";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var buffer = new byte[ClientApiPlaybackIntentEndpoints.MaxBodyBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > ClientApiPlaybackIntentEndpoints.MaxBodyBytes)
        {
            return Results.Json(new ClientErrorResponse("playback_intent_too_large", $"A playback intent is at most {ClientApiPlaybackIntentEndpoints.MaxBodyBytes} bytes."), statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        ClientPlaybackIntentRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ClientPlaybackIntentRequest>(buffer.AsSpan(0, length), s_json);
        }
        catch (JsonException)
        {
        }

        if (request?.Target is not { IsValid: true })
        {
            return Results.BadRequest(new ClientErrorResponse("invalid_playback_target", "target.workId is required and target.workEpisodeId must be a valid id when present."));
        }

        http.Items[ItemKey] = request;
        return await next(context);
    }
}

/// <summary>
/// Instant Play over the client API (additive to version 2): <c>POST video/playback-intents</c> starts or attaches to the acquisition of
/// the target and <c>GET requests/{requestId}</c> reads its consumer projection. Authorization is decided here and in the services, never
/// by hidden UI: the media type must be visible to the profile, the intent needs the request capability and auto-approval, and a status
/// read needs the request itself, request management or the request capability for that media type. A manager-only instance answers
/// 404 for the intent (the Playback instance gate); the status read stays, as Request state is not playback.
/// </summary>
public static class ClientApiPlaybackIntentEndpoints
{
    public const int MaxBodyBytes = 1024;

    public static IEndpointRouteBuilder MapClientApiPlaybackIntentsV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(ClientApiContract.BasePath)
            .RequireAuthorization();

        group.MapPost("/video/playback-intents", async (HttpContext httpContext, PlaybackIntentService intents, CancellationToken cancellationToken) =>
        {
            var target = ((ClientPlaybackIntentRequest)httpContext.Items[ClientPlaybackIntentBodyFilter.ItemKey]!).Target!;
            var result = await intents.StartAsync(target.WorkId, target.WorkEpisodeId, cancellationToken);
            if (result.Outcome == PlaybackIntentOutcome.TargetNotFound)
            {
                return Results.NotFound(new ClientErrorResponse("video_target_not_found", "The requested video does not exist."));
            }

            return Results.Ok(new ClientPlaybackIntentResponse(result.Outcome, new ClientVideoTarget(target.WorkId, result.Action?.WorkEpisodeId ?? target.WorkEpisodeId), result.RequestId, result.Acquisition));
        })
        .AddEndpointFilter<ClientPlaybackIntentBodyFilter>()
        .AddEndpointFilter<ClientVideoAccessFilter>()
        .RequireRateLimiting(InstantPlayRegistration.IntentRateLimitPolicy);

        group.MapGet("/requests/{requestId:guid}", async (
            Guid requestId,
            Guid? workEpisodeId,
            ConsumerAcquisitionQuery projection,
            AcquisitionRequestService requestService,
            IAppShellService appShell,
            IInstanceModuleService modules,
            CurrentAccountContext account,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var read = await projection.FindAsync(requestId, workEpisodeId, cancellationToken);
            if (read is null
                || !(await appShell.GetMediaAccessAsync(httpContext.User, cancellationToken)).IsVisible(AcquisitionAccessNames.WorkType(read.Request.Kind)))
            {
                return NotFound();
            }

            // One open request per title is shared state, so what a profile that may request this media type reads is exactly what the
            // detail page already shows it; nothing of the requester is part of the projection.
            if (read.Request.RequestedByProfileId != account.ProfileId && !account.Can(JularrPolicies.AdminMedia) && !(await requestService.GetCapabilitiesAsync(read.Request.Kind, cancellationToken)).CanRequest)
            {
                return NotFound();
            }

            var view = await projection.ProjectAsync(read.Request, workEpisodeId, await modules.IsEnabledAsync(InstanceModule.Playback, cancellationToken), cancellationToken);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new ClientRequestStatusResponse(read.Request.Id, new ClientVideoTarget(read.WorkId, workEpisodeId), view));

            static IResult NotFound() => Results.NotFound(new ClientErrorResponse("request_not_found", "The requested acquisition does not exist."));
        })
        .RequireRateLimiting(InstantPlayRegistration.StatusRateLimitPolicy);

        return endpoints;
    }
}
