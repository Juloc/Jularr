using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

/// <summary>
/// One-time move of the acquisitions the old Anime pipeline had in flight onto the request lifecycle: the download of an unfinished acquisition becomes the download of the
/// anime's request (its Operation, episodes, ownership job and tried releases carry over), so the Wanted pass follows and imports it. Acquisitions that are finished are dropped;
/// one that cannot be put on a request (no AniList match, another download already runs for the request) is dropped too; its files stay where the download client put them.
/// Nothing is downloaded or renamed again. Safe to run again: a converted download is recognised by its Operation.
/// </summary>
public sealed class AnimeLegacyAcquisitionMigration(
    SabnzbdAcquisitionStore store,
    AppDbContext db,
    AnimeRequestStarter starter,
    AcquisitionAccessStore requests,
    AcquisitionOwnershipStore ownership,
    AnimeImportStore imports,
    ILogger<AnimeLegacyAcquisitionMigration> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var acquisitions = (await store.LoadAsync(cancellationToken)).Acquisitions;
        if (acquisitions.Count == 0)
        {
            return 0;
        }

        var operations = new OperationStore(db);
        var importState = await imports.LoadAsync(cancellationToken);
        var converted = 0;
        foreach (var acquisition in acquisitions)
        {
            var download = acquisition.LatestAttempt is { } latest ? await operations.GetAsync(latest.OperationId, cancellationToken) : null;
            var unfinished = download is not null
                && (download.IsActive || (download.Status == OperationStatus.Succeeded && !importState.Imports.Any(record => record.DownloadOperationId == download.Id && record.Status is AnimeImportStatus.Imported or AnimeImportStatus.Dismissed)));
            if (unfinished && await ConvertAsync(acquisition, download!, cancellationToken))
            {
                converted++;
            }

            await store.UpdateAsync(state => state.Acquisitions.RemoveAll(item => item.Id == acquisition.Id), cancellationToken);
        }

        return converted;
    }

    private async Task<bool> ConvertAsync(SabnzbdAcquisition acquisition, OperationSnapshot download, CancellationToken cancellationToken)
    {
        if (await starter.EnsureRequestAsync(acquisition.AnimeKey, cancellationToken) is not var (request, _))
        {
            logger.LogWarning("The download of {Anime} has no AniList match to request it by; its files stay where the download client put them.", acquisition.AnimeKey);
            return false;
        }

        if (request.OperationId != download.Id)
        {
            if (request.Status is AcquisitionRequestStatus.Searching or AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing)
            {
                logger.LogWarning("The request of {Anime} already follows another download; the earlier one's files stay where the download client put them.", acquisition.AnimeKey);
                return false;
            }

            var latest = acquisition.LatestAttempt!;
            var payload = AnimeRequestPayload.Of(request) with
            {
                AnimeKey = acquisition.AnimeKey,
                Episodes = acquisition.Episodes,
                ReleaseIdentity = latest.ReleaseIdentity,
                ReleaseTitle = latest.ReleaseTitle,
                TriedReleases = [.. acquisition.Attempts.Select(attempt => attempt.ReleaseIdentity)],
                Searches = Math.Max(1, acquisition.Attempts.Length),
                NextSearchUtc = null,
                LastProblem = null
            };
            var animeId = await db.Anime.AsNoTracking().Where(anime => anime.Key == acquisition.AnimeKey).Select(anime => (Guid?)anime.Id).FirstOrDefaultAsync(cancellationToken);
            await requests.PatchPayloadAsync(request.Id, _ => payload.Serialize(), cancellationToken);
            await requests.UpdateStatusAsync(
                request.Id,
                download.IsActive ? AcquisitionRequestStatus.Downloading : AcquisitionRequestStatus.Importing,
                download.IsActive ? "Download is in progress." : "Download complete. Importing into the library.",
                download.Id,
                animeId is { } id ? $"/Library/Anime/{id}" : null,
                null,
                cancellationToken);
        }

        var legacyJob = acquisition.Id.ToString();
        await ownership.UpdateAsync(
            state =>
            {
                if (!state.Jobs.TryGetValue(legacyJob, out var job))
                {
                    return state;
                }

                var jobs = new Dictionary<string, AcquisitionOwnership>(state.Jobs, StringComparer.OrdinalIgnoreCase);
                jobs.Remove(legacyJob);
                jobs[download.Id.ToString()] = job with { JobId = download.Id.ToString() };
                return state with { Jobs = jobs };
            },
            cancellationToken);
        return true;
    }
}
