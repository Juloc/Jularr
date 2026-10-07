using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Selection;

namespace Jularr.Web.Features.Books;

/// <summary>
/// Availability discovered while resolving one canonical book search result. Availability is
/// external evidence only; it never creates a Work/Edition/Version by itself.
/// </summary>
public sealed record BookSearchAvailability(
    bool DirectOrFree,
    bool Opds,
    bool Usenet,
    int EligibleUsenetReleases);

/// <summary>One canonical Books search result plus the acquisition sources currently known for it.</summary>
public sealed record BookSearchItem(
    BookCatalogItem Book,
    BookSearchAvailability Availability);

/// <summary>A provider/indexer problem that did not prevent other sources from answering.</summary>
public sealed record BookSearchWarning(
    string Source,
    string Message);

/// <summary>
/// Application-level Books search. Catalog metadata, every enabled OPDS catalog and every enabled
/// Usenet indexer are searched through their existing adapters, then normalized back into the
/// canonical BookWorkSearch model. Usenet releases remain temporary acquisition candidates.
/// </summary>
public sealed class BookSearchCoordinator(
    BookCatalogService books,
    IndexerSearchCoordinator indexers,
    QualityProfileStore qualityProfiles,
    ILogger<BookSearchCoordinator> logger,
    ReleaseReliabilityService? reliability = null)
{
    /// <summary>The warning source of the metadata catalogs (Open Library, Google Books, Wikisource) when none of them answered.</summary>
    public const string CatalogSource = "Book catalogs";

    private const int ResultLimit = 24;

    public async Task<BookSearchResponse> SearchAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var browse = await books.SearchAsync(query, cancellationToken);
            return new BookSearchResponse(
                browse
                    .Take(ResultLimit)
                    .Select(book => new BookSearchItem(
                        book,
                        new BookSearchAvailability(book.CanAcquire, false, false, 0)))
                    .ToArray(),
                []);
        }

        var normalizedQuery = query.Trim();

        // Catalog and OPDS discovery are independent, so they run in parallel. Release search is
        // planned only after those records have been deduplicated into canonical works; this keeps
        // indexer load bounded and lets it reuse the normal Books author/title query rules.
        var catalogTask = CaptureAsync(
            CatalogSource,
            () => books.SearchPrimaryCatalogsAsync(normalizedQuery, cancellationToken),
            Array.Empty<BookCatalogItem>(),
            cancellationToken);
        var gutenbergTask = CaptureAsync(
            "Project Gutenberg",
            () => books.SearchGutenbergCatalogAsync(normalizedQuery, cancellationToken),
            Array.Empty<BookCatalogItem>(),
            cancellationToken);
        var opdsTask = CaptureAsync(
            "OPDS",
            () => books.SearchOpdsAsync(null, normalizedQuery, cancellationToken),
            Array.Empty<BookOpdsCatalogItem>(),
            cancellationToken);
        var qualityProfileTask = qualityProfiles.ResolveAsync(
            MediaAcquisitionKind.Book,
            workId: null,
            cancellationToken);

        await Task.WhenAll(
            catalogTask,
            gutenbergTask,
            opdsTask,
            qualityProfileTask);

        var catalog = catalogTask.Result.Value;
        var gutenberg = gutenbergTask.Result.Value;
        var opds = opdsTask.Result.Value;
        var bookProfile = qualityProfileTask.Result;
        var opdsCatalog = opds.Select(ToCatalogItem).ToArray();

        // OPDS participates in the exact same title/author work merge as metadata providers. It
        // stays provenance on the resulting work instead of becoming a parallel library identity.
        var works = BookWorkSearch.Rank(
                normalizedQuery,
                catalog,
                gutenberg,
                opdsCatalog)
            .Take(ResultLimit)
            .ToArray();

        // Check every displayed canonical work against Usenet without turning the search page into
        // N separate indexer calls per row. One strongest identity query per displayed work plus the
        // user's original query gives each result a chance to surface indexer availability while the
        // total query count stays bounded by ResultLimit + 1.
        var usenetQueries = BuildUsenetQueries(
            works,
            normalizedQuery);
        var usenetResult = await CaptureUsenetPoolAsync(
            usenetQueries,
            cancellationToken);
        var usenet = usenetResult.Value;

        var items = works.Select(work =>
        {
            var opdsAvailable = opds.Any(offer =>
                BookWorkSearch.SameWork(
                    work.Title,
                    work.Author,
                    offer.Title,
                    offer.Author));

            // Identity/title matching is deliberately applied before an indexer release can count
            // as available. A high quality release for another book is therefore never surfaced.
            var ranked = BookReleaseSelector.Rank(
                usenet.Releases,
                work.Title,
                work.Author,
                bookProfile);
            var eligible = ranked.Count(candidate =>
                candidate.Score > 0
                && candidate.Release.InternalDownloadUri is not null);

            return new BookSearchItem(
                work,
                new BookSearchAvailability(
                    work.CanAcquire,
                    opdsAvailable,
                    eligible > 0,
                    eligible));
        }).ToArray();

        var warnings = new List<BookSearchWarning>();
        AddWarning(warnings, catalogTask.Result.Warning);
        AddWarning(warnings, gutenbergTask.Result.Warning);
        AddWarning(warnings, opdsTask.Result.Warning);
        AddWarning(warnings, usenetResult.Warning);
        warnings.AddRange(usenet.Warnings.Select(warning =>
            new BookSearchWarning(
                warning.IndexerName,
                string.IsNullOrWhiteSpace(warning.Query)
                    ? warning.Message
                    : $"{warning.Query}: {warning.Message}")));

        return new BookSearchResponse(
            items,
            warnings
                .Distinct()
                .ToArray());
    }

    /// <summary>
    /// Builds one bounded Usenet availability query for every result shown by consumer search.
    /// The author + main-title query is preferred where possible because it is the strongest Book
    /// identity query; the user's original query is kept as a broad fallback.
    /// </summary>
    public static IReadOnlyList<string> BuildUsenetQueries(
        IReadOnlyList<BookCatalogItem> works,
        string originalQuery)
    {
        ArgumentNullException.ThrowIfNull(works);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalQuery);

        return works
            .Take(ResultLimit)
            .Select(work => SearchPlanner.BookQuery(work.Title, work.Author))
            .Prepend(originalQuery.Trim())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ResultLimit + 1)
            .ToArray();
    }

    /// <summary>
    /// Finds a directly-acquirable OPDS copy for a requested canonical work. Automatic
    /// acquisition uses the same title/author identity rule as consumer search.
    /// </summary>
    public async Task<BookOpdsCatalogItem?> FindOpdsOfferAsync(
        string title,
        string? author,
        CancellationToken cancellationToken)
    {
        var offers = await books.SearchOpdsAsync(
            null,
            BookWorkSearch.MainTitle(title),
            cancellationToken);

        return offers.FirstOrDefault(offer =>
            BookWorkSearch.SameWork(
                title,
                author,
                offer.Title,
                offer.Author));
    }

    public Task<bool> HasEnabledIndexerAsync(CancellationToken cancellationToken) =>
        indexers.HasEnabledIndexerAsync(cancellationToken);

    /// <summary>Shared automatic/manual Books Usenet search path.</summary>
    public async Task<BookUsenetSearchResult> SearchUsenetAsync(
        string title,
        string? author,
        CancellationToken cancellationToken,
        DateTimeOffset? wantedSince = null,
        Guid? workId = null)
    {
        var profile = await qualityProfiles.ResolveAsync(
            MediaAcquisitionKind.Book,
            workId,
            cancellationToken);
        return await BookUsenetSearch.SearchAsync(indexers, title, author, profile, cancellationToken, reliability: reliability is null ? null : await reliability.LoadAsync(cancellationToken), wantedSince: wantedSince);
    }

    private async Task<SourceResult<UsenetPool>> CaptureUsenetPoolAsync(
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await indexers.HasEnabledIndexerAsync(cancellationToken))
            {
                return new SourceResult<UsenetPool>(
                    new UsenetPool([], [], false),
                    null);
            }

            var result = await indexers.SearchTextAsync(MediaAcquisitionKind.Book, queries, new SearchOptions { Purpose = SearchPurpose.Interactive }, cancellationToken);
            var usedFallback = result.Trace.Any(line => line.Stage == "any-category" && line.Results > 0);

            return new SourceResult<UsenetPool>(
                new UsenetPool(
                    result.Releases,
                    result.Warnings,
                    usedFallback),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IndexerException
                or ProwlarrException
                or HttpRequestException
                or TaskCanceledException
                or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Books Usenet availability search failed for {Queries}",
                string.Join(" | ", queries));
            return new SourceResult<UsenetPool>(
                new UsenetPool([], [], false),
                new BookSearchWarning("Usenet", exception.Message));
        }
    }

    private async Task<SourceResult<T>> CaptureAsync<T>(
        string source,
        Func<Task<T>> action,
        T fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SourceResult<T>(
                await action(),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Books search source {Source} failed",
                source);
            return new SourceResult<T>(
                fallback,
                new BookSearchWarning(source, exception.Message));
        }
    }

    private static BookCatalogItem ToCatalogItem(BookOpdsCatalogItem item)
    {
        var identity =
            "opds-"
            + item.SourceId
            + "-"
            + Uri.EscapeDataString(item.Key);

        var covers = string.IsNullOrWhiteSpace(item.CoverImageUrl)
            ? Array.Empty<string>()
            : new[] { item.CoverImageUrl! };

        return new BookCatalogItem(
            identity,
            item.Title,
            item.Author,
            item.Description,
            item.CoverImageUrl,
            [],
            null,
            null,
            null,
            $"opds://{item.SourceId}/{Uri.EscapeDataString(item.Key)}",
            item.SourceName,
            null)
        {
            Identities = [identity],
            Language = BookWorkSearch.NormalizeLanguageTag(item.Language),
            CoverCandidates = covers
        };
    }

    private static void AddWarning(
        ICollection<BookSearchWarning> warnings,
        BookSearchWarning? warning)
    {
        if (warning is not null)
        {
            warnings.Add(warning);
        }
    }

    private sealed record SourceResult<T>(
        T Value,
        BookSearchWarning? Warning);

    private sealed record UsenetPool(
        IReadOnlyList<ProwlarrReleaseCandidate> Releases,
        IReadOnlyList<IndexerSearchWarning> Warnings,
        bool UsedCategoryFallback);
}

public sealed record BookSearchResponse(
    IReadOnlyList<BookSearchItem> Items,
    IReadOnlyList<BookSearchWarning> Warnings);
