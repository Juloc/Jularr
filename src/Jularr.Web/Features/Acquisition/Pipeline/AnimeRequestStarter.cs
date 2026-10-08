using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Pipeline;

// A monitored anime is acquired through a request like every other title: it is requested by its AniList entry, with the default scope (what Monitoring says).
public sealed class AnimeRequestDrafter(AppDbContext db) : IWantedRequestDrafter
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public async Task<WantedRequestDraft?> DraftAsync(Guid workId, CancellationToken cancellationToken)
    {
        var match = await (
                from link in db.WorkSourceLinks.AsNoTracking()
                join metadata in db.AnimeMetadata.AsNoTracking() on link.SourceId equals metadata.AnimeId
                join anime in db.Anime.AsNoTracking() on link.SourceId equals anime.Id
                where link.SourceKind == WorkSourceKind.Anime && link.WorkId == workId && metadata.Provider == AniListMetadataProvider.ProviderKey
                select new { metadata.ExternalId, anime.Title })
            .FirstOrDefaultAsync(cancellationToken);
        return match is null
            ? null
            : new WantedRequestDraft(new AcquisitionRequestDraft(MediaAcquisitionKind.Anime, AniListMetadataProvider.ProviderKey, match.ExternalId, match.Title, null, null) { WorkId = workId }, "owner");
    }
}

/// <summary>
/// "Search now" and search-on-add for anime: brings the canonical episodes and the Wanted queue of the anime up to date, makes sure an open request carries
/// it, and makes that request due, so the shared Wanted pass searches it at once. It searches nothing itself.
/// </summary>
public sealed class AnimeRequestStarter(
    AnimeMonitoring monitoring,
    AnimeCanonicalEpisodes episodes,
    WantedReconciler wanted,
    AnimeRequestDrafter drafter,
    AcquisitionAccessStore requests,
    WantedPassTrigger? trigger = null)
{
    public async Task<int> StartAsync(string? animeKey, CancellationToken cancellationToken, bool makeDue = true)
    {
        var keys = animeKey is null ? await monitoring.MonitoredKeysAsync(cancellationToken) : [animeKey];
        var started = 0;
        foreach (var key in keys)
        {
            await episodes.EnsureAsync(key, cancellationToken);
            if (await episodes.WorkOfAsync(key, cancellationToken) is not { } workId)
            {
                continue;
            }

            await wanted.ReconcileAsync(workId, cancellationToken);
            if (await drafter.DraftAsync(workId, cancellationToken) is not { } draft)
            {
                continue;
            }

            if (await requests.FindOpenAsync(MediaAcquisitionKind.Anime, draft.Draft.Provider, draft.Draft.ExternalId, cancellationToken) is { } open)
            {
                if (!makeDue)
                {
                    continue;
                }

                // A request that waits for the next episode or a back-off looks again now.
                await requests.PatchPayloadAsync(open.Id, stored => AnimeRequestPayload.Parse(stored) is { } current ? (current with { NextSearchUtc = null, Searches = 0 }).Serialize() : stored, cancellationToken);
                started++;
                continue;
            }

            try
            {
                await requests.CreateAsync(draft.Draft, draft.Requester, AcquisitionRequestStatus.Approved, draft.Requester, cancellationToken);
                started++;
            }
            catch (OpenRequestExistsException)
            {
                // Somebody requested it in the same moment; that request is the one.
            }
        }

        if (started > 0)
        {
            trigger?.Request();
        }

        return started;
    }
}
