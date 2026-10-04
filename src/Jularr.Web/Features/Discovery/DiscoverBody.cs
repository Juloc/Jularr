using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Recommendations;

namespace Jularr.Web.Features.Discovery;

public enum DiscoverBodyState
{
    /// <summary>The landing: rows of titles.</summary>
    Shelves,

    /// <summary>A search or drill-down: one grid of titles.</summary>
    Results,

    /// <summary>Titles were found, but none passes the filters.</summary>
    NoResults,

    /// <summary>The sources answered with nothing.</summary>
    Empty,

    /// <summary>The sources did not answer.</summary>
    Unavailable,

    /// <summary>My AniList was asked for, but no AniList account is connected.</summary>
    NotConnected,

    /// <summary>My AniList was asked for books, which AniList does not list.</summary>
    BooksNotInList
}

/// <summary>The body of the Discover page under the search field: what the Body handler renders for one address.</summary>
/// <param name="Degraded">Some sources did not answer; the titles shown may be incomplete.</param>
/// <param name="Total">The titles found, before the filters narrowed them.</param>
public sealed record DiscoverBodyView(
    UiTextBundle Ui,
    DiscoverBrowseQuery Query,
    DiscoverBodyState State,
    IReadOnlyList<DiscoverShelfView> Shelves,
    IReadOnlyList<DiscoverCardView> Cards,
    int Total,
    bool Degraded)
{
    public static DiscoverBodyView Of(UiTextBundle ui, DiscoverBrowseQuery query, DiscoverBodyState state) =>
        new(ui, query, state, [], [], 0, false);
}

public static class DiscoverRecommendations
{
    /// <summary>
    /// A personalised recommendation as a discovery title, so it goes through the same card as every other
    /// title. A candidate without a provider identity gets none, which keeps follow and request off its card.
    /// </summary>
    public static DiscoveryItem ToItem(MediaRecommendationCandidate candidate)
    {
        var category = candidate.MediaType switch
        {
            WorkMediaType.Anime => "anime",
            WorkMediaType.Manga => "manga",
            WorkMediaType.LightNovel => "light-novel",
            _ => "book"
        };

        return new DiscoveryItem(
            "recommendation:" + candidate.Id,
            category,
            candidate.Identity?.ProviderKey ?? "",
            candidate.Identity?.ExternalKey ?? candidate.Id,
            candidate.Title,
            null,
            null,
            candidate.CoverImageUrl,
            null,
            null,
            candidate.Year,
            null,
            null,
            null,
            null,
            [],
            candidate.IsLocal,
            candidate.IsLocal ? candidate.Href : null,
            candidate.Href,
            false);
    }

    public static DiscoveryCategory CategoryOf(WorkMediaType? type) => type switch
    {
        WorkMediaType.Anime => DiscoveryCategory.Anime,
        WorkMediaType.Movie => DiscoveryCategory.Movie,
        WorkMediaType.Series => DiscoveryCategory.Series,
        WorkMediaType.Manga => DiscoveryCategory.Manga,
        WorkMediaType.LightNovel => DiscoveryCategory.LightNovel,
        WorkMediaType.Book => DiscoveryCategory.Book,
        _ => DiscoveryCategory.All
    };
}
