using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Music;

/// <summary>One release as the Music Manual Search lists it: every reason behind the verdict, how it was found and where it came from. It carries no download address.</summary>
public sealed record MusicManualCandidate(
    string Identity,
    string Title,
    string? Indexer,
    long? SizeBytes,
    int? AgeDays,
    string? Quality,
    ManualSearchVerdict Verdict,
    int? Score,
    IReadOnlyList<SelectionReason> Reasons,
    IReadOnlyList<string> ScoreBreakdown,
    bool IsTried,
    bool CanGrab,
    IReadOnlyList<QueryProvenance> Provenance,
    IReadOnlyList<string> Sources);

public sealed record MusicManualSearchResult(IReadOnlyList<MusicManualCandidate> Candidates, ManualSearchSummary Summary, string? WinnerReason);

/// <summary>
/// Admin Manual Search for one album. Candidates come from the same search, selection engine and grab path as automatic acquisition
/// (<see cref="MusicAcquisitionEngine"/>); the browser only sends an opaque release identity and every grab searches again past the evidence
/// cache and re-validates it. A grab is recorded on the album's request like an automatic one, claimed with one conditional status write so a
/// request the scheduler grabbed for meanwhile is never grabbed twice.
/// </summary>
public sealed class MusicManualSearchService(
    AppDbContext db,
    MusicAcquisitionEngine engine,
    AcquisitionAccessStore requests,
    AcquisitionRequestService requestService,
    ManualGrabCoordinator coordinator,
    QualityProfileStore profiles,
    TimeProvider clock)
{
    public async Task<MusicManualSearchResult?> SearchAsync(Guid workId, bool refresh, SearchDepth depth, CancellationToken cancellationToken)
    {
        if (await LoadAsync(workId, cancellationToken) is not { } album)
        {
            return null;
        }

        var open = await requests.FindOpenAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, cancellationToken);
        var payload = open is null ? album.Payload : MusicRequestPayload.Of(open) with { WorkId = workId };
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Music, workId, cancellationToken);
        var wantedSince = open?.CreatedAt ?? clock.GetUtcNow().UtcDateTime;
        var evaluation = await engine.SearchAsync(wantedSince, payload, profile, new SearchOptions { Purpose = SearchPurpose.Interactive, Depth = depth, Refresh = refresh }, cancellationToken);
        var tried = new HashSet<string>(payload.TriedReleases ?? [], StringComparer.OrdinalIgnoreCase);
        var candidates = evaluation.Releases.Select(release => ToCandidate(release, tried)).ToArray();
        return new MusicManualSearchResult(
            candidates,
            new ManualSearchSummary(depth, evaluation.Search.RawResultCount, evaluation.Search.Releases.Count, evaluation.Search.Outcomes, evaluation.Search.Trace),
            evaluation.Selection.WinnerReason);
    }

    /// <summary>
    /// "Search now": makes sure the album has a request that waits for a release and runs it through the shared executor right away. An album that
    /// gave up is searched again from the start; one that is downloading or imported is left as it is. Returns the message of the request.
    /// </summary>
    public async Task<string?> SearchNowAsync(Guid workId, string requestedByProfileId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(workId, cancellationToken) is not { } album)
        {
            return null;
        }

        var open = await requests.FindOpenAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, cancellationToken);
        if (open is null)
        {
            var latest = await requests.FindLatestAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, cancellationToken);
            if (latest is { Status: AcquisitionRequestStatus.Failed })
            {
                return (await requestService.ApproveAsync(latest.Id, cancellationToken)).StatusMessage;
            }

            var created = await requests.CreateAsync(album.Draft, requestedByProfileId, AcquisitionRequestStatus.Approved, requestedByProfileId, cancellationToken);
            return (await requestService.ContinueAsync(created.Id, cancellationToken)).StatusMessage;
        }

        return open.Status == AcquisitionRequestStatus.Approved
            ? (await requestService.ContinueAsync(open.Id, cancellationToken)).StatusMessage
            : open.StatusMessage;
    }

    public async Task<ManualGrabOutcome> GrabAsync(Guid workId, string requestedByProfileId, string releaseIdentity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseIdentity);
        if (await LoadAsync(workId, cancellationToken) is not { } album)
        {
            return new ManualGrabOutcome(ManualGrabStatus.NotFound, null, null);
        }

        var request = await requests.FindOpenAsync(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, album.GroupId, cancellationToken)
            ?? await requests.CreateAsync(album.Draft, requestedByProfileId, AcquisitionRequestStatus.Approved, requestedByProfileId, cancellationToken);
        var payload = MusicRequestPayload.Of(request) with { WorkId = workId };
        if ((payload.TriedReleases ?? []).Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase))
        {
            return new ManualGrabOutcome(ManualGrabStatus.AlreadySubmitted, null, null);
        }

        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Music, workId, cancellationToken);
        var evaluation = await engine.SearchAsync(request.CreatedAt, payload, profile, new SearchOptions { Purpose = SearchPurpose.Interactive, Refresh = true }, cancellationToken);
        var selected = evaluation.Releases.FirstOrDefault(release => release.Candidate.Identity.Equals(releaseIdentity, StringComparison.Ordinal));
        if (selected is null || !selected.IsManuallyGrabbable)
        {
            return new ManualGrabOutcome(ManualGrabStatus.NotAvailable, null, null);
        }

        return await coordinator.GrabAsync(
            request,
            [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Failed, AcquisitionRequestStatus.Pending],
            async (claimed, progress) =>
            {
                var fresh = MusicRequestPayload.Of(claimed) with { WorkId = workId };
                return (fresh.TriedReleases ?? []).Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : await engine.GrabAsync(claimed, fresh, [selected], "The selected release is no longer available.", cancellationToken, progress);
            },
            cancellationToken);
    }

    private async Task<AlbumRef?> LoadAsync(Guid workId, CancellationToken cancellationToken)
    {
        var row = await (
                from album in db.MusicAlbums.AsNoTracking()
                join artist in db.MusicArtists.AsNoTracking() on album.ArtistId equals artist.Id
                join work in db.Works.AsNoTracking() on album.WorkId equals work.Id
                where album.WorkId == workId && album.MusicBrainzReleaseGroupId != null
                select new { GroupId = album.MusicBrainzReleaseGroupId!, work.CanonicalTitle, work.Year, artist.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var payload = new MusicRequestPayload(workId, row.Name, row.CanonicalTitle, row.Year);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, row.GroupId, row.CanonicalTitle, row.Name, null, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
        return new AlbumRef(row.GroupId, payload, draft);
    }

    private static MusicManualCandidate ToCandidate(MusicReleaseEvaluation evaluation, HashSet<string> tried)
    {
        var candidate = evaluation.Candidate;
        var selection = evaluation.Selection;
        var isTried = tried.Contains(candidate.Identity);
        var lower = evaluation.IsGrabbable && selection.Decision == SelectionDecision.Temporary;
        var verdict = !evaluation.IsManuallyGrabbable
            ? ManualSearchVerdict.Rejected
            : lower || selection.Decision == SelectionDecision.ManualReview ? ManualSearchVerdict.Warning : ManualSearchVerdict.Eligible;
        return new MusicManualCandidate(
            candidate.Identity,
            candidate.Title,
            candidate.Indexer,
            candidate.SizeBytes,
            candidate.AgeDays,
            selection.Score?.QualityKey,
            verdict,
            selection.Score?.Score,
            selection.Reasons,
            selection.Score?.ScoreReasons ?? [],
            isTried,
            evaluation.IsManuallyGrabbable && !isTried,
            candidate.Provenance,
            [.. candidate.Sources.Select(source => source.Indexer)]);
    }

    private sealed record AlbumRef(string GroupId, MusicRequestPayload Payload, AcquisitionRequestDraft Draft);
}
