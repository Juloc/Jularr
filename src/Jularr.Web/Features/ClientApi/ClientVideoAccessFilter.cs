using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.ClientApi;

/// <summary>
/// A media type the profile cannot browse has no API surface (<c>MediaCapability.Hidden</c>), exactly as it has no pages: a
/// route that names a canonical target (body target or <c>workId</c> query), a legacy episode or a media file answers 404
/// before its handler runs when that target does not exist or its media type is hidden. Requests that name no target, and an
/// invalid canonical target (left to the handler's 400), pass through. A legacy episode is an Anime episode by definition,
/// and a file without a canonical asset is a legacy Anime file.
/// </summary>
public sealed class ClientVideoAccessFilter(IAppShellService appShell, AppDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var cancellationToken = http.RequestAborted;
        var (named, mediaType) = await ResolveAsync(context, cancellationToken);
        if (named && (mediaType is not { } type || !(await appShell.GetMediaAccessAsync(http.User, cancellationToken)).IsVisible(type)))
        {
            return Results.NotFound(new ClientErrorResponse("video_target_not_found", "The requested video does not exist."));
        }

        return await next(context);
    }

    private async Task<(bool Named, WorkMediaType? MediaType)> ResolveAsync(EndpointFilterInvocationContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var bodyTarget = context.Arguments
            .Select(argument => argument switch
            {
                ClientVideoPlayerRequest request => request.Target,
                ClientPlaybackPlanRequest request => request.Target,
                ClientVideoProgressUpdate update => update.Target,
                _ => null
            })
            .FirstOrDefault(target => target is not null)
            ?? (http.Items[ClientPlaybackIntentBodyFilter.ItemKey] as ClientPlaybackIntentRequest)?.Target;
        var queryWorkId = Guid.TryParse(http.Request.Query["workId"], out var parsed) ? parsed : (Guid?)null;
        if (bodyTarget is not null || queryWorkId is not null)
        {
            if (bodyTarget is { IsValid: false } || queryWorkId == Guid.Empty)
            {
                return (false, null);
            }

            var workId = bodyTarget?.WorkId ?? queryWorkId!.Value;
            return (true, await db.Works.AsNoTracking().Where(x => x.Id == workId).Select(x => (WorkMediaType?)x.MediaType).SingleOrDefaultAsync(cancellationToken));
        }

        if (http.GetRouteValue("episodeId") is not null || http.GetRouteValue("animeId") is not null)
        {
            return (true, WorkMediaType.Anime);
        }

        if (!Guid.TryParse(http.GetRouteValue("mediaFileId")?.ToString(), out var mediaFileId))
        {
            return (false, null);
        }

        var file = await (
            from stored in db.StoredFiles.AsNoTracking()
            where stored.Id == mediaFileId
            join asset in db.MediaAssets.AsNoTracking() on stored.MediaAssetId equals (Guid?)asset.Id into assets
            from asset in assets.DefaultIfEmpty()
            join work in db.Works.AsNoTracking() on (Guid?)asset.WorkId equals (Guid?)work.Id into works
            from work in works.DefaultIfEmpty()
            select new { MediaType = (WorkMediaType?)work.MediaType, IsLegacy = stored.EpisodeId != null })
            .SingleOrDefaultAsync(cancellationToken);
        return (true, file is null ? null : file.MediaType ?? (file.IsLegacy ? WorkMediaType.Anime : null));
    }
}
