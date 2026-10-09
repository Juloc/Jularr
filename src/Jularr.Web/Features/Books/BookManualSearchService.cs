using System.Collections.Concurrent;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Events;

namespace Jularr.Web.Features.Books;

/// <summary>
/// Admin-only manual release search for an existing Book request. The browser submits only a
/// release identity: every grab performs a fresh server-side search and re-validates the selected
/// candidate before its internal download URL can reach the shared download-client submission path.
/// </summary>
public sealed class BookManualSearchService(
    AcquisitionCore core,
    QualityProfileStore profiles,
    AcquisitionAccessStore requests,
    IJularrEventPublisher events,
    TimeProvider clock,
    RequestWorkBinder? binder = null)
{
    private static readonly ConcurrentDictionary<Guid, SearchCacheEntry> SearchCache = new();
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(2);
    public async Task<BookManualSearchTarget> LoadAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await RequireRequestAsync(requestId, cancellationToken);
        EnsureSearchable(request);
        return new BookManualSearchTarget(
            request,
            BookAcquisitionExecutor.ReadPayload(request));
    }

    public async Task<BookManualSearchResult> SearchAsync(
        Guid requestId,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        var target = await LoadAsync(requestId, cancellationToken);
        var now = clock.GetUtcNow();
        if (!refresh
            && SearchCache.TryGetValue(requestId, out var cached)
            && now - cached.StoredAt < SearchCacheLifetime
            && cached.Title.Equals(target.Payload.Title, StringComparison.Ordinal)
            && string.Equals(cached.Author, target.Payload.Author, StringComparison.Ordinal))
        {
            return new BookManualSearchResult(
                target.Request,
                target.Payload,
                cached.Search);
        }

        var result = await SearchBookAsync(target.Request, target.Payload, refresh, cancellationToken);
        SearchCache[requestId] = new SearchCacheEntry(
            now,
            target.Payload.Title,
            target.Payload.Author,
            result);

        return new BookManualSearchResult(
            target.Request,
            target.Payload,
            result);
    }

    public async Task<AcquisitionRequest> GrabAsync(
        Guid requestId,
        string releaseIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseIdentity);

        var request = await RequireRequestAsync(requestId, cancellationToken);
        EnsureSearchable(request);

        var payload = BookAcquisitionExecutor.ReadPayload(request);
        var result = await SearchBookAsync(request, payload, refresh: true, cancellationToken);
        SearchCache.TryRemove(requestId, out _);
        var selected = SelectRelease(
            result,
            releaseIdentity.Trim(),
            payload.TriedReleases);

        if (selected is null)
        {
            throw new InvalidOperationException(
                "The selected release is no longer an eligible Book release.");
        }

        var execution = await core.GrabAsync(
            request,
            payload,
            [selected.Evaluation!],
            result.FailureMessage,
            new GrabTarget(BookAcquisitionExecutor.OperationKind, "Download Book", payload.Title, MediaAcquisitionKind.Book, string.Empty),
            cancellationToken);

        await requests.UpdateStatusAsync(
            request.Id,
            execution.Status,
            execution.Message,
            execution.OperationId,
            execution.ResultUrl,
            decidedByProfileId: null,
            cancellationToken);

        if (execution.Status == AcquisitionRequestStatus.Downloading)
        {
            await events.PublishAsync(
                JularrEvent.Create(
                    JularrEventCategory.ReleaseAvailable,
                    profileId: request.RequestedByProfileId,
                    mediaType: AcquisitionAccessNames.Kind(request.Kind),
                    subjectId: request.Id.ToString(),
                    messageParams: new Dictionary<string, string>
                    {
                        ["title"] = request.Title
                    },
                    deepLink: execution.ResultUrl ?? AcquisitionRequestService.StatusPath(request.Id),
                    dedupKey: $"acquisition-request:{request.Id}:release-available",
                    relatedOperationId: execution.OperationId),
                cancellationToken);
        }

        return await RequireRequestAsync(request.Id, cancellationToken);
    }

    /// <summary>
    /// Resolves the POSTed opaque identity against a fresh search. Rejected, URL-less and already
    /// tried results are never returned, so a client cannot turn Manual Search into an arbitrary
    /// download-URL submission endpoint.
    /// </summary>
    public static RankedBookRelease? SelectRelease(
        BookUsenetSearchResult search,
        string releaseIdentity,
        IReadOnlyList<string>? triedReleases = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (string.IsNullOrWhiteSpace(releaseIdentity))
        {
            return null;
        }

        var tried = new HashSet<string>(
            triedReleases ?? [],
            StringComparer.OrdinalIgnoreCase);

        return search.Ranked.FirstOrDefault(candidate =>
            candidate.Score > 0
            && candidate.Release.IsAcquirable
            && candidate.Release.Identity.Equals(
                releaseIdentity,
                StringComparison.Ordinal)
            && !tried.Contains(candidate.Release.Identity));
    }

    private async Task<BookUsenetSearchResult> SearchBookAsync(AcquisitionRequest request, BookRequestPayload payload, bool refresh, CancellationToken cancellationToken)
    {
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Book, request.WorkId, cancellationToken);
        var options = new SearchOptions { Purpose = SearchPurpose.Interactive, Refresh = refresh };
        return BookReleaseSelector.ToResult(await core.SearchAsync(BookReleaseSelector.Plan(payload.Title, payload.Author, payload.CatalogId), profile, options, cancellationToken));
    }

    private async Task<AcquisitionRequest> RequireRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        await requests.GetAsync(requestId, cancellationToken) is { } request
            ? binder is null ? request : await binder.EnsureBoundAsync(request, cancellationToken)
            : throw new InvalidOperationException(
                "The request no longer exists.");

    private sealed record SearchCacheEntry(
        DateTimeOffset StoredAt,
        string Title,
        string? Author,
        BookUsenetSearchResult Search);

    private static void EnsureSearchable(AcquisitionRequest request)
    {
        if (request.Kind != MediaAcquisitionKind.Book)
        {
            throw new InvalidOperationException(
                "Manual Book search only accepts Book requests.");
        }

        if (request.Status is not (
                AcquisitionRequestStatus.Approved
                or AcquisitionRequestStatus.Failed))
        {
            throw new InvalidOperationException(
                "This request is not waiting for a Book release.");
        }
    }
}

public sealed record BookManualSearchTarget(
    AcquisitionRequest Request,
    BookRequestPayload Payload);

public sealed record BookManualSearchResult(
    AcquisitionRequest Request,
    BookRequestPayload Payload,
    BookUsenetSearchResult Search)
{
    public IReadOnlySet<string> TriedReleases { get; } =
        new HashSet<string>(
            Payload.TriedReleases ?? [],
            StringComparer.OrdinalIgnoreCase);

    public bool CanGrab(RankedBookRelease candidate) =>
        candidate.Score > 0
        && candidate.Release.IsAcquirable
        && !TriedReleases.Contains(candidate.Release.Identity);
}
