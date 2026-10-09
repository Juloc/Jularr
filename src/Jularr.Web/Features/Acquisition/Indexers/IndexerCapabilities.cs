using System.Globalization;
using System.Xml.Linq;

namespace Jularr.Web.Features.Acquisition.Indexers;

/// <summary>A Newznab search function (the <c>t=</c> parameter); each one accepts its own set of parameters.</summary>
public enum IndexerSearchMode
{
    Search,
    TvSearch,
    Movie,
    Book,
    Music
}

/// <summary>
/// What an indexer reported it can search with, read from its <c>caps</c> document when it is tested or refreshed. The planner only
/// builds a structured query when the indexer advertised the function and the parameter, so a parameter is never sent merely
/// because another indexer supports it.
/// </summary>
public sealed record IndexerCapabilities(
    DateTimeOffset RefreshedAt,
    IReadOnlyDictionary<IndexerSearchMode, string[]> Modes,
    int? MaximumLimit = null,
    int[]? Categories = null,
    IndexerCategory[]? CategoryTree = null,
    string? ServerTitle = null,
    int? DefaultLimit = null)
{
    /// <summary>
    /// Whether the indexer offers a category, itself or through its parent or a sub category of the same parent (Newznab numbers a parent as a multiple
    /// of 1000). True when its categories were never read, so an entry that was tested before they were stored keeps searching.
    /// </summary>
    public bool Offers(int category) =>
        Categories is not { Length: > 0 }
        || Categories.Contains(category)
        || (category % 1000 == 0 ? Categories.Any(offered => offered / 1000 == category / 1000) : category < 100000 && Categories.Contains(category / 1000 * 1000));

    /// <summary>The text search every Newznab indexer answers; used for indexers whose caps were never read.</summary>
    public static IndexerCapabilities TextOnly(DateTimeOffset refreshedAt) =>
        new(refreshedAt, new Dictionary<IndexerSearchMode, string[]> { [IndexerSearchMode.Search] = ["q"] });

    public bool Supports(IndexerSearchMode mode) => Modes.ContainsKey(mode);

    public bool Supports(IndexerSearchMode mode, string parameter) =>
        Modes.TryGetValue(mode, out var parameters) && parameters.Contains(parameter, StringComparer.OrdinalIgnoreCase);

    /// <summary>The parameters of a function that have to be present in the caps for <paramref name="wanted"/> to be sent.</summary>
    public IReadOnlyList<string> Filter(IndexerSearchMode mode, IEnumerable<string> wanted) =>
        [.. wanted.Where(parameter => Supports(mode, parameter))];

    /// <summary>The one-line summary Admin shows, in human terms, for example <c>TV ID ✓ · Season/Episode ✓ · Book ✓</c>.</summary>
    public string Describe()
    {
        var facts = new List<string>();
        if (Supports(IndexerSearchMode.TvSearch))
        {
            if (Supports(IndexerSearchMode.TvSearch, "tvdbid") || Supports(IndexerSearchMode.TvSearch, "tmdbid") || Supports(IndexerSearchMode.TvSearch, "rid"))
            {
                facts.Add("TV ID ✓");
            }

            if (Supports(IndexerSearchMode.TvSearch, "season") && Supports(IndexerSearchMode.TvSearch, "ep"))
            {
                facts.Add("Season/Episode ✓");
            }
        }

        if (Supports(IndexerSearchMode.Movie))
        {
            facts.Add(Supports(IndexerSearchMode.Movie, "imdbid") || Supports(IndexerSearchMode.Movie, "tmdbid") ? "Movie ID ✓" : "Movie ✓");
        }

        if (Supports(IndexerSearchMode.Book))
        {
            facts.Add("Book ✓");
        }

        if (Supports(IndexerSearchMode.Music))
        {
            facts.Add("Music ✓");
        }

        return facts.Count == 0 ? "Text search" : string.Join(" · ", facts);
    }
}

/// <summary>One category the caps document lists, with the parent it was listed under; null for a top-level category.</summary>
public sealed record IndexerCategory(int Id, string? Name, int? ParentId);

/// <summary>Reads a Newznab <c>caps</c> document into <see cref="IndexerCapabilities"/>.</summary>
public static class NewznabCapsParser
{
    private static readonly (string Element, IndexerSearchMode Mode)[] Functions =
    [
        ("search", IndexerSearchMode.Search),
        ("tv-search", IndexerSearchMode.TvSearch),
        ("movie-search", IndexerSearchMode.Movie),
        ("book-search", IndexerSearchMode.Book),
        ("audio-search", IndexerSearchMode.Music)
    ];

    public static IndexerCapabilities Parse(XDocument document, DateTimeOffset now)
    {
        var searching = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "searching");
        var modes = new Dictionary<IndexerSearchMode, string[]>();
        foreach (var (name, mode) in Functions)
        {
            var element = searching?.Elements().FirstOrDefault(candidate => candidate.Name.LocalName == name);
            if (element is null || !string.Equals(element.Attribute("available")?.Value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            modes[mode] = (element.Attribute("supportedParams")?.Value ?? "q")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(parameter => parameter.ToLowerInvariant())
                .Distinct()
                .ToArray();
        }

        // A server that lists no function still answers the plain text search.
        if (!modes.ContainsKey(IndexerSearchMode.Search))
        {
            modes[IndexerSearchMode.Search] = ["q"];
        }

        var limits = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "limits");
        int? maximum = PositiveInt(limits?.Attribute("max")?.Value);
        var tree = new List<IndexerCategory>();
        foreach (var category in document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "categories")?.Elements().Where(element => element.Name.LocalName == "category") ?? [])
        {
            if (PositiveInt(category.Attribute("id")?.Value) is not { } parent)
            {
                continue;
            }

            tree.Add(new IndexerCategory(parent, category.Attribute("name")?.Value, null));
            tree.AddRange(category.Elements()
                .Where(element => element.Name.LocalName == "subcat")
                .Select(element => (Id: PositiveInt(element.Attribute("id")?.Value), element.Attribute("name")?.Value))
                .Where(item => item.Id is not null)
                .Select(item => new IndexerCategory(item.Id!.Value, item.Value, parent)));
        }

        var ids = tree.Select(category => category.Id).Distinct().Order().ToArray();
        var title = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "server")?.Attribute("title")?.Value?.Trim();
        return new IndexerCapabilities(now, modes, maximum, ids.Length > 0 ? ids : null, tree.Count > 0 ? [.. tree] : null, string.IsNullOrEmpty(title) ? null : title, PositiveInt(limits?.Attribute("default")?.Value));
    }

    private static int? PositiveInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
}
