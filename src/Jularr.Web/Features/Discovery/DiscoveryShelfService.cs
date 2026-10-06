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
    string MediaLabelKey);

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
    public static IReadOnlyList<DiscoveryShelfPlan> Plan(IReadOnlyList<WorkMediaType> visibleMediaTypes)
    {
        var visible = new HashSet<WorkMediaType>(visibleMediaTypes);
        var types = DisplayOrder
            .Where(visible.Contains)
            .Where(SupportedMediaTypes.Contains)
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
                    matching.Select(entry => entry.candidate.Items).ToArray())),
                [.. matching.SelectMany(entry => entry.candidate.Sources).DistinctBy(source => source.Source)]));
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
/// Assembles the provider-driven discovery board (#595): the rows a profile may see for the media types it may browse, loaded through the
/// shared <see cref="IDiscoveryFeed"/> in one call so every row's provider call runs side by side, identity-deduplicated across sources.
/// A row whose source has not answered stays in its place as a pending row; the board never waits for the slowest source beyond the
/// budget it is given. This is the reusable shelf surface #427, #428 and #434 build on.
/// </summary>
public sealed class DiscoveryShelfService(IDiscoveryFeed feed, IAppShellService shell)
{
    /// <param name="scope">The media types the viewer picked: only their rows are planned, so no other source is called or counted.</param>
    public async Task<DiscoveryShelfBoard> GetBoardAsync(ClaimsPrincipal? user, string profileId, bool isOwner, DiscoveryCategory scope, DiscoveryWait wait, CancellationToken cancellationToken)
    {
        var access = await shell.GetMediaAccessAsync(user, cancellationToken);
        var plans = DiscoveryShelfComposer.Plan(access.VisibleMediaTypes).Where(plan => DiscoverScopes.Includes(scope, plan.Category)).ToArray();
        if (plans.Length == 0)
        {
            return DiscoveryShelfBoard.Empty;
        }

        var audience = new DiscoveryAudience(profileId, isOwner, access.VisibleMediaTypes.ToHashSet());
        var load = await feed.LoadAsync([.. plans.Select(plan => new DiscoveryRequest("", plan.Category, plan.Mode))], audience, wait, cancellationToken);
        var overlay = await feed.OverlayLocalStateAsync(load.Batches.SelectMany(batch => batch.Items), profileId, cancellationToken);

        var rows = new List<DiscoveryShelfRow>(plans.Length);
        for (var index = 0; index < plans.Length; index++)
        {
            var plan = plans[index];
            var batch = load.Batches[index];
            var items = DiscoveryShelfComposer.Deduplicate(batch.Items.Select(item => overlay[item.Id]));
            var row = new DiscoveryShelfRow(plan.Id, plan.Kind, plan.MediaType, plan.Category, plan.Mode, "", plan.TitleKey, plan.MediaLabelKey, items, batch.Sources);

            // A row that every source answered with nothing is redundant chrome; a row that waits or failed keeps its place.
            if (row.State != DiscoverySectionState.Empty)
            {
                rows.Add(row);
            }
        }

        return new DiscoveryShelfBoard(rows, load.Settled, load.Pending);
    }
}
