using System.Text.Json;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Data;

namespace Jularr.Web.Features.Books;

/// <summary>
/// What the Books add dialog stores with a request so it can be executed later. The Usenet
/// search state (tried releases, searches, next search, last problem) is the shared
/// <see cref="ReleaseRequestPayload"/>.
/// </summary>
public sealed record BookRequestPayload(
    string CatalogId,
    string Title,
    string? Author) : ReleaseRequestPayload;

/// <summary>
/// Automatic Books acquisition on the shared core: the free catalog edition, enabled OPDS catalogs and every enabled Usenet indexer are searched
/// together, the one selection ranks what they returned, and the winner goes where its acquisition type says (an import by its direct source, or the
/// download client and the shared completed-download dispatcher, see <see cref="BookCompletedDownloadImportAdapter"/>).
/// </summary>
public sealed class BookAcquisitionExecutor(
    IndexerSearchCoordinator indexers,
    DownloadClientStore downloadClients,
    AcquisitionCore core,
    QualityProfileStore profiles,
    ReleaseRequestTracker tracker,
    AppDbContext db,
    RequestWorkBinder? binder = null) : IAcquisitionRequestExecutor
{
    /// <summary>Operation kind of a request-backed Books download.</summary>
    public const string OperationKind = "book-usenet-download";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        request = binder is null ? request : await binder.EnsureBoundAsync(request, cancellationToken);
        var payload = ReadPayload(request);
        var profile = await profiles.ResolveAsync(MediaAcquisitionKind.Book, request.WorkId, cancellationToken);
        var search = await core.SearchAsync(BookReleaseSelector.Plan(payload.Title, payload.Author, payload.CatalogId), profile, new SearchOptions(), cancellationToken);

        // A Usenet release needs an indexer and a download client; without them only a direct edition can serve the request.
        var usenetConfigured = await indexers.HasEnabledIndexerAsync(cancellationToken) && (await downloadClients.LoadAllAsync(cancellationToken)).Any(entry => entry.Enabled);
        var grabbable = search.Grabbable.Where(release => usenetConfigured || release.Candidate.Type == AcquisitionType.DirectImport).ToArray();
        if (grabbable.Length == 0 && !usenetConfigured)
        {
            var problems = string.Join("; ", search.Search.Warnings.Select(warning => warning.Message));
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                $"No direct/free or OPDS edition is available{(problems.Length == 0 ? string.Empty : $" ({problems})")} and no indexer or download client is configured.");
        }

        // A Book that is in the library is only searched again while its profile wants a better format, and then only a better one is taken.
        if (request.WorkId is { } workId && await BookInstalledQuality.BestAsync(db, profile, workId, cancellationToken) is { } installed)
        {
            if (!UpgradePolicy.Assess(profile, installed).IsUpgradable)
            {
                return new AcquisitionExecution(AcquisitionRequestStatus.Completed, $"The book is in the library as {installed}.", ResultUrl: $"/Books/Library/{workId}");
            }

            var better = grabbable.Where(release => release.Score is { } score && UpgradePolicy.IsUpgrade(profile, installed, score.QualityKey)).ToArray();
            var waiting = await tracker.WaitForUpgradeAsync(
                request,
                payload,
                [.. better.Select(release => new ReleaseRequestCandidate(release.Candidate.Identity, release.Candidate.Title, release.Candidate.InternalDownloadUri, release.Candidate.Indexer, release.Candidate.ParsedRelease.ReleaseGroup))],
                $"The book is in the library as {installed} and no better release is known yet.",
                cancellationToken);
            if (waiting is not null)
            {
                return waiting;
            }

            grabbable = better;
        }

        return await core.GrabAsync(
            request,
            payload,
            grabbable,
            BookReleaseSelector.ToResult(search).FailureMessage,
            new GrabTarget(OperationKind, "Download Book", payload.Title, MediaAcquisitionKind.Book, string.Empty),
            cancellationToken,
            searchUnavailable: search.Search.EveryIndexerFailed);
    }

    public static BookRequestPayload ReadPayload(AcquisitionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            try
            {
                if (JsonSerializer.Deserialize<BookRequestPayload>(request.PayloadJson, JsonSerializerOptions.Web) is { } persisted
                    && !string.IsNullOrWhiteSpace(persisted.Title))
                {
                    return persisted;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new BookRequestPayload(request.ExternalId, request.Title, request.Subtitle);
    }
}

/// <summary>Books requests on the shared release-request Wanted policy.</summary>
public sealed class BookWantedRequestHandler(
    AcquisitionAccessStore store,
    AcquisitionRequestService requests) : ReleaseRequestWantedHandler(store, requests)
{
    public override MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    protected override ReleaseRequestPayload ReadPayload(AcquisitionRequest request) =>
        BookAcquisitionExecutor.ReadPayload(request);
}

/// <summary>One indexer result as the book selector judged it; <see cref="Score"/> 0 means rejected.</summary>
public sealed record RankedBookRelease(AcquisitionCandidate Release, int Score, string? RejectedBecause)
{
    /// <summary>The selection's evaluation this row shows, which a grab hands to the core; null for rows built without a search.</summary>
    public ReleaseEvaluation<BookMatch>? Evaluation { get; init; }

    public string? QualityKey { get; init; }

    public int QualityRank { get; init; } = int.MaxValue;

    public IReadOnlyList<string> ScoreReasons { get; init; } = [];
}

/// <summary>The outcome of one book search over every source, as Manual Search and the admin test tool list it.</summary>
public sealed record BookUsenetSearchResult(
    IReadOnlyList<string> Queries,
    IReadOnlyList<RankedBookRelease> Ranked,
    IReadOnlyList<IndexerSearchWarning> Warnings,
    bool UsedCategoryFallback)
{
    /// <summary>True when no indexer could answer at all, so an empty result says nothing about the book.</summary>
    public bool EveryIndexerFailed { get; init; }

    public AcquisitionCandidate? Picked => Ranked.FirstOrDefault(release => release.Score > 0)?.Release;

    public string FailureMessage =>
        Ranked.Count == 0
            ? Warnings.Count > 0
                ? $"No release found on the indexers ({Warnings[0].IndexerName}: {Warnings[0].Message})."
                : "No release found on the indexers."
            : "No suitable EPUB or PDF release found on the indexers.";
}

/// <summary>
/// Searches the indexers for one book through the shared planner: the author and title as a book search where the indexer supports
/// it, then author + title, the title and the full title as text in the Books categories and, when too little matches, once more
/// without a category because many indexers file ebooks inconsistently.
/// </summary>
public static class BookUsenetSearch
{
    public static async Task<BookUsenetSearchResult> SearchAsync(
        IndexerSearchCoordinator indexers,
        string title,
        string? author,
        QualityProfile profile,
        CancellationToken cancellationToken,
        SearchOptions? options = null,
        ReleaseReliabilityLookup? reliability = null)
    {
        var intent = new SearchIntent(MediaAcquisitionKind.Book, title.Trim()) { Creator = string.IsNullOrWhiteSpace(author) ? null : author.Trim() };
        var result = await indexers.SearchAsync(
            intent,
            (options ?? new SearchOptions()).WithSourcePolicy(profile.SourcePolicy) with { UsableCount = releases => BookReleaseSelector.Rank(releases, title, author, profile, reliability).Count(ranked => ranked.Score > 0) },
            cancellationToken);
        return new BookUsenetSearchResult(
            [.. result.Trace.Select(line => line.QueryText).Distinct(StringComparer.OrdinalIgnoreCase)],
            BookReleaseSelector.Rank(result.Releases, title, author, profile, reliability),
            result.Warnings,
            result.Trace.Any(line => line.Stage == "any-category" && line.Results > 0))
        {
            EveryIndexerFailed = result.EveryIndexerFailed
        };
    }
}

