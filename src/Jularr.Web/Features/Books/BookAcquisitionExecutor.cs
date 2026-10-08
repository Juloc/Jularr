using System.Text.Json;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Wanted;

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
/// Automatic Books acquisition on the shared acquisition path: a direct/free catalog edition is
/// preferred, then an enabled OPDS EPUB, then every enabled Usenet indexer. The best accepted
/// Usenet release goes through the generic download-client abstraction, and the shared
/// completed-download dispatcher imports it (see
/// <see cref="BookCompletedDownloadImportAdapter"/>).
/// </summary>
public sealed class BookAcquisitionExecutor(
    BookCatalogService books,
    BookSearchCoordinator search,
    DownloadClientStore downloadClients,
    DownloadClientSubmissionService downloads,
    ReleaseRequestTracker tracker,
    RequestWorkBinder? binder = null) : IAcquisitionRequestExecutor
{
    /// <summary>Operation kind of a request-backed Books download.</summary>
    public const string OperationKind = "book-usenet-download";

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        request = binder is null ? request : await binder.EnsureBoundAsync(request, cancellationToken);
        var payload = ReadPayload(request);

        string? directNote = null;
        // Default source priority is local/direct before network download. This is a policy choice,
        // not a second Books acquisition engine: every Usenet download still uses the shared path.
        try
        {
            var workId = await books.AcquireCatalogBookAsync(payload.CatalogId, cancellationToken);
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Completed,
                "Imported a direct/free edition.",
                ResultUrl: $"/Books/Library/{workId}");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            directNote = exception.Message;
        }

        string? opdsNote = null;
        try
        {
            var opdsQuery = BookWorkSearch.MainTitle(payload.Title);
            var offer = await search.FindOpdsOfferAsync(
                payload.Title,
                payload.Author,
                cancellationToken);

            if (offer is not null)
            {
                var workId = await books.ImportOpdsBookAsync(
                    offer.SourceId,
                    opdsQuery,
                    offer.Key,
                    cancellationToken);
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Completed,
                    $"Imported an EPUB from {offer.SourceName}.",
                    ResultUrl: $"/Books/Library/{workId}");
            }

            opdsNote = "No matching enabled OPDS edition was found.";
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            opdsNote = exception.Message;
        }

        if (!await search.HasEnabledIndexerAsync(cancellationToken))
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                $"No direct/free or OPDS edition is available ({directNote}; {opdsNote}) and no indexer is configured.");
        }

        var downloadClientConfigured = (await downloadClients.LoadAllAsync(cancellationToken))
            .Any(entry => entry.Enabled);
        if (!downloadClientConfigured)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                $"No direct/free or OPDS edition is available ({directNote}; {opdsNote}) and no download client is configured.");
        }

        var usenetSearch = await search.SearchUsenetAsync(
            payload.Title,
            payload.Author,
            cancellationToken,
            request.WorkId);
        return await tracker.ContinueAsync(
            request,
            payload,
            Candidates(usenetSearch),
            usenetSearch.FailureMessage,
            async release =>
            {
                var outcome = await downloads.SubmitAsync(
                    new DownloadSubmissionSpec(
                        OperationKind,
                        "Download Book",
                        payload.Title,
                        request.RequestedByProfileId,
                        release.DownloadUri,
                        payload.Title,
                        MediaAcquisitionKind.Book,
                        ReleaseSource: release.Source,
                        ReleaseGroup: release.ReleaseGroup),
                    cancellationToken);
                return new ReleaseRequestSubmission(outcome.Accepted, outcome.OperationId, outcome.Message);
            },
            cancellationToken,
            searchUnavailable: usenetSearch.EveryIndexerFailed);
    }

    /// <summary>
    /// The releases the book selector accepted, best first. A Books release is remembered by its
    /// title, as it always was, so requests created before the shared state keep their history.
    /// </summary>
    public static IReadOnlyList<ReleaseRequestCandidate> Candidates(BookUsenetSearchResult search) =>
        search.Ranked
            .Where(candidate => candidate.Score > 0 && candidate.Release.InternalDownloadUri is not null)
            .Select(candidate => new ReleaseRequestCandidate(
                candidate.Release.Title,
                candidate.Release.Title,
                candidate.Release.InternalDownloadUri!,
                candidate.Release.Indexer,
                candidate.Release.ParsedRelease.ReleaseGroup))
            .ToArray();

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
public sealed record RankedBookRelease(ProwlarrReleaseCandidate Release, int Score, string? RejectedBecause)
{
    public string? QualityKey { get; init; }

    public int QualityRank { get; init; } = int.MaxValue;

    public IReadOnlyList<string> ScoreReasons { get; init; } = [];
}

/// <summary>The outcome of one book search on the indexers, shared by automatic adding and the admin test tool.</summary>
public sealed record BookUsenetSearchResult(
    IReadOnlyList<string> Queries,
    IReadOnlyList<RankedBookRelease> Ranked,
    IReadOnlyList<IndexerSearchWarning> Warnings,
    bool UsedCategoryFallback)
{
    /// <summary>True when no indexer could answer at all, so an empty result says nothing about the book.</summary>
    public bool EveryIndexerFailed { get; init; }

    public ProwlarrReleaseCandidate? Picked => Ranked.FirstOrDefault(release => release.Score > 0)?.Release;

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

/// <summary>Ranks indexer results for one book: Usenet only, EPUB first, then PDF, title words must match.</summary>
public static class BookReleaseSelector
{
    private static readonly string[] UnsupportedFormats = ["mobi", "azw3", "azw", "djvu", "cbr", "cbz", "mp3", "m4b", "audiobook", "hörbuch"];

    public static ProwlarrReleaseCandidate? Pick(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        string title,
        string? author,
        QualityProfile? profile = null) =>
        Rank(releases, title, author, profile).FirstOrDefault(release => release.Score > 0)?.Release;

    /// <summary>Every release, best first; rejected releases (score 0) last, each with its reason.</summary>
    public static IReadOnlyList<RankedBookRelease> Rank(
        IReadOnlyList<ProwlarrReleaseCandidate> releases,
        string title,
        string? author,
        QualityProfile? profile = null,
        ReleaseReliabilityLookup? reliability = null)
    {
        // Identity is decided first and the shared selection engine orders what is left, so a custom profile can reject or prefer a
        // format, regex or scored term but can never make a release for another book eligible.
        var effectiveProfile = profile ?? BookQualityProfiles.CreateDefaultBook();
        var titleWords = Words(SearchPlanner.MainTitle(title));
        var authorWords = Words(author);
        var judged = releases.GroupBy(release => release.Identity, StringComparer.Ordinal).ToDictionary(group => group.Key, group => Judge(group.First(), titleWords, authorWords), StringComparer.Ordinal);
        var selection = ReleaseSelectionEngine.Select(effectiveProfile, [.. judged.Values.Select(item => item.Candidate)], reliability);
        return [.. selection.Ranked.Select(evaluation => ToRanked(evaluation, judged[evaluation.Candidate.Id]))];
    }

    private sealed record BookJudgement(ProwlarrReleaseCandidate Release, SelectionCandidate Candidate, int MatchedTitleWords, int AuthorHits);

    private static BookJudgement Judge(ProwlarrReleaseCandidate release, IReadOnlyCollection<string> titleWords, IReadOnlyCollection<string> authorWords)
    {
        var words = Words(release.Title);
        var matchedTitle = titleWords.Count(words.Contains);
        var authorHits = authorWords.Count(words.Contains);
        var formatWords = words.Where(word => UnsupportedFormats.Contains(word)).ToArray();
        var safety = release.InternalDownloadUri is null
            ? "no download link"
            : release.Protocol is not null && !release.Protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase)
                ? "not a Usenet release"
                : !words.Contains("epub") && !words.Contains("pdf") && formatWords.Length > 0 ? $"{formatWords[0].ToUpperInvariant()}, not EPUB or PDF" : null;
        var identity = titleWords.Count == 0
            ? ReleaseIdentityEvidence.Conflict("EmptyTitle", "empty title")
            : matchedTitle < titleWords.Count
                ? ReleaseIdentityEvidence.Conflict("TitleDoesNotMatch", "title does not match")
                : authorHits > 0
                    ? ReleaseIdentityEvidence.Exact("TitleAndAuthor", "Title and author match.")
                    : ReleaseIdentityEvidence.Strong("Title", "The title matches.");

        // An oversized release for one book costs storage: it only loses against an otherwise equal one.
        var coverage = SelectionCoverage.Single with { Cost = release.SizeBytes is > 200L * 1024 * 1024 ? 6 : 0 };
        var candidate = new SelectionCandidate(release.Identity, BookReleaseParser.Instance.Parse(release.Title), release.SizeBytes, release.Indexer, release.Sources.FirstOrDefault()?.Priority ?? 0, release.PublishedAt, identity, coverage)
        {
            SafetyRejection = safety
        };
        return new BookJudgement(release, candidate, matchedTitle, authorHits);
    }

    private static RankedBookRelease ToRanked(CandidateEvaluation evaluation, BookJudgement judged)
    {
        var score = evaluation.Score;
        if (!evaluation.IsSelectable)
        {
            var because = evaluation.Reasons.FirstOrDefault(reason => reason.Kind is SelectionReasonKind.Safety)?.Detail
                          ?? (evaluation.Candidate.Identity.Confidence == IdentityConfidence.Conflict ? evaluation.Candidate.Identity.Detail : string.Join("; ", score?.RejectionReasons ?? []));
            return new RankedBookRelease(judged.Release, 0, because)
            {
                QualityKey = score?.QualityKey,
                QualityRank = score?.QualityRank ?? int.MaxValue,
                ScoreReasons = score?.ScoreReasons ?? []
            };
        }

        // The displayed score keeps its meaning: identity points plus the profile preference, minus the cost of a large release.
        var reasons = new List<string> { $"Title match +{10 + judged.MatchedTitleWords}" };
        var total = 10 + judged.MatchedTitleWords + score!.Score;
        if (judged.AuthorHits > 0)
        {
            total += judged.AuthorHits * 2;
            reasons.Add($"Author match +{judged.AuthorHits * 2}");
        }

        reasons.Add($"Quality {score.QualityKey}");
        reasons.AddRange(score.ScoreReasons);
        if (evaluation.Candidate.Coverage.Cost > 0)
        {
            total -= evaluation.Candidate.Coverage.Cost;
            reasons.Add($"Large release -{evaluation.Candidate.Coverage.Cost}");
        }

        return new RankedBookRelease(judged.Release, Math.Max(total, 1), null)
        {
            QualityKey = score.QualityKey,
            QualityRank = score.QualityRank,
            ScoreReasons = reasons
        };
    }

    private static HashSet<string> Words(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var word in value.Split(
                     [' ', '.', '_', '-', ':', ',', '(', ')', '[', ']', '\'', '"', '!', '?', '&', '/'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 1 && !StopWords.Contains(word))
            {
                result.Add(word.ToLowerInvariant());
            }
        }

        return result;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "and", "der", "die", "das", "und", "des", "le", "la", "les"
    };
}
