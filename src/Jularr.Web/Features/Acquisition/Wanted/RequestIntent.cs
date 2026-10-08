using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Features.Acquisition.Wanted;

// Records what an approved request explicitly asks for, as rows the reconciler reads next to Monitoring: the Work (the whole title) or single episodes.
// Only a request somebody made (submitted or approved) records intent; one the Wanted pass or Monitoring opened for a Work stays derived from Monitoring.
// It is idempotent and a pending request has no intent yet.
public sealed class RequestIntent(AppDbContext db, TimeProvider clock)
{
    // Creates or approves a request and records what it names as one unit, so an approved request never exists without its intent. A change that
    // returns null (the request was moved on meanwhile) records nothing; bind gives a request its canonical Work first.
    public async Task<AcquisitionRequest?> ChangeAndRecordAsync(Func<Task<AcquisitionRequest?>> change, Func<AcquisitionRequest, Task<AcquisitionRequest>>? bind, CancellationToken cancellationToken)
    {
        AcquisitionRequest? changed = null;
        await db.Database.InTransactionAsync(
            async () =>
            {
                changed = await change();
                if (changed is not null)
                {
                    await RecordAsync(bind is null ? changed : await bind(changed), cancellationToken);
                }
            },
            cancellationToken);
        return changed;
    }

    public async Task RecordAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.Status == AcquisitionRequestStatus.Pending || await WorkOfAsync(request, cancellationToken) is not { } workId)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var id = request.Id.ToString();
        if (request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
        {
            // A request without a readable payload is a plain request for the title (the executor's default choice); one Monitoring opened carries a
            // payload without a choice and asks for nothing of its own.
            var payload = VideoRequestPayload.Parse(request.PayloadJson);
            if ((payload is null ? VideoRequestPayload.Default(workId, request.Title, null).Requested : payload.Requested) is not { } choice)
            {
                return;
            }

            if (choice.Scope is VideoRequestScope.WholeWork or VideoRequestScope.AllCurrentAndFuture)
            {
                await InsertWorkAsync(id, workId, now, cancellationToken);
            }
            else if (choice.Scope == VideoRequestScope.Custom)
            {
                await db.Database.ExecuteSqlRawAsync(
                    WantedSql.RecordEpisodes,
                    [
                        new NpgsqlParameter("requestId", id),
                        new NpgsqlParameter("workId", workId),
                        new NpgsqlParameter("episodeIds", choice.EpisodeIds.ToArray()),
                        new NpgsqlParameter("seasonIds", choice.SeasonIds.ToArray()),
                        new NpgsqlParameter("now", now)
                    ],
                    cancellationToken);
            }

            return;
        }

        if (request.Kind is MediaAcquisitionKind.Book or MediaAcquisitionKind.LightNovel or MediaAcquisitionKind.Manga or MediaAcquisitionKind.Music)
        {
            await InsertWorkAsync(id, workId, now, cancellationToken);
        }
        else if (request.Kind == MediaAcquisitionKind.Audiobook)
        {
            // The audio edition of the Book Work is a canonical edition of its own, made now so it is wanted before any file exists.
            var editionId = await AudiobookEditions.EnsureAsync(db, workId, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                WantedSql.RecordEdition,
                [new NpgsqlParameter("requestId", id), new NpgsqlParameter("editionId", editionId), new NpgsqlParameter("now", now)],
                cancellationToken);
        }
    }

    // Whether the request names anything itself, which keeps it alive while Monitoring is off.
    public async Task<bool> HasAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<bool>(WantedSql.HasRequestTargets, new NpgsqlParameter("requestId", requestId.ToString())).SingleAsync(cancellationToken);

    // The Work row says the whole title.
    private async Task InsertWorkAsync(string requestId, Guid workId, DateTime now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(
            WantedSql.RecordWork,
            [new NpgsqlParameter("requestId", requestId), new NpgsqlParameter("workId", workId), new NpgsqlParameter("now", now)],
            cancellationToken);

    // The canonical Work a request is about: the bound Work, else the one its payload names (video and music payloads carry it), else the one its
    // provider identity points to (a video request made without a payload).
    private async Task<Guid?> WorkOfAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkId is { } bound)
        {
            return bound;
        }

        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(request.PayloadJson);
                if (document.RootElement.TryGetProperty("workId", out var value) && value.TryGetGuid(out var id) && id != Guid.Empty)
                {
                    return id;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (request.Kind is not (MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv))
        {
            return null;
        }

        var type = VideoWorkLinks.WorkType(request.Kind);
        return await db.WorkExternalIdentities.AsNoTracking()
            .Where(identity => identity.Provider == request.Provider && identity.ExternalId == request.ExternalId && identity.MediaType == type)
            .Select(identity => (Guid?)identity.WorkId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
