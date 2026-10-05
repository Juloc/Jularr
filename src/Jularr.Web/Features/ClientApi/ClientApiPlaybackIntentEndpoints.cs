using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ClientApi;

/// <summary>An explicit playback intent. A Movie targets its Work; a Series targets one episode, or no episode for the next required one.</summary>
public sealed record ClientPlaybackIntentRequest(ClientVideoTarget? Target);

/// <param name="Target">The unit to play or the one being acquired; for a Series asked for its next episode this names the episode that was chosen.</param>
/// <param name="RequestId">The canonical request the intent created or attached to; read its state with <c>GET requests/{requestId}</c>.</param>
public sealed record ClientPlaybackIntentResponse(PlaybackIntentOutcome Outcome, ClientVideoTarget? Target, Guid? RequestId, ConsumerAcquisitionView? Acquisition);

/// <summary>The consumer state of one request for one unit of it: <see cref="ConsumerAcquisitionView"/> and nothing technical.</summary>
public sealed record ClientRequestStatusResponse(Guid RequestId, ClientVideoTarget? Target, ConsumerAcquisitionView Acquisition);

/// <summary>
/// Instant Play over the client API (additive to version 2): <c>POST video/playback-intents</c> starts or attaches to the acquisition of
/// the target and <c>GET requests/{requestId}</c> reads its consumer projection. Authorization is decided here and in the services, never
/// by hidden UI: the media type must be visible to the profile, the intent needs the request capability and auto-approval, and a status
/// read needs the request itself, request management or the request capability for that media type. A manager-only instance answers
/// 404 for the intent (the Playback instance gate); the status read stays, as Request state is not playback.
/// </summary>
public static class ClientApiPlaybackIntentEndpoints
{
    private const int MaxIntentBodyBytes = 1024;

    public static IEndpointRouteBuilder MapClientApiPlaybackIntentsV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(ClientApiContract.BasePath)
            .RequireAuthorization();

        group.MapPost("/video/playback-intents", async (
            ClientPlaybackIntentRequest request,
            PlaybackIntentService intents,
            CancellationToken cancellationToken) =>
        {
            if (request.Target is not { IsValid: true } target)
            {
                return Results.BadRequest(new ClientErrorResponse("invalid_playback_target", "target.workId is required and target.workEpisodeId must be a valid id when present."));
            }

            var result = await intents.StartAsync(target.WorkId, target.WorkEpisodeId, cancellationToken);
            if (result.Outcome == PlaybackIntentOutcome.TargetNotFound)
            {
                return Results.NotFound(new ClientErrorResponse("video_target_not_found", "The requested video does not exist."));
            }

            return Results.Ok(new ClientPlaybackIntentResponse(result.Outcome, new ClientVideoTarget(target.WorkId, result.Action?.WorkEpisodeId ?? target.WorkEpisodeId), result.RequestId, result.Acquisition));
        })
        .AddEndpointFilter<ClientVideoAccessFilter>()
        .WithMetadata(new RequestSizeLimitAttribute(MaxIntentBodyBytes))
        .RequireRateLimiting(InstantPlayRegistration.IntentRateLimitPolicy);

        group.MapGet("/requests/{requestId:guid}", async (
            Guid requestId,
            Guid? workEpisodeId,
            AcquisitionAccessStore requests,
            AcquisitionRequestService requestService,
            ConsumerAcquisitionQuery projection,
            VideoRequestWorkResolver works,
            IAppShellService appShell,
            IInstanceModuleService modules,
            CurrentAccountContext account,
            AppDbContext db,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var request = await requests.GetAsync(requestId, cancellationToken);
            if (request is null || request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
            {
                return NotFound();
            }

            var mediaType = AcquisitionAccessNames.WorkType(request.Kind);
            if (!(await appShell.GetMediaAccessAsync(httpContext.User, cancellationToken)).IsVisible(mediaType))
            {
                return NotFound();
            }

            // One open request per title is shared state, so what a profile that may request this media type reads is exactly what the
            // detail page already shows it; nothing of the requester is part of the projection.
            if (request.RequestedByProfileId != account.ProfileId && !account.Can(JularrPolicies.AdminMedia) && !(await requestService.GetCapabilitiesAsync(request.Kind, cancellationToken)).CanRequest)
            {
                return NotFound();
            }

            var work = (await works.ResolveAsync([request], cancellationToken)).GetValueOrDefault(request.Id);
            if (work is null)
            {
                return NotFound();
            }

            if (workEpisodeId is { } episodeId && (request.Kind == MediaAcquisitionKind.Movie || !await db.WorkEpisodes.AsNoTracking().AnyAsync(x => x.Id == episodeId && x.WorkId == work.WorkId, cancellationToken)))
            {
                return NotFound();
            }

            var view = await projection.ProjectAsync(request, workEpisodeId, await modules.IsEnabledAsync(InstanceModule.Playback, cancellationToken), cancellationToken);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new ClientRequestStatusResponse(request.Id, new ClientVideoTarget(work.WorkId, workEpisodeId), view));

            static IResult NotFound() => Results.NotFound(new ClientErrorResponse("request_not_found", "The requested acquisition does not exist."));
        })
        .RequireRateLimiting(InstantPlayRegistration.StatusRateLimitPolicy);

        return endpoints;
    }
}
