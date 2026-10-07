using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Wanted;

namespace Jularr.Web.Features.ReadingAcquisition;

/// <summary>One release as the Manga and Light Novel Manual Search lists it. It carries no download address: a grab sends only the identity back.</summary>
public sealed record ReadingManualCandidate(
    string Identity,
    string Title,
    IReadOnlyList<string> Sources,
    long? SizeBytes,
    int? AgeDays,
    string Format,
    string? Language,
    int? Volume,
    string? Chapters,
    bool IsBatch,
    IdentityConfidence IdentityConfidence,
    ManualSearchVerdict Verdict,
    int? Score,
    IReadOnlyList<SelectionReason> Reasons,
    string? RejectedBecause,
    bool IsTried,
    bool CanGrab,
    IReadOnlyList<QueryProvenance> Provenance);

/// <summary>The request a Manual Search is for, with what the page needs to say whether searching is possible.</summary>
public sealed record ReadingManualSearchTarget(
    Guid RequestId,
    MediaAcquisitionKind Kind,
    string Title,
    string? Author,
    int? Volume,
    string ProfileName,
    AcquisitionRequestStatus Status,
    string? StatusMessage,
    int Searches,
    DateTime? NextSearchUtc,
    IReadOnlyList<string> TriedReleases)
{
    /// <summary>Only a request that waits for a release can be searched and grabbed for; a running or finished one is read-only context.</summary>
    public bool CanSearch => Status is AcquisitionRequestStatus.Approved or AcquisitionRequestStatus.Failed;
}

public sealed record ReadingManualSearchResult(ReadingManualSearchTarget Target, IReadOnlyList<ReadingManualCandidate> Candidates, ManualSearchSummary Summary, string? WinnerReason);

/// <summary>
/// Admin Manual Search for one Manga or Light Novel request. Candidates come from the same planner, selection engine and submission path as
/// automatic acquisition (<see cref="ReadingAcquisitionEngine"/>); the browser only sends an opaque release identity and every grab searches again
/// past the evidence cache and re-validates it, so a release the profile or the identity gate rejects can never be taken. A grab is recorded on the
/// request like an automatic one (<see cref="ManualGrabCoordinator"/>).
/// </summary>
public sealed class ReadingManualSearchService(
    AcquisitionAccessStore requests,
    IndexerSearchCoordinator indexers,
    ReadingAcquisitionEngine engine,
    ManualGrabCoordinator coordinator,
    QualityProfileStore profiles,
    TimeProvider clock,
    ReleaseReliabilityService? reliability = null)
{
    public async Task<ReadingManualSearchTarget?> GetTargetAsync(Guid requestId, CancellationToken cancellationToken) =>
        await requests.GetAsync(requestId, cancellationToken) is { } request && IsUsenetSearchable(request)
            ? await TargetOfAsync(request, cancellationToken)
            : null;

    public async Task<ReadingManualSearchResult?> SearchAsync(Guid requestId, bool refresh, SearchDepth depth, CancellationToken cancellationToken)
    {
        if (await requests.GetAsync(requestId, cancellationToken) is not { } request || !IsUsenetSearchable(request))
        {
            return null;
        }

        var payload = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request));
        var profile = await profiles.ResolveAsync(request.Kind, workId: null, cancellationToken);
        var lookup = reliability is null ? null : await reliability.LoadAsync(cancellationToken);
        var options = new SearchOptions { Purpose = SearchPurpose.Interactive, Depth = depth, Refresh = refresh };
        var search = await ReadingUsenetSearch.SearchAsync(indexers, ReadingAcquisitionEngine.ToTarget(request.Kind, payload), cancellationToken, options, profile, lookup, SelectionContext.SinceCreated(request.CreatedAt));
        var target = await TargetOfAsync(request, cancellationToken);
        var tried = new HashSet<string>(payload.TriedReleases ?? [], StringComparer.OrdinalIgnoreCase);
        var candidates = search.Ranked.Select(ranked => ToCandidate(ranked, tried, target.CanSearch)).ToArray();
        var summary = new ManualSearchSummary(depth, search.Search?.RawResultCount ?? 0, search.Search?.Releases.Count ?? 0, search.Search?.Outcomes ?? [], search.Search?.Trace ?? []);
        return new ReadingManualSearchResult(target, candidates, summary, search.WinnerReason);
    }

    public async Task<ManualGrabOutcome> GrabAsync(Guid requestId, string releaseIdentity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseIdentity);
        if (await requests.GetAsync(requestId, cancellationToken) is not { } request || !IsUsenetSearchable(request))
        {
            return new ManualGrabOutcome(ManualGrabStatus.NotFound, null, null);
        }

        var payload = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request));
        if ((payload.TriedReleases ?? []).Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase))
        {
            return new ManualGrabOutcome(ManualGrabStatus.AlreadySubmitted, null, null);
        }

        var profile = await profiles.ResolveAsync(request.Kind, workId: null, cancellationToken);
        var lookup = reliability is null ? null : await reliability.LoadAsync(cancellationToken);
        var options = new SearchOptions { Purpose = SearchPurpose.Interactive, Refresh = true };
        var search = await ReadingUsenetSearch.SearchAsync(indexers, ReadingAcquisitionEngine.ToTarget(request.Kind, payload), cancellationToken, options, profile, lookup, SelectionContext.SinceCreated(request.CreatedAt));
        var selected = search.Ranked.FirstOrDefault(ranked => ranked.Release.Identity.Equals(releaseIdentity, StringComparison.Ordinal));
        if (selected is null || selected.Score <= 0 || selected.Release.InternalDownloadUri is null)
        {
            return new ManualGrabOutcome(ManualGrabStatus.NotAvailable, null, null);
        }

        var candidate = new ReleaseRequestCandidate(selected.Release.Identity, selected.Release.Title, selected.Release.InternalDownloadUri, selected.Release.Indexer, selected.Release.ParsedRelease.ReleaseGroup);
        return await coordinator.GrabAsync(
            request,
            [AcquisitionRequestStatus.Approved, AcquisitionRequestStatus.Failed, AcquisitionRequestStatus.Pending],
            async (claimed, progress) =>
            {
                var fresh = ReadingAcquisitionEngine.ReadPayload(claimed, ReadingAcquisitionEngine.FallbackTarget(claimed));
                return (fresh.TriedReleases ?? []).Contains(releaseIdentity, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : await engine.GrabAsync(claimed, fresh, [candidate], "The selected release is no longer available.", cancellationToken, progress);
            },
            cancellationToken);
    }

    // A public Syosetu work is imported from its web source and never searched on Usenet.
    private static bool IsUsenetSearchable(AcquisitionRequest request) =>
        request.Kind is MediaAcquisitionKind.Manga or MediaAcquisitionKind.LightNovel
        && !request.Provider.Equals(Jularr.Web.Features.Novels.NcodeNovelSourceProvider.ProviderKey, StringComparison.OrdinalIgnoreCase);

    private async Task<ReadingManualSearchTarget> TargetOfAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var payload = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request));
        var profile = await profiles.ResolveAsync(request.Kind, workId: null, cancellationToken);
        return new ReadingManualSearchTarget(request.Id, request.Kind, payload.Title, payload.Author, payload.RequestedVolume, profile.Name, request.Status, request.StatusMessage, payload.Searches, payload.NextSearchUtc, payload.TriedReleases ?? []);
    }

    private ReadingManualCandidate ToCandidate(RankedReadingRelease ranked, HashSet<string> tried, bool requestIsOpen)
    {
        var release = ranked.Release;
        var selection = ranked.Selection;
        var isSelectable = selection?.IsSelectable == true && ranked.Score > 0 && release.InternalDownloadUri is not null;
        var isTried = tried.Contains(release.Identity);
        var verdict = !isSelectable
            ? ManualSearchVerdict.Rejected
            : selection!.Decision == SelectionDecision.Temporary ? ManualSearchVerdict.Warning : ManualSearchVerdict.Eligible;
        var parsed = ranked.Parsed;
        var chapters = parsed.ChapterStart is { } start
            ? parsed.ChapterEnd is { } end && end != start ? $"{start:0.##}-{end:0.##}" : $"{start:0.##}"
            : null;
        return new ReadingManualCandidate(
            release.Identity,
            release.Title,
            [.. release.Sources.Select(source => source.Indexer).DefaultIfEmpty(release.Indexer ?? "—")],
            release.SizeBytes,
            release.PublishedAt is { } published ? Math.Max(0, (int)(clock.GetUtcNow() - published).TotalDays) : null,
            selection?.Score?.QualityKey ?? ReadingReleaseEvidenceParser.QualityOf(parsed.Format),
            parsed.Language,
            parsed.VolumeNumber,
            chapters,
            parsed.IsCompleteOrBatch,
            selection?.Candidate.Identity.Confidence ?? IdentityConfidence.Conflict,
            verdict,
            isSelectable ? ranked.Score : null,
            selection?.Reasons ?? [],
            ranked.RejectedBecause,
            isTried,
            isSelectable && !isTried && requestIsOpen,
            release.Provenance);
    }
}
