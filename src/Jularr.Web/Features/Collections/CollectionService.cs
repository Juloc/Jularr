using System.Collections.Concurrent;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Collections;

/// <summary>A collection in a profile's list: identity, kind and how many works it currently holds.</summary>
public sealed record CollectionSummaryView(
    Guid Id,
    CollectionKind Kind,
    string Name,
    string? Description,
    int ItemCount,
    DateTime? LastMaterializedAt);

/// <summary>A collection as a Library tile: name, kind, how many works the profile can browse and up to four poster URLs.</summary>
public sealed record CollectionTileView(
    Guid Id,
    CollectionKind Kind,
    string Name,
    int ItemCount,
    IReadOnlyList<string> PosterUrls);

/// <summary>One card of a collection shelf plus, for a smart collection, the reasons the work matched.</summary>
public sealed record CollectionCardView(long WorkId, MediaBannerCardModel Card, IReadOnlyList<string> Reasons);

/// <summary>The full detail of one collection: its metadata, the rendered shelf and per-item explainability.</summary>
public sealed record CollectionDetailView(
    Guid Id,
    CollectionKind Kind,
    string Name,
    string? Description,
    string? RuleJson,
    DateTime? LastMaterializedAt,
    MediaShelfModel Shelf,
    IReadOnlyList<CollectionCardView> Items);

/// <summary>
/// Orchestrates collections (#427): CRUD over manual and smart collections, materialization of smart
/// membership from canonical media facts, and rendering the result with the shared media shelf/banner card.
/// Reads are capability-filtered (a work whose media type a profile may not browse never appears) and the
/// assembled detail board is TTL-cached; any write invalidates the cache, and changing a rule forces the
/// next read to re-materialize so a smart collection never shows a stale membership.
/// </summary>
public sealed class CollectionService(
    CollectionStore store,
    CollectionFactsProvider facts,
    AppDbContext db,
    IAppShellService shell)
{
    private static readonly ConcurrentDictionary<string, BoardCacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan BoardLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Posters in a collection tile's mosaic.</summary>
    public const int MosaicSize = 4;

    private const int MosaicCandidates = 12;

    public async Task<IReadOnlyList<CollectionSummaryView>> ListAsync(string profileId, CancellationToken cancellationToken)
    {
        var collections = await store.ListAsync(profileId, cancellationToken);
        if (collections.Count == 0)
        {
            return [];
        }

        var ids = collections.Select(x => x.Id).ToList();
        var counts = await db.CollectionItems.AsNoTracking()
            .Where(x => ids.Contains(x.CollectionId))
            .GroupBy(x => x.CollectionId)
            .Select(g => new { CollectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CollectionId, x => x.Count, cancellationToken);

        return
        [
            .. collections.Select(x => new CollectionSummaryView(
                x.Id,
                x.Kind,
                x.Name,
                x.Description,
                counts.GetValueOrDefault(x.Id),
                x.LastMaterializedAt))
        ];
    }

    /// <summary>
    /// The collections of a profile as Library tiles: the count of works the profile may browse and up to
    /// <see cref="MosaicSize"/> poster URLs taken from the first works that have artwork. Only anime works
    /// carry poster artwork today; other media fall back to the tile placeholder.
    /// </summary>
    public async Task<IReadOnlyList<CollectionTileView>> ListTilesAsync(
        ClaimsPrincipal? user,
        string profileId,
        CancellationToken cancellationToken)
    {
        var collections = await store.ListAsync(profileId, cancellationToken);
        if (collections.Count == 0)
        {
            return [];
        }

        var access = await shell.GetMediaAccessAsync(user, cancellationToken);
        var ids = collections.Select(x => x.Id).ToList();
        var items = await (
            from item in db.CollectionItems.AsNoTracking()
            join work in db.Works.AsNoTracking() on item.WorkId equals work.Id
            where ids.Contains(item.CollectionId)
            orderby item.Position, work.CanonicalTitle
            select new { item.CollectionId, item.WorkId, work.MediaType })
            .ToListAsync(cancellationToken);

        var visible = items
            .Where(x => access.IsVisible(x.MediaType))
            .GroupBy(x => x.CollectionId)
            .ToDictionary(group => group.Key, group => group.Select(x => x.WorkId).ToList());

        // Poster candidates: a handful of works per collection is enough to fill four tiles.
        var candidates = visible.Values.SelectMany(works => works.Take(MosaicCandidates)).Distinct().ToList();
        var posters = await ResolvePostersAsync(candidates, cancellationToken);

        return
        [
            .. collections.Select(collection =>
            {
                var works = visible.GetValueOrDefault(collection.Id) ?? [];
                return new CollectionTileView(
                    collection.Id,
                    collection.Kind,
                    collection.Name,
                    works.Count,
                    [
                        .. works.Take(MosaicCandidates)
                            .Select(work => posters.GetValueOrDefault(work))
                            .Where(url => !string.IsNullOrWhiteSpace(url))
                            .Take(MosaicSize)
                            .Select(url => url!)
                    ]);
            })
        ];
    }

    private async Task<IReadOnlyDictionary<long, string?>> ResolvePostersAsync(
        IReadOnlyCollection<long> workIds,
        CancellationToken cancellationToken)
    {
        if (workIds.Count == 0)
        {
            return new Dictionary<long, string?>();
        }

        var covers = await (
            from link in db.WorkSourceLinks.AsNoTracking()
            join metadata in db.AnimeMetadata.AsNoTracking() on link.SourceId equals metadata.AnimeId
            where workIds.Contains(link.WorkId) && link.SourceKind == WorkSourceKind.Anime
            select new { link.WorkId, link.SourceId, metadata.CoverImageUrl })
            .ToListAsync(cancellationToken);

        return covers
            .GroupBy(x => x.WorkId)
            .ToDictionary(
                group => group.Key,
                group => AnimeArtworkStore.ResolvePosterUrl(group.First().SourceId, group.First().CoverImageUrl));
    }

    public Task<Collection?> GetAsync(string profileId, Guid id, CancellationToken cancellationToken) =>
        store.GetAsync(profileId, id, cancellationToken);

    public async Task<Collection> CreateAsync(
        string profileId,
        CollectionKind kind,
        string name,
        string? description,
        CollectionRuleNode? rule,
        CancellationToken cancellationToken)
    {
        var collection = await store.CreateAsync(new Collection
        {
            ProfileId = profileId,
            Kind = kind,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            RuleJson = kind == CollectionKind.Smart && rule is not null
                ? CollectionRuleSerializer.Serialize(rule)
                : null
        }, cancellationToken);
        InvalidateCache();
        return collection;
    }

    public async Task<bool> UpdateAsync(
        string profileId,
        Guid id,
        string name,
        string? description,
        CollectionRuleNode? rule,
        CancellationToken cancellationToken)
    {
        var ruleJson = rule is not null ? CollectionRuleSerializer.Serialize(rule) : null;
        var updated = await store.UpdateAsync(profileId, id, name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(), ruleJson, cancellationToken);
        InvalidateCache();
        return updated;
    }

    public async Task<bool> DeleteAsync(string profileId, Guid id, CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(profileId, id, cancellationToken);
        InvalidateCache();
        return deleted;
    }

    public async Task<bool> AddManualItemAsync(string profileId, Guid id, long workId, CancellationToken cancellationToken)
    {
        var collection = await store.GetAsync(profileId, id, cancellationToken);
        if (collection is null || collection.Kind != CollectionKind.Manual)
        {
            return false;
        }

        var added = await store.AddManualItemAsync(id, workId, cancellationToken);
        InvalidateCache();
        return added;
    }

    public async Task<bool> RemoveItemAsync(string profileId, Guid id, long workId, CancellationToken cancellationToken)
    {
        var collection = await store.GetAsync(profileId, id, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        var removed = await store.RemoveItemAsync(id, workId, cancellationToken);
        InvalidateCache();
        return removed;
    }

    /// <summary>
    /// Recomputes a smart collection's membership from current canonical facts and persists it (#427).
    /// Returns the number of matched works, or null when the collection does not exist, is not the caller's,
    /// or is not a smart collection.
    /// </summary>
    public async Task<int?> MaterializeAsync(string profileId, Guid id, CancellationToken cancellationToken)
    {
        var collection = await store.GetAsync(profileId, id, cancellationToken);
        if (collection is null || collection.Kind != CollectionKind.Smart)
        {
            return null;
        }

        var count = await MaterializeInternalAsync(collection, cancellationToken);
        InvalidateCache();
        return count;
    }

    private async Task<int> MaterializeInternalAsync(Collection collection, CancellationToken cancellationToken)
    {
        var rule = CollectionRuleSerializer.Deserialize(collection.RuleJson);
        var snapshots = await facts.BuildAllAsync(cancellationToken);
        var members = SmartCollectionMaterializer.Match(rule, snapshots);
        await store.ReplaceSmartItemsAsync(
            collection.Id,
            [.. members.Select(m => (m.Work.WorkId, string.Join('\n', m.Reasons)))],
            DateTime.UtcNow,
            cancellationToken);
        return members.Count;
    }

    /// <summary>
    /// The rendered detail of one collection: a smart collection with no current materialization (new or
    /// with a just-changed rule) is materialized first, then the stored membership is rendered with the
    /// shared banner card, capability-filtered to the media types the profile may browse.
    /// </summary>
    public async Task<CollectionDetailView?> GetDetailAsync(
        ClaimsPrincipal? user,
        string profileId,
        Guid id,
        UiTextBundle ui,
        CancellationToken cancellationToken)
    {
        var collection = await store.GetAsync(profileId, id, cancellationToken);
        if (collection is null)
        {
            return null;
        }

        if (collection.Kind == CollectionKind.Smart && collection.LastMaterializedAt is null)
        {
            await MaterializeInternalAsync(collection, cancellationToken);
            collection = await store.GetAsync(profileId, id, cancellationToken) ?? collection;
        }

        var access = await shell.GetMediaAccessAsync(user, cancellationToken);
        var visibleKey = string.Join(',', access.VisibleMediaTypes.Select(t => (int)t));
        var cacheKey = $"{profileId}|{id}|{visibleKey}";
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var items = await store.GetItemsAsync(id, cancellationToken);
        var workIds = items.Select(x => x.WorkId).ToList();
        var snapshots = (await facts.BuildForWorksAsync(workIds, cancellationToken))
            .ToDictionary(x => x.WorkId);
        var hrefs = await ResolveHrefsAsync(workIds, cancellationToken);

        var cards = new List<CollectionCardView>();
        foreach (var item in items)
        {
            if (!snapshots.TryGetValue(item.WorkId, out var snapshot) || !access.IsVisible(snapshot.MediaType))
            {
                // Capability filtering: a work whose media type the profile may not browse is omitted.
                continue;
            }

            var card = MediaBannerCardModel.Create(ToCardData(snapshot, hrefs), ui);
            cards.Add(new CollectionCardView(item.WorkId, card, ParseReasons(item.MatchReason)));
        }

        var shelf = new MediaShelfModel(
            $"collection-{id}",
            collection.Name,
            null,
            null,
            [.. cards.Select(x => x.Card)]);

        var view = new CollectionDetailView(
            collection.Id,
            collection.Kind,
            collection.Name,
            collection.Description,
            collection.RuleJson,
            collection.LastMaterializedAt,
            shelf,
            cards);
        PutCached(cacheKey, view);
        return view;
    }

    private async Task<IReadOnlyDictionary<long, string>> ResolveHrefsAsync(
        IReadOnlyList<long> workIds,
        CancellationToken cancellationToken)
    {
        if (workIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        var links = await db.WorkSourceLinks.AsNoTracking()
            .Where(x => workIds.Contains(x.WorkId))
            .Select(x => new { x.WorkId, x.SourceKind, x.SourceId })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<long, string>();
        foreach (var link in links)
        {
            if (!map.ContainsKey(link.WorkId) &&
                CollectionWorkLinks.Resolve(link.SourceKind, link.SourceId) is { } href)
            {
                map[link.WorkId] = href;
            }
        }

        return map;
    }

    private static MediaBannerCardData ToCardData(WorkFactSnapshot snapshot, IReadOnlyDictionary<long, string> hrefs) =>
        new(
            snapshot.BannerKind ?? MediaBannerKind.Book,
            snapshot.Title,
            hrefs.GetValueOrDefault(snapshot.WorkId, "#"),
            ProviderStatus: ToProviderStatus(snapshot.Status),
            Year: snapshot.Year,
            GroupCount: snapshot.SecondaryUnitCount);

    /// <summary>Reverses <see cref="MediaBannerCardModel.MapStatus"/> so the shared card can render the badge.</summary>
    private static string? ToProviderStatus(MediaReleaseStatus? status) => status switch
    {
        MediaReleaseStatus.Ongoing => "RELEASING",
        MediaReleaseStatus.Finished => "FINISHED",
        MediaReleaseStatus.Upcoming => "NOT_YET_RELEASED",
        MediaReleaseStatus.Hiatus => "HIATUS",
        MediaReleaseStatus.Cancelled => "CANCELLED",
        _ => null
    };

    private static IReadOnlyList<string> ParseReasons(string? matchReason) =>
        string.IsNullOrWhiteSpace(matchReason)
            ? []
            : matchReason.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void InvalidateCache() => Cache.Clear();

    private static bool TryGetCached(string key, out CollectionDetailView view)
    {
        if (Cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            view = entry.View;
            return true;
        }

        view = null!;
        return false;
    }

    private static void PutCached(string key, CollectionDetailView view)
    {
        if (Cache.Count > 256)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in Cache)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    Cache.TryRemove(pair.Key, out _);
                }
            }
        }

        Cache[key] = new BoardCacheEntry(view, DateTimeOffset.UtcNow.Add(BoardLifetime));
    }

    private sealed record BoardCacheEntry(CollectionDetailView View, DateTimeOffset ExpiresAt);
}
