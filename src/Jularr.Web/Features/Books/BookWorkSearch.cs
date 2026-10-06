using Jularr.Web.Features.Acquisition.Search;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Books;

/// <summary>
/// One provider's view of a specific edition of a work (#405): kept when records merge so the
/// Add book dialog can expand a work into what is actually known about its editions, distinct
/// from the single best-metadata row <see cref="BookWorkSearch"/> chooses for the work itself.
/// </summary>
public sealed record BookEditionSummary(
    string Id,
    int? Year,
    string? Language,
    string? Publisher,
    string? Isbn,
    string Format,
    string SourceName,
    string? CoverImageUrl);

/// <summary>
/// The one Books search model (#371, #405): turns provider records (Open Library, Google
/// Books, Wikisource, Gutenberg) into canonical works. Records with identity evidence for the
/// same book merge into one result, the best metadata and cover are chosen across them, and
/// the works are ranked by what the query asks for rather than by provider order.
/// </summary>
public static partial class BookWorkSearch
{
    public const string GoogleBooksSource = "Google Books";

    /// <summary>An edition Jularr can add directly (an EPUB or an importable Wikisource text).</summary>
    public const string EditionFormatEpub = "epub";

    /// <summary>An edition that can be read online but not imported as a file.</summary>
    public const string EditionFormatText = "text";

    /// <summary>A catalog record with no directly usable file: metadata only.</summary>
    public const string EditionFormatListing = "listing";

    private const int CoverMemoryLimit = 4000;

    /// <summary>Common MARC/ISO 639-2 three-letter codes to the two-letter tag Jularr uses elsewhere.</summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageTagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en", ["ind"] = "id", ["deu"] = "de", ["ger"] = "de", ["fra"] = "fr", ["fre"] = "fr",
        ["spa"] = "es", ["ita"] = "it", ["nld"] = "nl", ["dut"] = "nl", ["pol"] = "pl", ["por"] = "pt",
        ["tur"] = "tr", ["jpn"] = "ja", ["kor"] = "ko", ["zho"] = "zh", ["chi"] = "zh", ["rus"] = "ru"
    };

    // Once chosen, a work keeps its cover across searches, so cards do not change pictures.
    private static readonly ConcurrentDictionary<string, string> ChosenCovers = new(StringComparer.Ordinal);

    /// <summary>
    /// Words that mark a companion product (a workbook for a book, a summary of it) when they are
    /// added to the requested title but are not part of the query.
    /// </summary>
    private static readonly HashSet<string> CompanionWords = new(StringComparer.Ordinal)
    {
        "workbook", "journal", "summary", "summaries", "study", "guide", "companion", "analysis",
        "notebook", "planner", "takeaways", "insights", "cliffsnotes", "sparknotes", "quicklet",
        "summarized", "review", "trivia", "quiz", "activity", "coloring", "colouring", "tracker", "logbook"
    };

    /// <summary>Merges the provider lists into canonical works and ranks them for <paramref name="query"/>.</summary>
    public static IReadOnlyList<BookCatalogItem> Rank(
        string query,
        params IReadOnlyList<BookCatalogItem>[] providers)
    {
        var request = new Query(query);
        return Merge(providers)
            .Select(work => (Item: work.ToItem(), Score: request.Score(work), work.FirstOrder))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.FirstOrder)
            .Select(entry => entry.Item)
            .ToArray();
    }

    /// <summary>
    /// One work from two records already known to be the same book (a trending entry and its
    /// matching Google Books edition), merged by the same rules as search results.
    /// </summary>
    public static BookCatalogItem Combine(BookCatalogItem work, BookCatalogItem sameWork)
    {
        var merged = new Work(new Record(work, 0));
        merged.Add(new Record(sameWork, 1));
        return merged.ToItem();
    }

    /// <summary>
    /// Whether two title/author pairs name the same work: equal main titles and overlapping
    /// authors (without an author on either side the full titles must be equal). Similar titles
    /// alone never match.
    /// </summary>
    public static bool SameWork(string title, string? author, string otherTitle, string? otherAuthor)
    {
        var main = NormalizeTitle(MainTitle(title));
        if (main.Length == 0 || main != NormalizeTitle(MainTitle(otherTitle)))
        {
            return false;
        }

        var authors = AuthorTokens(author);
        var otherAuthors = AuthorTokens(otherAuthor);
        return authors.Count > 0 && otherAuthors.Count > 0
            ? authors.Overlaps(otherAuthors)
            : NormalizeTitle(title) == NormalizeTitle(otherTitle);
    }

    /// <summary>ISBN-13 for an ISBN-10 or ISBN-13 (separators ignored), or null when it is not one.</summary>
    public static string? NormalizeIsbn(string? value)
    {
        var digits = new string((value ?? "").Where(character => char.IsAsciiDigit(character) || character is 'X' or 'x').ToArray()).ToUpperInvariant();
        if (digits.Length == 13 && digits.All(char.IsAsciiDigit))
        {
            return digits;
        }

        if (digits.Length != 10 || !digits[..9].All(char.IsAsciiDigit))
        {
            return null;
        }

        var core = "978" + digits[..9];
        var sum = core.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
        return core + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A short language tag ("en", "id") for a provider's two- or three-letter code, or the
    /// lower-cased code itself when it is not one of the common aliases above; null when unknown.
    /// </summary>
    public static string? NormalizeLanguageTag(string? value)
    {
        var tag = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(tag) || tag.Length > 8)
        {
            return null;
        }

        return LanguageTagAliases.TryGetValue(tag, out var alias) ? alias : tag;
    }

    /// <summary>
    /// What an edition record offers: a file Jularr can import, a text it can only show online,
    /// or a catalog listing with neither (#405 "language/format when known").
    /// </summary>
    private static string EditionFormat(BookCatalogItem item) =>
        item.CanAcquire
            || item.SourceUrl.StartsWith("opds://", StringComparison.OrdinalIgnoreCase)
            ? EditionFormatEpub
        : item.CanPreview ? EditionFormatText
        : EditionFormatListing;

    /// <summary>A title for matching: lower case letters and digits, single spaces, no leading article.</summary>
    public static string NormalizeTitle(string? value)
    {
        var words = Words(value);
        if (words.Count > 1 && words[0] is "the" or "a" or "an" or "der" or "die" or "das" or "le" or "la" or "les")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>The title before a subtitle (":", ";" or " - ").</summary>
    public static string MainTitle(string title) => SearchPlanner.MainTitle(title);

    private static List<Work> Merge(IReadOnlyList<BookCatalogItem>[] providers)
    {
        var works = new List<Work>();
        var order = 0;
        foreach (var provider in providers)
        {
            foreach (var item in provider)
            {
                var record = new Record(item, order++);
                var work = works.FirstOrDefault(candidate => candidate.Accepts(record));
                if (work is null)
                {
                    works.Add(new Work(record));
                }
                else
                {
                    work.Add(record);
                }
            }
        }

        return works;
    }

    private static List<string> Words(string? value) =>
        [.. WordPattern().Matches(RemoveDiacritics(value ?? "").ToLowerInvariant()).Select(match => match.Value)];

    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static HashSet<string> AuthorTokens(string? author) =>
        Words(author).Where(word => word.Length > 1).ToHashSet(StringComparer.Ordinal);

    private static bool IsGoogle(BookCatalogItem item) =>
        item.SourceName.Equals(GoogleBooksSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>One provider record with the identity evidence it carries.</summary>
    private sealed record Record(BookCatalogItem Item, int Order)
    {
        public string Title { get; } = NormalizeTitle(Item.Title);
        public string Main { get; } = NormalizeTitle(MainTitle(Item.Title));
        public HashSet<string> Authors { get; } = AuthorTokens(Item.Author);
        public HashSet<string> Isbns { get; } = Item.Isbns.Select(NormalizeIsbn).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A canonical work: every record of the same book, from any provider.</summary>
    private sealed class Work(Record first)
    {
        private readonly List<Record> records = [first];

        public int FirstOrder => records[0].Order;
        public IReadOnlyList<Record> Records => records;

        public string Main => Preferred.Main;
        public string NormalizedTitle => Preferred.Title;
        public HashSet<string> Authors => records.SelectMany(record => record.Authors).ToHashSet(StringComparer.Ordinal);
        public HashSet<string> Isbns => records.SelectMany(record => record.Isbns).ToHashSet(StringComparer.Ordinal);
        public int EditionCount => records.Max(record => record.Item.EditionCount ?? 0);
        public int Providers => records.Select(record => record.Item.SourceName).Distinct(StringComparer.Ordinal).Count();

        // The work-level record (Open Library works) names the work best; otherwise the first.
        private Record Preferred =>
            records.FirstOrDefault(record => record.Item.Id.StartsWith("ol-", StringComparison.Ordinal)) ?? records[0];

        /// <summary>
        /// Same book when an ISBN is shared, or when the main titles are equal and the authors
        /// overlap (a record without author needs the full title to match). Similar titles
        /// alone never merge.
        /// </summary>
        public bool Accepts(Record record)
        {
            if (record.Isbns.Count > 0 && records.Any(existing => existing.Isbns.Overlaps(record.Isbns)))
            {
                return true;
            }

            return records.Any(existing =>
                existing.Main.Length > 0
                && existing.Main == record.Main
                && (existing.Authors.Count > 0 && record.Authors.Count > 0
                    ? existing.Authors.Overlaps(record.Authors)
                    : existing.Title == record.Title));
        }

        public void Add(Record record) => records.Add(record);

        public BookCatalogItem ToItem()
        {
            // The record Jularr acquires from: a free edition first, then the work-level record.
            var primary = records.FirstOrDefault(record => record.Item.CanAcquire)?.Item ?? Preferred.Item;
            var others = records.Select(record => record.Item).Where(item => !ReferenceEquals(item, primary)).ToArray();
            var google = records.Select(record => record.Item).FirstOrDefault(IsGoogle);
            var covers = CoverCandidates();
            var key = WorkKey();
            var cover = covers.Count == 0
                ? null
                : ChosenCovers.TryGetValue(key, out var remembered) && covers.Contains(remembered)
                    ? remembered
                    : Remember(key, covers[0]);

            return primary with
            {
                Title = Preferred.Item.Title,
                Author = FirstNonEmpty(Preferred.Item.Author, others.Select(item => item.Author)),
                // Google Books carries the publisher's description of current editions.
                Summary = FirstNonEmpty(
                    google?.Summary,
                    records
                        .Select(record => record.Item.Summary)
                        .OrderByDescending(summary => summary?.Length ?? 0)),
                CoverImageUrl = cover,
                Subjects = records
                    .SelectMany(record => record.Item.Subjects)
                    .Where(subject => !string.IsNullOrWhiteSpace(subject))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray(),
                // The work's original year: the earliest any record knows.
                FirstPublishYear = records.Select(record => record.Item.FirstPublishYear).Where(year => year is > 0).Min(),
                TextUrl = FirstNonEmpty(primary.TextUrl, others.Select(item => item.TextUrl)),
                EpubUrl = FirstNonEmpty(primary.EpubUrl, others.Select(item => item.EpubUrl)),
                TextSourceName = FirstNonEmpty(primary.TextSourceName, others.Select(item => item.TextSourceName)),
                Identities = [.. records.SelectMany(record => record.Item.Identities.Prepend(record.Item.Id)).Distinct(StringComparer.Ordinal)],
                Isbns = [.. Isbns.Order(StringComparer.Ordinal)],
                EditionCount = EditionCount > 0 ? EditionCount : null,
                CoverCandidates = covers,
                Publisher = FirstNonEmpty(google?.Publisher, records.Select(record => record.Item.Publisher)),
                PublishedDate = FirstNonEmpty(google?.PublishedDate, records.Select(record => record.Item.PublishedDate)),
                ExternalListState = records.Select(record => record.Item.ExternalListState).FirstOrDefault(state => state is not null),
                Editions = Editions()
            };
        }

        /// <summary>
        /// One row per merged provider record (#405): each carries its own year, language,
        /// publisher, ISBN and format, newest first. A single-record work still returns its one
        /// edition; the dialog only offers a picker once there is more than one to choose from.
        /// </summary>
        private IReadOnlyList<BookEditionSummary> Editions() =>
            records
                .Select(record => new BookEditionSummary(
                    record.Item.Id,
                    record.Item.FirstPublishYear,
                    record.Item.Language,
                    record.Item.Publisher,
                    record.Item.Isbns.FirstOrDefault(),
                    EditionFormat(record.Item),
                    record.Item.SourceName,
                    record.Item.CoverImageUrl))
                .OrderByDescending(edition => edition.Year ?? 0)
                .ToArray();

        /// <summary>
        /// Every usable cover, best first (see <see cref="CoverRank"/>). The client falls back
        /// along this list when one fails to load.
        /// </summary>
        private IReadOnlyList<string> CoverCandidates() =>
            records
                .SelectMany(record => record.Item.CoverCandidates.Count > 0
                    ? record.Item.CoverCandidates
                    : record.Item.CoverImageUrl is { } url ? [url] : [])
                .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith('/'))
                .Distinct(StringComparer.Ordinal)
                .Select((url, index) => (url, index))
                .OrderBy(entry => CoverRank(entry.url))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.url)
                .ToArray();

        private string WorkKey() => Main + "|" + string.Join(' ', Authors.Order(StringComparer.Ordinal));

        private static string Remember(string key, string cover)
        {
            if (ChosenCovers.Count >= CoverMemoryLimit)
            {
                ChosenCovers.Clear();
            }

            ChosenCovers[key] = cover;
            return cover;
        }
    }

    /// <summary>
    /// Lower is better. A local cover first; then Google Books, which usually shows the current
    /// retail edition (#371), large sizes before its thumbnail; then large Open Library covers
    /// and other artwork; tiny thumbnails last. Open Library names sizes -L/-M/-S; Google Books
    /// uses zoom 6/4/3 (large), 2 (small), 1 (thumbnail) and 5 (small thumbnail).
    /// </summary>
    private static int CoverRank(string url)
    {
        if (url.StartsWith('/'))
        {
            return 0;
        }

        if (url.Contains("covers.openlibrary.org", StringComparison.OrdinalIgnoreCase))
        {
            return url.Contains("-L.", StringComparison.Ordinal) ? 3
                : url.Contains("-M.", StringComparison.Ordinal) ? 4
                : 6;
        }

        if (url.Contains("books.google", StringComparison.OrdinalIgnoreCase))
        {
            return GoogleZoom().Match(url) is { Success: true } zoom
                ? zoom.Groups[1].Value switch
                {
                    "6" or "4" or "3" => 1,
                    "2" or "1" => 2,
                    _ => 5
                }
                : 1;
        }

        return 4;
    }

    private static string? FirstNonEmpty(string? first, IEnumerable<string?> rest) =>
        !string.IsNullOrWhiteSpace(first) ? first : rest.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>What the user typed: title words, maybe an author, maybe an ISBN.</summary>
    private sealed class Query(string text)
    {
        private readonly string? isbn = NormalizeIsbn(text);
        private readonly List<string> words = Words(text);

        public int Score(Work work)
        {
            if (isbn is not null)
            {
                return work.Isbns.Contains(isbn) ? 10_000 : 0;
            }

            // "atomic habits james clear": the author words are not part of the title.
            var authors = work.Authors;
            var authorHits = words.Count(authors.Contains);
            var titleWords = authorHits > 0 && authorHits < words.Count
                ? words.Where(word => !authors.Contains(word)).ToList()
                : words;
            var wanted = NormalizeTitle(string.Join(' ', titleWords));
            var title = work.NormalizedTitle;
            var main = work.Main;
            var titleTokens = title.Split(' ');

            var score = 0;
            if (wanted.Length > 0 && (main == wanted || title == wanted))
            {
                score += 1000;
            }
            else if (wanted.Length > 0 && (main.StartsWith(wanted + " ", StringComparison.Ordinal) || title.StartsWith(wanted + " ", StringComparison.Ordinal)))
            {
                score += 400;
            }
            else if (titleWords.Count > 0 && titleWords.All(titleTokens.Contains))
            {
                score += 200;
            }

            if (authorHits > 0)
            {
                score += 150;
            }

            // A workbook, summary or journal for the requested book stays below the book itself.
            if (titleTokens.Any(token => CompanionWords.Contains(token) && !words.Contains(token)))
            {
                score -= 700;
            }

            // Popular works have many editions; companion products and one-off records few.
            score += (int)Math.Min(120, Math.Log2(work.EditionCount + 1) * 12);
            score += work.Providers > 1 ? 40 : 0;
            score += work.Records.Any(record => record.Item.CoverImageUrl is not null) ? 20 : 0;
            score += authors.Count > 0 ? 10 : 0;
            score += work.Records.Any(record => record.Item.FirstPublishYear is > 0) ? 5 : 0;
            return score;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[?&]zoom=(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleZoom();
}
