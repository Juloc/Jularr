using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Search;

namespace Jularr.Web.Features.Books;

// The Book search intent carries the catalog id of the requested edition under this key.
public static class BookIntent
{
    public const string CatalogKey = "bookcatalog";
}

// A free edition of the requested catalog entry (Project Gutenberg, Wikisource, an EPUB the catalog links).
public sealed class BookCatalogDirectSource(BookCatalogService books) : IDirectSource
{
    public const string SourceName = "catalog";

    public string Name => SourceName;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public async Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(SearchIntent intent, CancellationToken cancellationToken)
    {
        if (!intent.ExternalIds.TryGetValue(BookIntent.CatalogKey, out var catalogId) || !await books.CanAcquireDirectlyAsync(catalogId, cancellationToken))
        {
            return [];
        }

        return [BookDirectCandidates.Of(intent.Title, "Free edition", new DirectOffer(SourceName, catalogId, IdentityIsExact: true))];
    }

    public async Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken) =>
        new(AcquisitionRequestStatus.Completed, "Imported a direct/free edition.", ResultUrl: $"/Books/Library/{await books.AcquireCatalogBookAsync(offer.Key, cancellationToken)}");
}

// An EPUB an enabled OPDS catalog offers for the same title and author.
public sealed class BookOpdsDirectSource(BookCatalogService books) : IDirectSource
{
    public const string SourceName = "opds";

    public string Name => SourceName;

    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

    public async Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(SearchIntent intent, CancellationToken cancellationToken) =>
        [.. (await books.SearchOpdsAsync(null, BookWorkSearch.MainTitle(intent.Title), cancellationToken))
            .Where(offer => BookWorkSearch.SameWork(intent.Title, intent.Creator, offer.Title, offer.Author))
            .Select(offer => BookDirectCandidates.Of(offer.Title, offer.SourceName, new DirectOffer(SourceName, $"{offer.SourceId}:{offer.Key}")))];

    public async Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken)
    {
        var separator = offer.Key.IndexOf(':');
        var sourceId = offer.Key[..separator];
        var workId = await books.ImportOpdsBookAsync(sourceId, BookWorkSearch.MainTitle(BookAcquisitionExecutor.ReadPayload(request).Title), offer.Key[(separator + 1)..], cancellationToken);
        return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "Imported an EPUB from an OPDS catalog.", ResultUrl: $"/Books/Library/{workId}");
    }
}

internal static class BookDirectCandidates
{
    // A direct file is an EPUB by construction; the title says so, so the release parser gives it the same quality a Usenet EPUB release has.
    public static AcquisitionCandidate Of(string title, string sourceName, DirectOffer offer)
    {
        var name = $"{title} [EPUB]";
        return new AcquisitionCandidate(name, sourceName, null, "direct", null, null, null, null, null, null, null, null, AnimeReleaseParser.Parse(name), [], null, null)
        {
            Type = AcquisitionType.DirectImport,
            Offer = offer
        };
    }
}
