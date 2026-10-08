using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Api;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Monitoring;

public sealed record SetTargetMonitoringRequest(bool? Monitored);

public sealed record SetRelationMonitoringRequest(string Label, IReadOnlyList<string>? Roles, bool Monitored, bool OnlyFuture);

public sealed record WorkMonitoringResponse(Guid WorkId, bool Monitored, bool ReachedByRelation, IReadOnlyList<WorkMonitoringDecisionResponse> Decisions);

public sealed record WorkMonitoringDecisionResponse(Guid TargetId, MonitoringTargetKind Kind, bool Monitored);

/// <summary>
/// The owner API of canonical Monitoring (<c>/api/monitoring/v1</c>), authenticated like the acquisition API. Every call is one command: it writes ordinary
/// decisions in one batch and then lets the Work's request follow, so a client never loops over episodes or tracks.
/// </summary>
public static class MonitoringEndpoints
{
    public const string BasePath = "/api/monitoring/v1";

    public static IEndpointRouteBuilder MapMonitoringApiV1(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(BasePath)
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, AcquisitionApiKeyAuthenticationHandler.SchemeName)
                .RequireRole(AccountRoles.Owner))
            .RequireRateLimiting("acquisitionApi");

        group.MapGet("/works/{workId:guid}", async (Guid workId, MonitoringResolver monitoring, CancellationToken cancellationToken) =>
        {
            var view = await monitoring.LoadAsync(workId, cancellationToken);
            return Results.Ok(new WorkMonitoringResponse(workId, view.IsWorkMonitored, view.IsRelationMonitored, [
                .. Enum.GetValues<MonitoringTargetKind>().SelectMany(kind => new[] { true, false }.SelectMany(value => view.DecidedIds(kind, value).Select(id => new WorkMonitoringDecisionResponse(id, kind, value))))
            ]));
        });

        group.MapPut("/targets/{kind}/{targetId:guid}", async (
            MonitoringTargetKind kind,
            Guid targetId,
            SetTargetMonitoringRequest request,
            MonitoringCommands commands,
            MonitoringFollower follower,
            CancellationToken cancellationToken) =>
            await commands.SetAsync(kind, targetId, request.Monitored, cancellationToken) is { } workId
                ? Results.Ok(await follower.FollowAsync(workId, cancellationToken))
                : Results.NotFound());

        group.MapPost("/works/{workId:guid}/future", async (Guid workId, MonitoringCommands commands, MonitoringFollower follower, CancellationToken cancellationToken) =>
        {
            await commands.FutureAsync(workId, cancellationToken);
            return Results.Ok(await follower.FollowAsync(workId, cancellationToken));
        });

        group.MapPut("/relations/{kind}/{sourceKey}", async (
            MonitoringRelationKind kind,
            string sourceKey,
            SetRelationMonitoringRequest request,
            MonitoringCommands commands,
            CurrentAccountContext account,
            CancellationToken cancellationToken) =>
        {
            await commands.SetRelationAsync(new MonitoringRelationSource(kind, sourceKey, request.Label, request.Roles, account.ProfileId), request.Monitored, request.OnlyFuture, cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }
}

/// <summary>Lets the request of a Work follow its monitoring after a command: the Movie or Series request is woken, ended or opened as the Work now says.</summary>
public sealed class MonitoringFollower(AppDbContext db, VideoMonitoringService video)
{
    public async Task<VideoMonitoringOutcome?> FollowAsync(Guid workId, CancellationToken cancellationToken)
    {
        var type = await db.Works.AsNoTracking().Where(work => work.Id == workId).Select(work => (WorkMediaType?)work.MediaType).FirstOrDefaultAsync(cancellationToken);
        return type switch
        {
            WorkMediaType.Movie => await video.ReconcileAsync(workId, MediaAcquisitionKind.Movie, wake: true, cancellationToken),
            WorkMediaType.Series => await video.ReconcileAsync(workId, MediaAcquisitionKind.Tv, wake: true, cancellationToken),
            _ => null
        };
    }
}
