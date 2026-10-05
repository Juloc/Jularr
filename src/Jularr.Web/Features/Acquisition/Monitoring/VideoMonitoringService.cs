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
    VideoRequestScopeResolver scopes,
    QualityProfileStore profiles,
    TimeProvider clock)
{
    /// <summary>The scope value that stops all TV acquisition for a Work; the other values are the Request dialog's.</summary>
    public const string OffScope = "off";

    /// <summary>The message of a request that ended because monitoring was turned off.</summary>
    public const string MonitoringTurnedOff = "Monitoring was turned off.";

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
            : await ApplyAsync(work, MediaAcquisitionKind.Movie, monitored, payload => payload with { Monitored = monitored }, cancellationToken);
    }

    /// <summary>
    /// Sets what is monitored of a Series: <c>all</c>, <c>future</c>, <c>custom</c> (the given seasons, episodes and, with
    /// <paramref name="monitorFuture"/>, later episodes) or <see cref="OffScope"/>. A custom choice that selects nothing is off. Episodes
    /// the choice would include but the checklist left out are excluded explicitly. Throws <see cref="ArgumentException"/> for an unknown
    /// scope or a season or episode that does not belong to the Series.
    /// </summary>
    public async Task<VideoMonitoringOutcome> SetSeriesAsync(
        Guid workId,
        string? scope,
        IReadOnlyCollection<Guid> seasonIds,
        IReadOnlyCollection<Guid> episodeIds,
        bool monitorFuture,
        CancellationToken cancellationToken)
    {
        var work = await db.Works.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId && x.MediaType == WorkMediaType.Series, cancellationToken);
        if (work is null)
        {
            return VideoMonitoringOutcome.NotFound;
        }

        var off = scope == OffScope || scope == "custom" && seasonIds.Count == 0 && episodeIds.Count == 0 && !monitorFuture;
        if (off)
        {
            return await ApplyAsync(work, MediaAcquisitionKind.Tv, on: false, payload => payload with { Monitored = false }, cancellationToken);
        }

        if (!VideoRequestScopeResolver.TryParseScope(scope, out var parsed))
        {
            throw new ArgumentException("Choose all, future, custom or off.", nameof(scope));
        }

        var chosen = await scopes.BuildTvPayloadAsync(work, new VideoRequestScopeChoice(parsed, seasonIds, episodeIds, monitorFuture), cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        chosen = chosen with { MonitorFutureFromUtc = now };
        if (parsed == VideoRequestScope.Custom)
        {
            var checkedEpisodes = episodeIds.ToHashSet();
            var selection = new VideoRequestSelection(chosen, now);
            var episodes = await db.WorkEpisodes.AsNoTracking().Where(x => x.WorkId == workId).Select(x => new { x.Id, x.SeasonId, x.AiredAt }).ToListAsync(cancellationToken);
            chosen = chosen with { ExcludedEpisodeIds = [.. episodes.Where(x => selection.Includes(x.Id, x.SeasonId, x.AiredAt) && !checkedEpisodes.Contains(x.Id)).Select(x => x.Id)] };
        }

        return await ApplyAsync(work, MediaAcquisitionKind.Tv, on: true, payload => ScopeOf(payload, chosen), cancellationToken);
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

    private static VideoRequestPayload ScopeOf(VideoRequestPayload payload, VideoRequestPayload chosen) => payload with
    {
        Monitored = true,
        Scope = chosen.Scope,
        SelectedEpisodeIds = chosen.SelectedEpisodeIds,
        SelectedSeasonIds = chosen.SelectedSeasonIds,
        MonitorFuture = chosen.MonitorFuture,
        MonitorFutureFromUtc = chosen.MonitorFutureFromUtc,
        ExcludedEpisodeIds = chosen.ExcludedEpisodeIds
    };

    /// <summary>
    /// Writes <paramref name="edit"/> to the title's request: the open one, else the one monitoring Off ended (reopened with its requester),
    /// else a new request. Every write is conditional on the status it was decided from, so a concurrent change makes it look again instead of
    /// leaving a status and a payload that disagree. Off with nothing open changes nothing.
    /// </summary>
    private async Task<VideoMonitoringOutcome> ApplyAsync(Work work, MediaAcquisitionKind kind, bool on, Func<VideoRequestPayload, VideoRequestPayload> edit, CancellationToken cancellationToken)
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
                    if (await requests.PatchPayloadAsync(open.Id, stored => Edited(stored, seed, edit), open.Status, endStatus, ends ? MonitoringTurnedOff : null, cancellationToken))
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
                    if (await requests.PatchPayloadAsync(stopped.Id, stored => Edited(stored, seed, edit), stopped.Status, AcquisitionRequestStatus.Approved, "Monitoring was turned on again.", cancellationToken))
                    {
                        return VideoMonitoringOutcome.Saved;
                    }

                    continue;
                }

                if ((await IdentitiesAsync(kind, work.Id, cancellationToken)).FirstOrDefault() is not { } identity)
                {
                    return VideoMonitoringOutcome.NotAcquirable;
                }

                var draft = new AcquisitionRequestDraft(kind, identity.Provider, identity.ExternalId, work.CanonicalTitle, null, null, edit(seed).Serialize());
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
    private static string Edited(string? stored, VideoRequestPayload seed, Func<VideoRequestPayload, VideoRequestPayload> edit)
    {
        var current = VideoRequestPayload.Parse(stored) ?? seed;
        var next = edit(current) with { ScopeRevision = current.ScopeRevision + 1, Searches = 0, NextSearchUtc = null, LastProblem = null };
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
