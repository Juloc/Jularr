using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Recommendations;

namespace Jularr.Web.Features.Discovery;

public enum DiscoverBodyState
{
    /// <summary>Titles, or the places where they are about to arrive: rows on the landing, groups or one grid for a search or drill-down.</summary>
    Sections,

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

public enum DiscoverSectionLayout
{
    /// <summary>One horizontally scrolling row.</summary>
    Track,

    /// <summary>A wrapping grid, for a scope with one media type.</summary>
    Grid
}

/// <summary>
/// One part of the Discover body that is replaced as a whole when it changes: a row, a result group or the grid of a scope. A section keeps its
/// place while a source behind it is pending (reserved ghost cards the size of real ones), so the arrival of its titles moves nothing.
/// </summary>
/// <param name="Id">Stable between generations; the browser matches sections by it.</param>
/// <param name="Heading">The visible title, or null for a section that is named by its count only.</param>
/// <param name="Count">The "N results" text of a grid.</param>
/// <param name="Message">A consumer-safe sentence about a source that failed, shown with a retry for <paramref name="Retry"/>.</param>
public sealed record DiscoverSectionView(
    string Id,
    string? Heading,
    string? SeeAllUrl,
    DiscoverSectionLayout Layout,
    DiscoverySectionState State,
    IReadOnlyList<DiscoverCardView> Cards,
    string? Count,
    string? Message,
    IReadOnlyList<DiscoverySource> Retry)
{
    /// <summary>A media group of an all-types search: its heading folds the group away.</summary>
    public bool Collapsible { get; init; }

    public const int TrackGhosts = 6;
    public const int GridGhosts = 12;

    public int Ghosts => Layout == DiscoverSectionLayout.Grid ? GridGhosts : TrackGhosts;

    /// <summary>What a viewer would notice changing: the section's state, its titles in order and their indicators. The browser replaces a section only when this differs.</summary>
    public string Signature
    {
        get
        {
            var parts = new List<string> { Id, State.ToString(), Message ?? "", string.Join('+', Retry) };
            parts.AddRange(Cards.Select(card => $"{card.Key}~{card.State.Css}~{card.RequestStatus}~{card.IsFollowed}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];
        }
    }
}

/// <summary>The body of the Discover page under the search field: what the Body handler renders for one address.</summary>
/// <param name="Total">The titles found, before the filters narrowed them.</param>
/// <param name="Settled">The sources that have answered; the browser sends it back to wait for the next arrival.</param>
/// <param name="Pending">The sources still running; zero ends the browser's waiting.</param>
public sealed record DiscoverBodyView(
    UiTextBundle Ui,
    DiscoverBrowseQuery Query,
    DiscoverBodyState State,
    IReadOnlyList<DiscoverSectionView> Sections,
    int Total,
    int Settled,
    int Pending)
{
    public static DiscoverBodyView Of(UiTextBundle ui, DiscoverBrowseQuery query, DiscoverBodyState state) =>
        new(ui, query, state, [], 0, 0, 0);
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
