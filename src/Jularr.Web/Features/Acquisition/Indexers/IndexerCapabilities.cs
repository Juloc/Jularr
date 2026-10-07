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
    int[]? Categories = null)
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
        int? maximum = int.TryParse(limits?.Attribute("max")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
        var categories = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "categories")?.Descendants()
            .Where(element => element.Name.LocalName is "category" or "subcat")
            .Select(element => int.TryParse(element.Attribute("id")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();
        return new IndexerCapabilities(now, modes, maximum, categories is { Length: > 0 } ? categories : null);
    }
}
