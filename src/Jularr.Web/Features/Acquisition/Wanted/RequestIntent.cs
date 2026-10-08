using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Web.Features.Acquisition.Wanted;

// Records what an approved request explicitly asks for, as rows the reconciler reads next to Monitoring: the Work (the whole title) or single episodes.
// It is called every time an approved request is executed and is idempotent; a pending request has no intent yet.
public sealed class RequestIntent(AppDbContext db, TimeProvider clock)
{
    public async Task RecordAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        if (request.Status == AcquisitionRequestStatus.Pending || WorkOf(request) is not { } workId)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var id = request.Id.ToString();
        if (request.Kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv)
        {
            // The choice a video request still carries decides what it asks for; a request that applied it earlier has recorded it then.
            if (VideoRequestPayload.Parse(request.PayloadJson)?.Requested is not { } choice)
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
    }

    private async Task InsertWorkAsync(string requestId, Guid workId, DateTime now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync(
            WantedSql.RecordWork,
            [new NpgsqlParameter("requestId", requestId), new NpgsqlParameter("workId", workId), new NpgsqlParameter("now", now)],
            cancellationToken);

    // The canonical Work a request is about: the bound Work, else the one its payload names (video and music payloads carry it).
    private static Guid? WorkOf(AcquisitionRequest request)
    {
        if (request.WorkId is { } bound)
        {
            return bound;
        }

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(request.PayloadJson);
            return document.RootElement.TryGetProperty("workId", out var value) && value.TryGetGuid(out var id) && id != Guid.Empty ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
