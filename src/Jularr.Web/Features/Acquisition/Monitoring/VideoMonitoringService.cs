using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
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
/// The Movie and TV side of Monitoring: every command writes ordinary decisions with <see cref="MonitoringCommands"/> and then reconciles the title's
/// request with what the Work now says (<see cref="ReconcileAsync"/>), so the Wanted pass, the detail pages and the Admin surface all read the same
/// canonical state and there is no second timer or store.
/// <list type="bullet">
/// <item>Off ends an approved request together with the wake-up, in one write (the original request, with its requester): Completed when the title
/// has local media, otherwise Rejected so the requester never sees "available" for something that was not acquired. One in flight ends at its next step.</item>
/// <item>On wakes the open request, reopens the one monitoring ended, or opens a new request through <see cref="AcquisitionRequestService"/> when the
/// title has none, so approval, capability and executor rules stay in that one path.</item>
/// </list>
/// </summary>
public sealed class VideoMonitoringService(
    AppDbContext db,
    AcquisitionAccessStore requests,
    AcquisitionRequestService requestService,
    VideoAcquisitionEngine engine,
    QualityProfileStore profiles,
    MonitoringCommands commands,
    MonitoringResolver monitoring)
{
    /// <summary>The scope value that stops all TV acquisition for a Work; the other values are the Request dialog's.</summary>
    public const string OffScope = "off";

    /// <summary>The message of a request that ended because monitoring was turned off.</summary>
    public const string MonitoringTurnedOff = "Monitoring was turned off.";

    /// <summary>The message of a waiting request after a change; it replaces a "searching again at ..." that the change made obsolete.</summary>
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
        if (!await db.Works.AsNoTracking().AnyAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Movie, cancellationToken))
        {
            return VideoMonitoringOutcome.NotFound;
        }

        await commands.SetAsync(MonitoringTargetKind.Work, workId, monitored, cancellationToken);
        return await ReconcileAsync(workId, MediaAcquisitionKind.Movie, wake: true, cancellationToken);
    }

    /// <summary>
    /// Sets what a whole Series monitors: <c>all</c> (every episode and every future one), <c>future</c> (episodes that appear from now on) or
    /// <see cref="OffScope"/>. It replaces every decision made below the Series before. Throws <see cref="ArgumentException"/> for any other scope;
    /// a custom selection is made one season or episode at a time with <see cref="SetSeasonMonitoredAsync"/> and <see cref="SetEpisodeMonitoredAsync"/>.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetSeriesAsync(Guid workId, string? scope, CancellationToken cancellationToken)
    {
        if (!await db.Works.AsNoTracking().AnyAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Series, cancellationToken))
        {
            return VideoMonitoringOutcome.NotFound;
        }

        if (scope == OffScope)
        {
            await commands.SetAsync(MonitoringTargetKind.Work, workId, false, cancellationToken);
        }
        else if (VideoRequestScopeResolver.TryParseScope(scope, out var parsed) && parsed != VideoRequestScope.Custom)
        {
            await (parsed == VideoRequestScope.FutureOnly ? commands.FutureAsync(workId, cancellationToken) : commands.SetAsync(MonitoringTargetKind.Work, workId, true, cancellationToken));
        }
        else
        {
            throw new ArgumentException("Choose all, future or off.", nameof(scope));
        }

        return await ReconcileAsync(workId, MediaAcquisitionKind.Tv, wake: true, cancellationToken);
    }

    /// <summary>
    /// Monitors or unmonitors every episode of one season and leaves the rest of the Series as it is, including the episodes that are added to
    /// that season later. Returns <see cref="VideoMonitoringOutcome.NotFound"/> for a season the Series does not have and
    /// <see cref="VideoMonitoringOutcome.Unchanged"/> when every episode of it already is as asked.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetSeasonMonitoredAsync(Guid workId, int seasonNumber, bool monitored, CancellationToken cancellationToken)
    {
        if (!await db.Works.AsNoTracking().AnyAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Series, cancellationToken))
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var episodes = await db.WorkEpisodes.AsNoTracking().Where(x => x.WorkId == workId && x.SeasonNumber == seasonNumber).Select(x => new { x.Id, x.SeasonId }).ToListAsync(cancellationToken);
        var seasonId = await db.WorkSeasons.AsNoTracking().Where(x => x.WorkId == workId && x.SeasonNumber == seasonNumber).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (seasonId is null && episodes.Count == 0)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var view = await monitoring.LoadAsync(workId, cancellationToken);
        if (episodes.All(episode => view.IsMonitored(episode.Id, episode.SeasonId) == monitored) && (seasonId is not { } season || view.DecisionOf(season) is null || view.DecisionOf(season) == monitored))
        {
            return VideoMonitoringOutcome.Unchanged;
        }

        if (seasonId is { } id)
        {
            await commands.SetAsync(MonitoringTargetKind.Season, id, monitored, cancellationToken);
        }
        else
        {
            // A season that only exists as a number on its episodes has no id to decide on, so its episodes are switched one by one in a single statement.
            await commands.SetManyAsync(MonitoringTargetKind.Episode, [.. episodes.Select(episode => episode.Id)], monitored, cancellationToken);
        }

        return await ReconcileAsync(workId, MediaAcquisitionKind.Tv, wake: true, cancellationToken);
    }

    /// <summary>
    /// Monitors or unmonitors one episode and leaves the rest of the Series as it is. An episode only carries a decision of its own when it differs from what it
    /// inherits, so switching it back to the inherited state removes the decision; a switch that changes nothing writes and wakes nothing.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetEpisodeMonitoredAsync(Guid workId, Guid episodeId, bool monitored, CancellationToken cancellationToken)
    {
        var episode = await db.WorkEpisodes.AsNoTracking().Where(x => x.Id == episodeId && x.WorkId == workId).Select(x => new { x.SeasonId }).FirstOrDefaultAsync(cancellationToken);
        if (episode is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var view = await monitoring.LoadAsync(workId, cancellationToken);
        if (view.IsMonitored(episodeId, episode.SeasonId) == monitored)
        {
            return VideoMonitoringOutcome.Unchanged;
        }

        var inherited = (episode.SeasonId is { } season ? view.DecisionOf(season) : null) ?? view.IsWorkMonitored;
        await commands.SetAsync(MonitoringTargetKind.Episode, episodeId, inherited == monitored ? null : monitored, cancellationToken);
        return await ReconcileAsync(workId, MediaAcquisitionKind.Tv, wake: true, cancellationToken);
    }

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
    /// Brings the title's request in line with the Work's monitoring: the open request is woken (<paramref name="wake"/>) or ended when nothing is monitored
    /// any more, else the request monitoring ended is reopened, else a new one is opened. The Wanted pass calls it without waking, to create the request of a
    /// Work that became monitored through a relation. Every write is conditional on the status it was decided from, so a concurrent change makes it look
    /// again instead of leaving a status and a payload that disagree.
    /// </summary>
    public async Task<VideoMonitoringOutcome> ReconcileAsync(Guid workId, MediaAcquisitionKind kind, bool wake, CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.MediaType == VideoWorkLinks.WorkType(kind), cancellationToken);
        if (work is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var seed = new VideoRequestPayload(work.Id, work.CanonicalTitle, work.Year);
        AcquisitionRequestStatus? endStatus = null;
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var on = (await monitoring.LoadAsync(workId, cancellationToken)).IsAnyMonitored;
                if (await FindOpenRequestAsync(kind, workId, cancellationToken) is { } open)
                {
                    if (!wake)
                    {
                        return VideoMonitoringOutcome.Unchanged;
                    }

                    if (open.Status == AcquisitionRequestStatus.Approved && !on)
                    {
                        endStatus ??= await engine.StatusWhenMonitoringStopsAsync(kind, workId, cancellationToken);
                    }

                    if (await requests.PatchPayloadAsync(open.Id, stored => Wake(stored, seed, on), open.Status, _ => StatusAfter(open.Status, endStatus, on), cancellationToken))
                    {
                        return VideoMonitoringOutcome.Saved;
                    }

                    continue;
                }

                if (!on)
                {
                    return VideoMonitoringOutcome.Unchanged;
                }

                if (kind == MediaAcquisitionKind.Movie && await engine.HasMovieFileAsync(workId, cancellationToken))
                {
                    return VideoMonitoringOutcome.InLibrary;
                }

                if (await FindStoppedRequestAsync(kind, workId, cancellationToken) is { } stopped)
                {
                    var reopen = AcquisitionRequestStatus.Approved;
                    if (await requests.PatchPayloadAsync(stopped.Id, stored => Reopen(stored, seed), stopped.Status, _ => new AcquisitionStatusOutcome(reopen, "Monitoring was turned on again."), cancellationToken))
                    {
                        return VideoMonitoringOutcome.Saved;
                    }

                    continue;
                }

                if ((await IdentitiesAsync(kind, workId, cancellationToken)).FirstOrDefault() is not { } identity)
                {
                    return VideoMonitoringOutcome.NotAcquirable;
                }

                var draft = new AcquisitionRequestDraft(kind, identity.Provider, identity.ExternalId, work.CanonicalTitle, null, null, seed.Serialize());
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
    /// The stored payload after a monitoring change: a new revision and a fresh search. The change is the latest decision, so a choice the request still
    /// carried to apply is dropped, and when monitoring is turned off it also beats a playback intent, because nobody keeps waiting through it.
    /// </summary>
    private static string Wake(string? stored, VideoRequestPayload seed, bool on)
    {
        var current = VideoRequestPayload.Parse(stored) ?? seed;
        var next = current with { MonitoringRevision = current.MonitoringRevision + 1, Searches = 0, NextSearchUtc = null, LastProblem = null, EndedByMonitoring = !on, Requested = null };
        return (on ? next : next.WithoutPlaybackIntent()).Serialize();
    }

    /// <summary>A request that monitoring ended starts its search over, so it does not begin in the back-off of the earlier one.</summary>
    private static string Reopen(string? stored, VideoRequestPayload seed)
    {
        var current = VideoRequestPayload.Parse(stored) ?? seed;
        return (current with
        {
            MonitoringRevision = current.MonitoringRevision + 1,
            Searches = 0,
            NextSearchUtc = null,
            LastProblem = null,
            EndedByMonitoring = false,
            Requested = null,
            TriedReleases = null,
            ActiveWorkEpisodeId = null,
            ActiveSeasonNumber = null,
            ActiveEpisodeNumber = null
        }).Serialize();
    }

    /// <summary>The status an open request gets from the change being written: an approved one ends once nothing is monitored.</summary>
    private static AcquisitionStatusOutcome StatusAfter(AcquisitionRequestStatus status, AcquisitionRequestStatus? endStatus, bool on) =>
        status != AcquisitionRequestStatus.Approved
            ? new AcquisitionStatusOutcome(status, null)
            : on
                ? new AcquisitionStatusOutcome(status, MonitoringChanged)
                : new AcquisitionStatusOutcome(endStatus ?? status, MonitoringTurnedOff);

    /// <summary>The newest request monitoring Off ended for the Work, which turning monitoring on reopens.</summary>
    private async Task<AcquisitionRequest?> FindStoppedRequestAsync(MediaAcquisitionKind kind, Guid workId, CancellationToken cancellationToken)
    {
        AcquisitionRequest? stopped = null;
        foreach (var identity in await IdentitiesAsync(kind, workId, cancellationToken))
        {
            var latest = await requests.FindLatestAsync(kind, identity.Provider, identity.ExternalId, cancellationToken);
            var ended = latest is { Status: AcquisitionRequestStatus.Completed or AcquisitionRequestStatus.Rejected } && VideoRequestPayload.Parse(latest.PayloadJson) is { EndedByMonitoring: true };
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
