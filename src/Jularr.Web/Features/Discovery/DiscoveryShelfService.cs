using System.Collections.Concurrent;
using System.Security.Claims;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Discovery;

/// <summary>
/// One planned row before its items are fetched: the taxonomy entry plus how to source it. Pure data,
/// so the taxonomy and capability filtering can be asserted without touching a provider.
/// </summary>
public sealed record DiscoveryShelfPlan(
    string Id,
    DiscoveryShelfKind Kind,
    WorkMediaType MediaType,
    DiscoveryCategory Category,
    DiscoveryMode Mode,
    string TitleKey,
    string MediaLabelKey)
{
    public bool UsesAniList => Category is DiscoveryCategory.Anime or DiscoveryCategory.Manga or DiscoveryCategory.LightNovel;

    public bool UsesBooks => Category is DiscoveryCategory.Book;
}

/// <summary>
/// The pure taxonomy + normalisation behind the shelf board: which rows exist for a set of visible media
/// types, and identity-deduplication of a row's items across sources via the media-core identity. Kept
/// side-effect free so #595's row-taxonomy, capability-filter and identity-dedupe tests need no network.
/// </summary>
public static class DiscoveryShelfComposer
{
    /// <summary>
    /// Media types with a provider feed behind the shared discovery coordinator.
    /// </summary>
    public static IReadOnlyList<WorkMediaType> SupportedMediaTypes { get; } =
        [WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Manga, WorkMediaType.LightNovel, WorkMediaType.Book];

    /// <summary>Display order of media types across the board (anime first, books last).</summary>
    private static IReadOnlyList<WorkMediaType> DisplayOrder { get; } =
        [WorkMediaType.Anime, WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Manga, WorkMediaType.LightNovel, WorkMediaType.Book];

    /// <summary>
    /// The ordered rows for a profile's visible media types. Only types the profile may at least browse
    /// (<paramref name="visibleMediaTypes"/> from the capability policy), that have a provider feed, and
    /// whose source is enabled, produce rows — so a Books-only user gets book rows only. Trending rows
    /// lead (mixed media types), then Top rows, then the Books-only "newly published" row (#371).
    /// </summary>
    public static IReadOnlyList<DiscoveryShelfPlan> Plan(
        IReadOnlyList<WorkMediaType> visibleMediaTypes,
        bool includeAniList,
        bool includeBooks)
    {
        var visible = new HashSet<WorkMediaType>(visibleMediaTypes);
        var types = DisplayOrder
            .Where(visible.Contains)
            .Where(SupportedMediaTypes.Contains)
            .Where(type => SourceEnabled(type, includeAniList, includeBooks))
            .ToArray();

        var plans = new List<DiscoveryShelfPlan>();

        foreach (var type in types)
        {
            plans.Add(Row(type, DiscoveryShelfKind.Trending, DiscoveryMode.Trending));
        }

        foreach (var type in types)
        {
            plans.Add(Row(type, DiscoveryShelfKind.Top, DiscoveryMode.Top));
        }

        foreach (var type in types.Where(type => type is WorkMediaType.Movie or WorkMediaType.Series))
        {
            plans.Add(Row(type, DiscoveryShelfKind.NewlyPublished, DiscoveryMode.New));
            plans.Add(Row(type, DiscoveryShelfKind.Upcoming, DiscoveryMode.Upcoming));
        }

        if (types.Contains(WorkMediaType.Book))
        {
            plans.Add(Row(WorkMediaType.Book, DiscoveryShelfKind.NewlyPublished, DiscoveryMode.New));
        }

        return plans;
    }

    /// <summary>
    /// Presents Books and Light Novels as the one Discover scope users select, while preserving their
    /// canonical media types on every item. Trending/Top rows are interleaved; the Books-only New row
    /// stays separate because Light Novels do not currently have an honest recent-publication feed.
    /// </summary>
    public static IReadOnlyList<DiscoveryShelfRow> CombineBooksAndLightNovels(
        IReadOnlyList<DiscoveryShelfRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new List<DiscoveryShelfRow>(rows.Count);
        var consumed = new HashSet<int>();

        for (var index = 0; index < rows.Count; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            var row = rows[index];
            if (row.Kind == DiscoveryShelfKind.NewlyPublished
                || row.Category is not (DiscoveryCategory.Book or DiscoveryCategory.LightNovel))
            {
                result.Add(row);
                continue;
            }

            var matching = rows
                .Select((candidate, candidateIndex) => (candidate, candidateIndex))
                .Where(entry =>
                    !consumed.Contains(entry.candidateIndex)
                    && entry.candidate.Kind == row.Kind
                    && entry.candidate.Mode == row.Mode
                    && string.Equals(entry.candidate.Genre, row.Genre, StringComparison.Ordinal)
                    && entry.candidate.Category is DiscoveryCategory.Book or DiscoveryCategory.LightNovel)
                .ToArray();

            foreach (var entry in matching)
            {
                consumed.Add(entry.candidateIndex);
            }

            result.Add(new DiscoveryShelfRow(
                $"{ModeSlug(row.Mode)}-books-light-novels",
                row.Kind,
                MediaType: null,
                DiscoveryCategory.BooksAndLightNovels,
                row.Mode,
                row.Genre,
                row.TitleKey,
                "discover.categories.booksLightNovels",
                Deduplicate(Interleave(
                    matching.Select(entry => entry.candidate.Items).ToArray()))));
        }

        return result;
    }

    private static IReadOnlyList<DiscoveryItem> Interleave(
        IReadOnlyList<DiscoveryItem>[] groups)
    {
        var result = new List<DiscoveryItem>();
        for (var index = 0; ; index++)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (index >= group.Count)
                {
                    continue;
                }

                result.Add(group[index]);
                added = true;
            }

            if (!added)
            {
                return result;
            }
        }
    }

    private static string ModeSlug(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "top",
        DiscoveryMode.New => "new",
        DiscoveryMode.Upcoming => "upcoming",
        _ => "trending"
    };

    /// <summary>
    /// De-duplicates a row's items by their media-core identity (normalised media type + provider +
    /// external id), so the same work surfaced by more than one source appears once, first occurrence
    /// wins. Items whose identity cannot be resolved fall back to their stable composite id.
    /// </summary>
    public static IReadOnlyList<DiscoveryItem> Deduplicate(IEnumerable<DiscoveryItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveryItem>();
        foreach (var item in items)
        {
            if (seen.Add(IdentityKey(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>The media-core identity key of a discovery item, used for cross-source de-duplication.</summary>
    public static string IdentityKey(DiscoveryItem item) =>
        WatchlistDraftInput.TryIdentity(item.Category, item.Provider, item.ExternalId, out var identity)
            ? identity.Key
            : item.Id;

    private static bool SourceEnabled(WorkMediaType type, bool includeAniList, bool includeBooks) =>
        type switch
        {
            WorkMediaType.Book => includeBooks,
            WorkMediaType.Movie or WorkMediaType.Series => true,
            _ => includeAniList
        };

    private static DiscoveryShelfPlan Row(WorkMediaType type, DiscoveryShelfKind kind, DiscoveryMode mode)
    {
        var category = Category(type);
        return new DiscoveryShelfPlan(
            $"{KindSlug(kind)}-{WorkMediaTypes.ToStorage(type).ToLowerInvariant()}",
            kind,
            type,
            category,
            mode,
            TitleKey(mode),
            MediaLabelKey(type));
    }

    private static DiscoveryCategory Category(WorkMediaType type) => type switch
    {
        WorkMediaType.Anime => DiscoveryCategory.Anime,
        WorkMediaType.Movie => DiscoveryCategory.Movie,
        WorkMediaType.Series => DiscoveryCategory.Series,
        WorkMediaType.Manga => DiscoveryCategory.Manga,
        WorkMediaType.LightNovel => DiscoveryCategory.LightNovel,
        WorkMediaType.Book => DiscoveryCategory.Book,
        _ => DiscoveryCategory.All
    };

    private static string KindSlug(DiscoveryShelfKind kind) => kind switch
    {
        DiscoveryShelfKind.Trending => "trending",
        DiscoveryShelfKind.Top => "top",
        DiscoveryShelfKind.NewlyPublished => "new",
        DiscoveryShelfKind.Upcoming => "upcoming",
        _ => kind.ToString().ToLowerInvariant()
    };

    // Reuses the existing browse-tab and category catalog keys so no duplicate copy is introduced.
    private static string TitleKey(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Top => "discover.tabs.top",
        DiscoveryMode.New => "discover.tabs.new",
        DiscoveryMode.Upcoming => "discover.tabs.upcoming",
        _ => "discover.tabs.trending"
    };

    private static string MediaLabelKey(WorkMediaType type) => type switch
    {
        WorkMediaType.Anime => "discover.categories.anime",
        WorkMediaType.Movie => "search.type.movie",
        WorkMediaType.Series => "search.type.series",
        WorkMediaType.Manga => "reading.manga.title",
        WorkMediaType.LightNovel => "discover.categories.lightNovel",
        WorkMediaType.Book => "nav.books",
        _ => "discover.categories.all"
    };
}

/// <summary>
/// Assembles the provider-driven discovery board (#595): the rows a profile may see for the media types
/// it may browse, each fetched through the shared <see cref="IDiscoveryFeed"/> (which already applies the
/// TTL cache, local-state overlay and dedupe), identity-deduplicated across sources and cached as a whole
/// so a landing paint is not re-orchestrated on every visit. This is the reusable shelf surface #427,
/// #428 and #434 build on.
/// </summary>
public sealed class DiscoveryShelfService(IDiscoveryFeed feed, IAppShellService shell)
{
    private static readonly ConcurrentDictionary<string, BoardCacheEntry> Cache =
        new(StringComparer.Ordinal);

    private static readonly TimeSpan BoardLifetime = TimeSpan.FromMinutes(2);

    public async Task<DiscoveryShelfBoard> GetBoardAsync(
        ClaimsPrincipal? user,
        string profileId,
        bool isOwner,
        bool includeAniList,
        bool includeBooks,
        CancellationToken cancellationToken)
    {
        var access = await shell.GetMediaAccessAsync(user, cancellationToken);
        var plans = DiscoveryShelfComposer.Plan(access.VisibleMediaTypes, includeAniList, includeBooks);
        if (plans.Count == 0)
        {
            return DiscoveryShelfBoard.Empty;
        }

        var cacheKey = BuildCacheKey(profileId, isOwner, plans);
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var warnings = new List<string>();
        var rows = new List<DiscoveryShelfRow>(plans.Count);

        // Sequential on purpose: the feed's coordinator uses one scoped DbContext for its local-state
        // overlay, which must not be touched concurrently. Each request is individually TTL-cached, so
        // repeat visits are cheap even without parallelism.
        foreach (var plan in plans)
        {
            var response = await feed.GetAsync(
                new DiscoveryRequest("", plan.Category, plan.Mode),
                profileId,
                isOwner,
                plan.UsesAniList,
                plan.UsesBooks,
                cancellationToken);

            warnings.AddRange(response.Warnings);

            var items = DiscoveryShelfComposer.Deduplicate(response.Items);
            if (items.Count == 0)
            {
                // An empty row would be redundant chrome; skip it rather than render a hollow shelf.
                continue;
            }

            rows.Add(new DiscoveryShelfRow(
                plan.Id,
                plan.Kind,
                plan.MediaType,
                plan.Category,
                plan.Mode,
                "",
                plan.TitleKey,
                plan.MediaLabelKey,
                items));
        }

        var board = new DiscoveryShelfBoard(
            rows,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
        PutCached(cacheKey, board);
        return board;
    }

    public static void InvalidateCache() => Cache.Clear();

    private static string BuildCacheKey(
        string profileId,
        bool isOwner,
        IReadOnlyList<DiscoveryShelfPlan> plans) =>
        string.Join(
            '|',
            profileId,
            isOwner ? "owner" : "user",
            string.Join(',', plans.Select(plan => plan.Id)));

    private static bool TryGetCached(string key, out DiscoveryShelfBoard board)
    {
        if (Cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                board = entry.Board;
                return true;
            }

            Cache.TryRemove(key, out _);
        }

        board = null!;
        return false;
    }

    private static void PutCached(string key, DiscoveryShelfBoard board)
    {
        if (Cache.Count > 128)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in Cache)
            {
                if (item.Value.ExpiresAt <= now)
                {
                    Cache.TryRemove(item.Key, out _);
                }
            }
        }

        Cache[key] = new BoardCacheEntry(board, DateTimeOffset.UtcNow.Add(BoardLifetime));
    }

    private sealed record BoardCacheEntry(DiscoveryShelfBoard Board, DateTimeOffset ExpiresAt);
}
