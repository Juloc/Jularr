using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// The kinds of shelf a provider-driven discovery surface can offer (#595, epic #556). This is the
/// shared taxonomy the later shelf surfaces (#427 home rows, #428 library rows, #434 franchise rows)
/// reuse. Only the kinds that a readily available provider feed can honestly source are populated this
/// wave (<see cref="Trending"/>, <see cref="Top"/>, <see cref="NewlyPublished"/>); the remaining kinds
/// are declared so consumers can render them once an adapter exists, and are documented seams until
/// then (see <see cref="DiscoveryShelfComposer"/>).
/// </summary>
public enum DiscoveryShelfKind
{
    /// <summary>What is trending now for a media type (AniList <c>TRENDING_DESC</c>, Books trending).</summary>
    Trending,

    /// <summary>The highest-rated titles for a media type (AniList <c>SCORE_DESC</c>, Books popular).</summary>
    Top,

    /// <summary>Recently published titles. Only Books currently sources this honestly (#371).</summary>
    NewlyPublished,

    /// <summary>Currently airing/releasing titles. Seam: needs a provider status feed not yet available.</summary>
    Airing,

    /// <summary>Announced/not-yet-released titles. Seam: needs a provider status feed not yet available.</summary>
    Upcoming,

    /// <summary>Titles from one studio. Seam: needs a provider studio feed not yet available.</summary>
    ByStudio,

    /// <summary>Titles in one franchise the profile follows. Seam: needs a franchise-seeded feed.</summary>
    ByFranchise,

    /// <summary>"Because you…" personalized recommendations. Seam: needs a recommendation feed.</summary>
    Personalized
}

/// <summary>
/// One named, ordered row of a discovery board. Carries stable localization keys (not resolved text)
/// so the same row can be rendered by any surface in its own culture, plus the identity-deduped items
/// and a deep link back into <c>/Discover</c> pre-filtered to this row. Reused by #427/#428/#434.
/// </summary>
/// <param name="Id">Stable slug (e.g. <c>trending-anime</c>) for keys and test assertions.</param>
/// <param name="MediaType">The media type this row is about; <c>null</c> for a cross-type row.</param>
/// <param name="TitleKey">UI catalog key for the row's mode label (e.g. <c>discover.tabs.trending</c>).</param>
/// <param name="MediaLabelKey">UI catalog key for the media-type label, or <c>null</c> for a cross-type row.</param>
public sealed record DiscoveryShelfRow(
    string Id,
    DiscoveryShelfKind Kind,
    WorkMediaType? MediaType,
    DiscoveryCategory Category,
    DiscoveryMode Mode,
    string Genre,
    string TitleKey,
    string? MediaLabelKey,
    IReadOnlyList<DiscoveryItem> Items,
    IReadOnlyList<DiscoverySourceResult> Sources)
{
    /// <summary>Deep link into Discover pre-filtered to this row's category/mode/genre.</summary>
    public string DeepLinkUrl => DiscoveryShelfLinks.ToDiscoverUrl(Category, Mode, Genre);

    public DiscoverySectionState State => DiscoverySections.StateOf(Items.Count, Sources);
}

/// <summary>A resolved discovery board: the ordered visible rows, which may still wait for a source, and how far the sources have answered.</summary>
/// <param name="Settled">The sources that have answered or failed; a follow-up load passes it back to wait for the next arrival.</param>
public sealed record DiscoveryShelfBoard(
    IReadOnlyList<DiscoveryShelfRow> Rows,
    int Settled,
    int Pending)
{
    public bool IsEmpty => Rows.Count == 0;

    public static DiscoveryShelfBoard Empty { get; } = new([], 0, 0);
}

/// <summary>
/// The single provider entry point the shelf service composes against. <see cref="DiscoveryCoordinator"/>
/// implements it; expressing it as an interface keeps the shelf taxonomy, dedupe, capability filtering
/// and caching testable without any network, and lets a future feed (e.g. a TMDB movie/TV adapter) be
/// swapped in behind the same contract.
/// </summary>
public interface IDiscoveryFeed
{
    /// <summary>Loads the titles of several requests at once, so the provider calls of all of them run side by side.</summary>
    Task<DiscoveryLoad> LoadAsync(
        IReadOnlyList<DiscoveryRequest> requests,
        DiscoveryAudience audience,
        DiscoveryWait wait,
        CancellationToken cancellationToken);

    /// <summary>Whether an AniList account is connected, so that the My List browse view is offered only where it can answer.</summary>
    Task<bool> IsAniListConnectedAsync(CancellationToken cancellationToken);

    /// <summary>The library state of the given titles, keyed by <see cref="DiscoveryItem.Id"/>, read in one batch.</summary>
    Task<IReadOnlyDictionary<string, DiscoveryItem>> OverlayLocalStateAsync(IEnumerable<DiscoveryItem> items, string profileId, CancellationToken cancellationToken);
}

/// <summary>Builds the <c>/Discover</c> deep link for a shelf row, matching the client URL scheme in discover.js.</summary>
public static class DiscoveryShelfLinks
{
    public static string ToDiscoverUrl(DiscoveryCategory category, DiscoveryMode mode, string genre)
    {
        var parts = new List<string>(3);
        if (category != DiscoveryCategory.All)
        {
            parts.Add("category=" + CategoryParam(category));
        }

        // A scope of one type opens its overview without a mode, so its Trending view names itself.
        if (mode != DiscoveryMode.Trending || category != DiscoveryCategory.All)
        {
            parts.Add("mode=" + ModeParam(mode));
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            parts.Add("genre=" + Uri.EscapeDataString(genre));
        }

        return parts.Count == 0 ? "/" : "/?" + string.Join('&', parts);
    }

    private static string CategoryParam(DiscoveryCategory category) => category switch
    {
        DiscoveryCategory.Anime => "anime",
        DiscoveryCategory.Movie => "movie",
        DiscoveryCategory.Series => "tv",
        DiscoveryCategory.LightNovel => "light-novel",
        DiscoveryCategory.Manga => "manga",
        DiscoveryCategory.Book => "book",
        _ => "all"
    };

    private static string ModeParam(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "top",
        DiscoveryMode.MyList => "my-list",
        DiscoveryMode.New => "new",
        DiscoveryMode.Upcoming => "upcoming",
        DiscoveryMode.Search => "search",
        _ => "trending"
    };
}
