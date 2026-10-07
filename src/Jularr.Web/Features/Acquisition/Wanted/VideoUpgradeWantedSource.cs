using System.Collections.Concurrent;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>When each media type's upgrade scan last ran in this process; a scan is cheap to repeat after a restart, so nothing is persisted.</summary>
public sealed class UpgradeScanState
{
    /// <summary>How often the installed titles of a media type are looked at for upgrades; an idle library costs one query per interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<MediaAcquisitionKind, DateTime> next = new();

    /// <summary>Claims the scan of <paramref name="kind"/> when it is due and schedules the next one.</summary>
    public bool TryStart(MediaAcquisitionKind kind, DateTime nowUtc, TimeSpan interval)
    {
        if (next.TryGetValue(kind, out var due) && due > nowUtc)
        {
            return false;
        }

        next[kind] = nowUtc + interval;
        return true;
    }
}

/// <summary>
/// The Movie and TV part of the shared Wanted pass for upgrades: a monitored title whose request is Completed but whose installed quality is
/// below what the current profile wants (a profile or cutoff changed after the import) becomes Wanted again, so the same request lifecycle
/// searches, grabs and imports the better release. It only reopens requests; it reads the installed quality from the canonical Version and
/// the policy from <see cref="UpgradePolicy"/>, and stores nothing of its own. It looks at acquired titles only (a Version with a recorded
/// quality) and at most once per <see cref="UpgradeScanState.Interval"/>, so an idle library costs one query an hour.
/// </summary>
public sealed class VideoUpgradeWantedSource(
    MediaAcquisitionKind kind,
    AppDbContext db,
    AcquisitionAccessStore requests,
    InstalledVideoVersions installed,
    QualityProfileStore profiles,
    UpgradeScanState scans) : IWantedSource
{
    public const int MaxTitlesPerPass = 200;

    private static readonly string[] RequestProviders = [ProviderKeys.Tmdb, ProviderKeys.Tvdb, ProviderKeys.Imdb];

    public MediaAcquisitionKind Kind => kind;

    public async Task<int> PrepareAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!scans.TryStart(kind, nowUtc, UpgradeScanState.Interval))
        {
            return 0;
        }

        var mediaType = VideoWorkLinks.WorkType(kind);
        var workIds = await (
                from asset in db.MediaAssets.AsNoTracking()
                join version in db.WorkVersions.AsNoTracking() on asset.WorkVersionId equals version.Id
                join work in db.Works.AsNoTracking() on asset.WorkId equals work.Id
                where asset.Kind == MediaAssetKind.Video && work.MediaType == mediaType && version.Quality != null && db.StoredFiles.Any(file => file.MediaAssetId == asset.Id)
                select asset.WorkId)
            .Distinct()
            .Take(MaxTitlesPerPass)
            .ToListAsync(cancellationToken);

        var reopened = 0;
        foreach (var workId in workIds)
        {
            if (await IsUpgradableAsync(workId, cancellationToken) && await ReopenAsync(workId, cancellationToken))
            {
                reopened++;
            }
        }

        return reopened;
    }

    private async Task<bool> IsUpgradableAsync(Guid workId, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(kind, workId, cancellationToken);
        return kind == MediaAcquisitionKind.Movie
            ? UpgradePolicy.Assess(profile, await installed.BestMovieQualityAsync(workId, profile, cancellationToken)).IsUpgradable
            : (await installed.BestQualityByEpisodeAsync(kind, workId, profile, cancellationToken)).Values.Any(quality => UpgradePolicy.Assess(profile, quality).IsUpgradable);
    }

    private async Task<bool> ReopenAsync(Guid workId, CancellationToken cancellationToken)
    {
        var identities = await db.WorkExternalIdentities.AsNoTracking()
            .Where(identity => identity.WorkId == workId && RequestProviders.Contains(identity.Provider))
            .Select(identity => new { identity.Provider, identity.ExternalId })
            .ToListAsync(cancellationToken);
        foreach (var identity in identities)
        {
            if (await requests.FindLatestAsync(kind, identity.Provider, identity.ExternalId, cancellationToken) is not { Status: AcquisitionRequestStatus.Completed } request
                || VideoRequestPayload.Parse(request.PayloadJson) is not { Monitored: true } payload)
            {
                continue;
            }

            await requests.PatchPayloadAsync(request.Id, stored => VideoRequestPayload.Parse(stored) is { } current ? (current with { Searches = 0, NextSearchUtc = null, LastProblem = null }).Serialize() : stored, cancellationToken);
            var transition = await requests.TryTransitionStatusAsync(
                request.Id,
                [AcquisitionRequestStatus.Completed],
                AcquisitionRequestStatus.Approved,
                "A better release is wanted for the installed quality. Searching.",
                operationId: null,
                resultUrl: VideoWorkLinks.DetailPath(kind, payload.WorkId),
                clearOperation: true,
                cancellationToken);
            return transition is not null;
        }

        return false;
    }
}
