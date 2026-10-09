using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>Where the categories of a media type on one indexer come from.</summary>
public enum IndexerCategoryEvidence
{
    /// <summary>The owner chose them.</summary>
    Owner,

    /// <summary>The indexer's own category names say so, including custom categories.</summary>
    Name,

    /// <summary>The standard Newznab taxonomy, and the indexer lists the category.</summary>
    Standard,

    /// <summary>No category of its own exists; the media type shares another type's category and the owner should verify it.</summary>
    Shared,

    /// <summary>The indexer's caps were never read, so the standard default is only an assumption.</summary>
    Assumed,

    /// <summary>The indexer lists categories and none of them belongs to this media type.</summary>
    Unavailable
}

public sealed record IndexerKindCategories(MediaAcquisitionKind Kind, int[] Categories, IndexerCategoryEvidence Evidence)
{
    public bool IsAvailable => Evidence != IndexerCategoryEvidence.Unavailable && Categories.Length > 0;
}

/// <summary>
/// The one place that decides which categories of an indexer belong to which media type. The indexer's own caps decide: category names (also
/// custom ones) first, then the standard Newznab numbers, and only for categories the indexer really lists. Nothing is guessed: a media type
/// without a matching category is unavailable on that indexer instead of being searched in an unrelated section.
/// </summary>
public static partial class IndexerCategoryMapper
{
    private const int CustomCategoryFloor = 100000;

    private static readonly (MediaAcquisitionKind Kind, string[] Words)[] NameWords =
    [
        (MediaAcquisitionKind.Audiobook, ["audiobook", "audiobooks", "spoken", "hörbuch", "hoerbuch"]),
        (MediaAcquisitionKind.LightNovel, ["lightnovel", "lightnovels", "ranobe"]),
        (MediaAcquisitionKind.Manga, ["manga", "mangas", "comic", "comics", "manhwa", "manhua"]),
        (MediaAcquisitionKind.Anime, ["anime"]),
        (MediaAcquisitionKind.Movie, ["movie", "movies", "film", "films"]),
        (MediaAcquisitionKind.Tv, ["tv", "series", "television"]),
        (MediaAcquisitionKind.Music, ["music", "audio", "flac", "mp3", "lossless"]),
        (MediaAcquisitionKind.Book, ["book", "books", "ebook", "ebooks", "epub", "magazines"])
    ];

    /// <summary>The standard taxonomy by id. A parent id (a multiple of 1000) stands for every sub category that is not claimed by a more specific media type.</summary>
    private static MediaAcquisitionKind? StandardKind(int id) => id switch
    {
        >= 2000 and < 3000 => MediaAcquisitionKind.Movie,
        3030 => MediaAcquisitionKind.Audiobook,
        >= 3000 and < 4000 => MediaAcquisitionKind.Music,
        5070 => MediaAcquisitionKind.Anime,
        >= 5000 and < 6000 => MediaAcquisitionKind.Tv,
        7030 => MediaAcquisitionKind.Manga,
        7000 or 7020 => MediaAcquisitionKind.Book,
        _ => null
    };

    /// <summary>The standard categories of a type when no caps are known: what the indexer is asked for until it was tested.</summary>
    public static int[] StandardDefault(MediaAcquisitionKind kind, IndexerSettings settings) => kind switch
    {
        MediaAcquisitionKind.Anime => settings.Categories,
        MediaAcquisitionKind.Movie => [2000],
        MediaAcquisitionKind.Tv => [5000],
        MediaAcquisitionKind.Book => settings.EffectiveBookCategories,
        MediaAcquisitionKind.LightNovel => [.. settings.EffectiveBookCategories.Concat([7020, 7000]).Distinct()],
        MediaAcquisitionKind.Manga => [.. settings.EffectiveBookCategories.Concat([7030, 7000]).Distinct()],
        MediaAcquisitionKind.Audiobook => [3030, 3000],
        MediaAcquisitionKind.Music => [3000, 3010, 3040],
        _ => []
    };

    /// <summary>The categories of every media type the indexer's caps allow; a type without any is listed as unavailable.</summary>
    public static IReadOnlyList<IndexerKindCategories> Derive(IndexerCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var nodes = NodesOf(capabilities);
        var byId = nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        var classified = nodes.Select(node => (Node: node, Match: Classify(node, byId))).Where(item => item.Match is not null).Select(item => (item.Node, Match: item.Match!.Value)).ToList();

        // A section the indexer lists without any sub category stands for the standard sub categories too (an Anime or Comics section inside it).
        foreach (var parent in nodes.Where(node => node.ParentId is null && node.Id < CustomCategoryFloor && !nodes.Any(other => other.ParentId == node.Id)))
        {
            foreach (var implicitId in new[] { 3030, 5070, 7020, 7030 }.Where(id => id / 1000 * 1000 == parent.Id && !byId.ContainsKey(id)))
            {
                classified.Add((new IndexerCategory(implicitId, null, parent.Id), (StandardKind(implicitId)!.Value, false)));
            }
        }

        var results = new List<IndexerKindCategories>();
        foreach (var kind in Enum.GetValues<MediaAcquisitionKind>())
        {
            var matches = classified.Where(item => item.Match.Kind == kind).ToArray();
            var ids = new SortedSet<int>();
            foreach (var (node, _) in matches)
            {
                var children = node.ParentId is null ? nodes.Where(other => other.ParentId == node.Id).ToArray() : [];
                if (children.Length == 0 || children.All(child => classified.Any(item => item.Node.Id == child.Id && item.Match.Kind == kind)))
                {
                    ids.Add(node.Id);
                }
            }

            ids.RemoveWhere(id => nodes.Any(node => node.Id == id && node.ParentId is { } parent && ids.Contains(parent)));
            if (ids.Count == 0)
            {
                results.Add(new IndexerKindCategories(kind, [], IndexerCategoryEvidence.Unavailable));
                continue;
            }

            var named = matches.Any(item => item.Match.FromName && ids.Contains(item.Node.Id));
            results.Add(new IndexerKindCategories(kind, [.. ids], named ? IndexerCategoryEvidence.Name : IndexerCategoryEvidence.Standard));
        }

        // Light novels have no standard section; where the indexer has Books but no named light novel category they share it, marked as such.
        var books = results.First(item => item.Kind == MediaAcquisitionKind.Book);
        var novel = results.FindIndex(item => item.Kind == MediaAcquisitionKind.LightNovel);
        if (!results[novel].IsAvailable && books.IsAvailable)
        {
            results[novel] = new IndexerKindCategories(MediaAcquisitionKind.LightNovel, books.Categories, IndexerCategoryEvidence.Shared);
        }

        return results;
    }

    private static IndexerCategory[] NodesOf(IndexerCapabilities capabilities)
    {
        if (capabilities.CategoryTree is { Length: > 0 } tree)
        {
            return tree;
        }

        var ids = capabilities.Categories ?? [];
        return [.. ids.Select(id => new IndexerCategory(id, null, id % 1000 != 0 && id < CustomCategoryFloor && ids.Contains(id / 1000 * 1000) ? id / 1000 * 1000 : null))];
    }

    /// <summary>The categories of one media type on one indexer: the owner's choice, else what the caps allow, else the standard default of an untested indexer.</summary>
    public static IndexerKindCategories Resolve(MediaAcquisitionKind kind, IndexerEntry entry)
    {
        if (entry.Settings.CategoriesFor(kind) is { } chosen)
        {
            return new IndexerKindCategories(kind, chosen, IndexerCategoryEvidence.Owner);
        }

        return entry.Type == IndexerType.Newznab && entry.Settings.Capabilities is { Categories.Length: > 0 } capabilities
            ? Derive(capabilities).First(item => item.Kind == kind)
            : new IndexerKindCategories(kind, StandardDefault(kind, entry.Settings), IndexerCategoryEvidence.Assumed);
    }

    // The category's own name wins, then the standard number, then the parent's meaning for a sub category that says nothing itself.
    private static (MediaAcquisitionKind Kind, bool FromName)? Classify(IndexerCategory node, Dictionary<int, IndexerCategory> byId)
    {
        if (KindByName(node.Name) is { } named)
        {
            return (named, true);
        }

        if (node.Id < CustomCategoryFloor && StandardKind(node.Id) is { } standard)
        {
            return (standard, false);
        }

        return node.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && Classify(parent, byId) is { } inherited && inherited.Kind is not (MediaAcquisitionKind.Book or MediaAcquisitionKind.Music)
            ? (inherited.Kind, inherited.FromName)
            : null;
    }

    private static MediaAcquisitionKind? KindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tokens = Words().Matches(name.ToLowerInvariant()).Select(match => match.Value.Replace(" ", string.Empty, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var squashed = name.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var (kind, words) in NameWords)
        {
            if (words.Any(word => tokens.Contains(word) || (word.Length > 5 && squashed.Contains(word, StringComparison.Ordinal))))
            {
                return kind;
            }
        }

        return null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
