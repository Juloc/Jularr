using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Monitoring;

/// <summary>What an Admin monitoring change came to.</summary>
public enum VideoMonitoringOutcome
{
    NotFound,

    /// <summary>The monitoring state was written.</summary>
    Saved,

    /// <summary>There was nothing to change (monitoring off for a title nothing is acquiring).</summary>
    Unchanged,

    /// <summary>The Movie is in the library already, so monitoring it has nothing to acquire.</summary>
    InLibrary,

    /// <summary>The title has no provider identity to request it by.</summary>
    NotAcquirable,

    /// <summary>Somebody else changed the same request at the same moment and nothing was written; try again.</summary>
    Conflict
}

/// <summary>
/// The one owner of Movie and TV monitoring. Monitoring is part of the one <see cref="VideoRequestPayload"/> of the title's request, so
/// the Wanted pass, the Library detail page and this Admin surface all read the same state and there is no second timer or store.
/// An edit is an atomic patch of the Admin-owned payload fields (<see cref="AcquisitionAccessStore.PatchPayloadAsync"/>) and always
/// wakes the request, so it takes effect on the next pass; a search that is running when it happens re-reads before it grabs and does
/// not write its stale copy back (<see cref="VideoRequestPayload.Reconcile"/>).
/// <list type="bullet">
/// <item>Off ends an approved request together with the payload change, in one write (the original request, with its requester): Completed
/// when the title has local media, otherwise Rejected so the requester never sees "available" for something that was not acquired. One in
/// flight ends at its next step.</item>
/// <item>On reopens that same request, joins the open one, or opens a new request through <see cref="AcquisitionRequestService"/>
/// when the title has none, so approval, capability and executor rules stay in that one path. A reopened request starts its search
/// over.</item>
/// </list>
/// "Future" counts from the moment of the last scope change.
/// </summary>
public sealed class VideoMonitoringService(
    AppDbContext db,
    AcquisitionAccessStore requests,
    AcquisitionRequestService requestService,
    VideoAcquisitionEngine engine,
    QualityProfileStore profiles,
    TimeProvider clock)
{
    /// <summary>The scope value that stops all TV acquisition for a Work; the other values are the Request dialog's.</summary>
    public const string OffScope = "off";

    /// <summary>The message of a request that ended because monitoring was turned off.</summary>
    public const string MonitoringTurnedOff = "Monitoring was turned off.";

    /// <summary>The message of a waiting request after an edit; it replaces a "searching again at ..." that the edit made obsolete.</summary>
    public const string MonitoringChanged = "Monitoring changed. Searching again.";

    private const int MaxAttempts = 4;

    /// <summary>The open request of the Work under any of its provider identities, or null.</summary>
    public async Task<AcquisitionRequest?> FindOpenRequestAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        foreach (var identity in await IdentitiesAsync(kind, workId, cancellationToken))
        {
            if (await requests.FindOpenAsync(kind, identity.Provider, identity.ExternalId, cancellationToken) is { } open)
            {
                return open;
            }
        }

        return null;
    }

    /// <summary>Switches monitoring of a Movie.</summary>
    public async Task<VideoMonitoringOutcome> SetMovieMonitoredAsync(Guid workId, bool monitored, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Movie, cancellationToken);
        return work is null
            ? VideoMonitoringOutcome.NotFound
            : await ApplyAsync(work, MediaAcquisitionKind.Movie, monitored, (payload, _) => payload with { Monitored = monitored }, cancellationToken);
    }

    /// <summary>
    /// Sets what a whole Series monitors: <c>all</c> (every episode and every future one), <c>future</c> (episodes that air from now on) or
    /// <see cref="OffScope"/>. It replaces every selection and exclusion made before. Throws <see cref="ArgumentException"/> for any other scope;
    /// a custom selection is made one season or episode at a time with <see cref="SetSeasonMonitoredAsync"/> and <see cref="SetEpisodeMonitoredAsync"/>.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetSeriesAsync(Guid workId, string? scope, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Series, cancellationToken);
        if (work is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        if (scope == OffScope)
        {
            return await ApplyAsync(work, MediaAcquisitionKind.Tv, on: false, (payload, _) => payload with { Monitored = false }, cancellationToken);
        }

        if (!VideoRequestScopeResolver.TryParseScope(scope, out var parsed) || parsed == VideoRequestScope.Custom)
        {
            throw new ArgumentException("Choose all, future or off.", nameof(scope));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        return await ApplyAsync(
            work,
            MediaAcquisitionKind.Tv,
            on: true,
            (payload, _) => payload with
            {
                Monitored = true,
                Scope = parsed,
                SelectedEpisodeIds = [],
                SelectedSeasonIds = [],
                ExcludedEpisodeIds = [],
                ExcludedSeasonIds = [],
                MonitorFuture = true,
                MonitorFutureFromUtc = now
            },
            cancellationToken);
    }

    /// <summary>
    /// Monitors or unmonitors every episode of one season and leaves the rest of the Series as it is, including the episodes that are added to
    /// that season later. Returns <see cref="VideoMonitoringOutcome.NotFound"/> for a season the Series does not have.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetSeasonMonitoredAsync(Guid workId, int seasonNumber, bool monitored, CancellationToken cancellationToken)
    {
        var loaded = await LoadEpisodesAsync(workId, cancellationToken);
        if (loaded is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var (work, episodes) = loaded.Value;

        var targets = episodes.Where(episode => episode.SeasonNumber == seasonNumber).ToList();
        var seasonId = targets.Count > 0 && targets.All(episode => episode.SeasonId == targets[0].SeasonId) ? targets[0].SeasonId : null;
        return targets.Count == 0 ? VideoMonitoringOutcome.NotFound : await SwitchUnitAsync(work, targets, seasonId, monitored, cancellationToken);
    }

    /// <summary>Monitors or unmonitors one episode and leaves the rest of the Series as it is.</summary>
    public async Task<VideoMonitoringOutcome> SetEpisodeMonitoredAsync(Guid workId, Guid episodeId, bool monitored, CancellationToken cancellationToken)
    {
        var loaded = await LoadEpisodesAsync(workId, cancellationToken);
        if (loaded is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var (work, episodes) = loaded.Value;

        var targets = episodes.Where(episode => episode.Id == episodeId).ToList();
        return targets.Count == 0 ? VideoMonitoringOutcome.NotFound : await SwitchUnitAsync(work, targets, wholeSeasonId: null, monitored, cancellationToken);
    }

    private async Task<(Work Work, List<VideoEpisodeRef> Episodes)?> LoadEpisodesAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Series, cancellationToken);
        if (work is null)
        {
            return null;
        }

        var episodes = await db.WorkEpisodes.AsNoTracking()
            .Where(x => x.WorkId == workId)
            .Select(x => new VideoEpisodeRef(x.Id, x.SeasonId, x.SeasonNumber, x.AiredAt))
            .ToListAsync(cancellationToken);
        return (work, episodes);
    }

    /// <summary>
    /// Applies <see cref="VideoUnitMonitoring.Switch"/> to the stored payload inside the compare-and-set write, so a concurrent edit of another
    /// unit is never overwritten. The read before it only decides whether the Series stays monitored (a switch that leaves nothing selected
    /// ends the request like monitoring Off).
    /// </summary>
    private async Task<VideoMonitoringOutcome> SwitchUnitAsync(Work work, List<VideoEpisodeRef> targets, Guid? wholeSeasonId, bool monitored, CancellationToken cancellationToken)
    {
        var request = await FindOpenRequestAsync(MediaAcquisitionKind.Tv, work.Id, cancellationToken)
            ?? await FindStoppedRequestAsync(MediaAcquisitionKind.Tv, work.Id, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var read = request is null ? Unmonitored(work) : VideoRequestPayload.Of(request, work.Id, work.CanonicalTitle, work.Year);
        var on = VideoUnitMonitoring.Switch(read, request?.CreatedAt ?? now, now, targets, wholeSeasonId, monitored).Monitored;
        return await ApplyAsync(work, MediaAcquisitionKind.Tv, on, (payload, created) => VideoUnitMonitoring.Switch(payload, created, now, targets, wholeSeasonId, monitored), cancellationToken);
    }

    private static VideoRequestPayload Unmonitored(Work work) =>
        VideoRequestPayload.Default(MediaAcquisitionKind.Tv, work.Id, work.CanonicalTitle, work.Year) with { Monitored = false };

    /// <summary>Assigns the quality profile of one Work, or clears the override with a blank id.</summary>
    public async Task<VideoMonitoringOutcome> SetProfileAsync(MediaAcquisitionKind kind, Guid workId, string? profileId, CancellationToken cancellationToken)
    {
        var type = VideoWorkLinks.WorkType(kind);
        if (!await db.Works.AsNoTracking().AnyAsync(x => x.Id == workId && x.MediaType == type, cancellationToken))
        {
            return VideoMonitoringOutcome.NotFound;
        }

        await profiles.AssignWorkAsync(workId, profileId, cancellationToken);
        return VideoMonitoringOutcome.Saved;
    }

    /// <summary>
    /// Writes <paramref name="edit"/> to the title's request: the open one, else the one monitoring Off ended (reopened with its requester),
    /// else a new request. Every write is conditional on the status it was decided from, so a concurrent change makes it look again instead of
    /// leaving a status and a payload that disagree. Off with nothing open changes nothing.
    /// </summary>
    private async Task<VideoMonitoringOutcome> ApplyAsync(Work work, MediaAcquisitionKind kind, bool on, Func<VideoRequestPayload, DateTime, VideoRequestPayload> edit, CancellationToken cancellationToken)
    {
        var seed = VideoRequestPayload.Default(kind, work.Id, work.CanonicalTitle, work.Year);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var open = await FindOpenRequestAsync(kind, work.Id, cancellationToken);
                if (open is not null)
                {
                    var ends = !on && open.Status == AcquisitionRequestStatus.Approved;
                    var endStatus = ends ? await engine.StatusWhenMonitoringStopsAsync(kind, work.Id, cancellationToken) : open.Status;
                    var message = ends ? MonitoringTurnedOff : open.Status == AcquisitionRequestStatus.Approved ? MonitoringChanged : null;
                    if (await requests.PatchPayloadAsync(open.Id, stored => Edited(stored, seed, edit, open.CreatedAt), open.Status, endStatus, message, cancellationToken))
                    {
                        return VideoMonitoringOutcome.Saved;
                    }

                    continue;
                }

                if (!on)
                {
                    return VideoMonitoringOutcome.Unchanged;
                }

                if (kind == MediaAcquisitionKind.Movie && await engine.HasMovieFileAsync(work.Id, cancellationToken))
                {
                    return VideoMonitoringOutcome.InLibrary;
                }

                if (await FindStoppedRequestAsync(kind, work.Id, cancellationToken) is { } stopped)
                {
                    if (await requests.PatchPayloadAsync(stopped.Id, stored => Edited(stored, seed, edit, stopped.CreatedAt), stopped.Status, AcquisitionRequestStatus.Approved, "Monitoring was turned on again.", cancellationToken))
                    {
                        return VideoMonitoringOutcome.Saved;
                    }

                    continue;
                }

                if ((await IdentitiesAsync(kind, work.Id, cancellationToken)).FirstOrDefault() is not { } identity)
                {
                    return VideoMonitoringOutcome.NotAcquirable;
                }

                var draft = new AcquisitionRequestDraft(kind, identity.Provider, identity.ExternalId, work.CanonicalTitle, null, null, edit(seed with { Monitored = false }, clock.GetUtcNow().UtcDateTime).Serialize());
                if (!(await requestService.SubmitWithOutcomeAsync(draft, cancellationToken)).AlreadyRequested)
                {
                    return VideoMonitoringOutcome.Saved;
                }
            }
        }
        catch (PayloadConflictException)
        {
            return VideoMonitoringOutcome.Conflict;
        }

        return VideoMonitoringOutcome.Conflict;
    }

    /// <summary>
    /// The stored payload after an Admin change: the edit itself, a new revision and a fresh search. Turning monitoring on after it was off
    /// also forgets what the earlier search tried, so a reopened request does not start in a back-off.
    /// </summary>
    private static string Edited(string? stored, VideoRequestPayload seed, Func<VideoRequestPayload, DateTime, VideoRequestPayload> edit, DateTime requestCreatedAt)
    {
        var current = VideoRequestPayload.Parse(stored) ?? seed;
        var next = edit(current, requestCreatedAt) with { ScopeRevision = current.ScopeRevision + 1, Searches = 0, NextSearchUtc = null, LastProblem = null };
        return (!current.Monitored && next.Monitored
            ? next with { TriedReleases = null, ActiveWorkEpisodeId = null, ActiveSeasonNumber = null, ActiveEpisodeNumber = null }
            : next).Serialize();
    }

    /// <summary>The newest request monitoring Off ended for the Work, which turning monitoring on reopens.</summary>
    private async Task<AcquisitionRequest?> FindStoppedRequestAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        AcquisitionRequest? stopped = null;
        foreach (var identity in await IdentitiesAsync(kind, workId, cancellationToken))
        {
            var latest = await requests.FindLatestAsync(kind, identity.Provider, identity.ExternalId, cancellationToken);
            var ended = latest is { Status: AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected } && VideoRequestPayload.Parse(latest.PayloadJson) is { Monitored: false };
            if (ended && (stopped is null || latest!.CreatedAt > stopped.CreatedAt))
            {
                stopped = latest;
            }
        }

        return stopped;
    }

    private async Task<List<WorkIdentity>> IdentitiesAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        var type = VideoWorkLinks.WorkType(kind);
        return await db.WorkExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.WorkId == workId && identity.MediaType == type)
            .OrderByDescending(identity => identity.IsPrimary)
            .Select(identity => new WorkIdentity(identity.Provider, identity.ExternalId))
            .ToListAsync(cancellationToken);
    }

    private sealed record WorkIdentity(string Provider, string ExternalId);
}
