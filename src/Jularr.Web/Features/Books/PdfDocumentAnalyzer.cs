using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Books;

/// <summary>The derived model of a PDF with the reading text of each logical page (page furniture removed).</summary>
public sealed record PdfDocumentAnalysis(PdfDerivedDocument Document, IReadOnlyList<string> LogicalPageTexts);

/// <summary>
/// Derives the <see cref="PdfDerivedDocument"/> of a PDF from the page texts <see cref="PdfDocumentReader"/>
/// extracted. Every decision is a simple, explainable heuristic over the whole document (never the file name):
/// furniture is a paragraph that repeats at the page edges, a duplicate is a page (range) whose normalised
/// text repeats an earlier page. Physical pages are never dropped; they are mapped to the page they repeat.
/// Raising <see cref="Version"/> makes stored derived documents stale so works get re-analysed.
/// </summary>
public static partial class PdfDocumentAnalyzer
{
    public const int Version = 1;

    private const int EdgeParagraphs = 3;
    private const int MinFurniturePages = 8;
    private const double MinFurnitureShare = 0.4;
    private const int MaxFurnitureLength = 200;
    private const int TextPageCharacters = 30;
    private const int InformativeCharacters = 40;
    private const int StrongRepeatCharacters = 200;
    private const int MinDuplicateRun = 3;
    private const int MaxAccidentalRepeatOffset = 2;
    private const double DuplicateSimilarity = 0.9;
    private const double AccidentalRepeatSimilarity = 0.98;
    private const double ScannedCoverage = 0.15;
    private const double DigitalCoverage = 0.85;
    private const double LayoutHeavyMedianCharacters = 600;
    private const double LayoutHeavyShortParagraphShare = 0.5;
    private const int ShortParagraphCharacters = 20;

    public static PdfDocumentAnalysis Analyze(IReadOnlyList<PdfPageText> pages, string sourceHash)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count);

        var paragraphs = pages
            .Select(page => ParagraphBreak().Split(page.Text).Select(paragraph => paragraph.Trim()).Where(paragraph => paragraph.Length > 0).ToArray())
            .ToArray();
        var (cleaned, furniture, removedPerPage) = RemoveFurniture(paragraphs);
        var pageTexts = cleaned.Select(kept => string.Join("\n\n", kept)).ToArray();

        var keys = pageTexts.Select(NormalizedKey).ToArray();
        var tokens = new HashSet<string>?[pages.Count];
        var duplicateOf = Enumerable.Repeat(-1, pages.Count).ToArray();
        var duplicateRanges = FindDuplicateRanges(keys, index => tokens[index] ??= WordSet(pageTexts[index]), duplicateOf);

        var logicalIndexByPage = new Dictionary<int, int>();
        var logicalPages = new List<(List<int> Physical, int Furniture)>();
        for (var index = 0; index < pages.Count; index++)
        {
            if (duplicateOf[index] >= 0)
            {
                continue;
            }

            logicalIndexByPage[index] = logicalPages.Count;
            logicalPages.Add(([index + 1], removedPerPage[index]));
        }

        for (var index = 0; index < pages.Count; index++)
        {
            if (duplicateOf[index] >= 0)
            {
                logicalPages[logicalIndexByPage[duplicateOf[index]]].Physical.Add(index + 1);
            }
        }

        var classification = Classify(pageTexts, cleaned, duplicateRanges.Sum(range => range.PageCount), removedPerPage.Sum(), out var diagnostics);
        var document = new PdfDerivedDocument(
            Version,
            sourceHash,
            pages.Count,
            classification,
            logicalPages.Select((logical, index) => new PdfLogicalPage(index + 1, logical.Physical, logical.Furniture)).ToArray(),
            duplicateRanges,
            furniture,
            diagnostics);
        var logicalTexts = logicalPages.Select(logical => pageTexts[logical.Physical[0] - 1]).ToArray();
        return new PdfDocumentAnalysis(document, logicalTexts);
    }

    /// <summary>
    /// A paragraph in the first or last <see cref="EdgeParagraphs"/> of a page whose digit-masked form sits at the
    /// page edges of at least 40% of the pages is furniture. Masking digits makes "Seite 7 von 262" and a dated
    /// file path repeat as one pattern; only edge positions count so repeated body text is never removed.
    /// </summary>
    private static (string[][] Cleaned, PdfFurniturePattern[] Patterns, int[] RemovedPerPage) RemoveFurniture(string[][] paragraphs)
    {
        var masked = paragraphs.Select(page => page.Select(FurnitureTemplate).ToArray()).ToArray();
        var pagesByTemplate = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var pageIndex = 0; pageIndex < paragraphs.Length; pageIndex++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < masked[pageIndex].Length; index++)
            {
                var template = masked[pageIndex][index];
                if (IsEdge(index, masked[pageIndex].Length) && paragraphs[pageIndex][index].Length <= MaxFurnitureLength && seen.Add(template))
                {
                    pagesByTemplate[template] = pagesByTemplate.GetValueOrDefault(template) + 1;
                }
            }
        }

        var pagesWithText = paragraphs.Count(page => page.Length > 0);
        var minPages = Math.Max(MinFurniturePages, (int)Math.Ceiling(pagesWithText * MinFurnitureShare));
        var templates = pagesByTemplate.Where(entry => entry.Value >= minPages).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var removedByTemplate = templates.Keys.ToDictionary(template => template, _ => 0, StringComparer.Ordinal);
        var longestFirst = templates.Keys.OrderByDescending(template => template.Length).ToArray();
        var removedPerPage = new int[paragraphs.Length];
        var cleaned = new string[paragraphs.Length][];
        for (var pageIndex = 0; pageIndex < paragraphs.Length; pageIndex++)
        {
            var page = paragraphs[pageIndex];
            var kept = new List<string>(page.Length);
            for (var index = 0; index < page.Length; index++)
            {
                var parts = IsEdge(index, page.Length) && page[index].Length <= MaxFurnitureLength ? FurnitureParts(masked[pageIndex][index], longestFirst) : [];
                if (parts.Count > 0)
                {
                    foreach (var part in parts)
                    {
                        removedByTemplate[part]++;
                    }

                    removedPerPage[pageIndex]++;
                    continue;
                }

                kept.Add(page[index]);
            }

            cleaned[pageIndex] = kept.ToArray();
        }

        var patterns = templates
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new PdfFurniturePattern(
                FurnitureKind(entry.Key),
                entry.Key,
                entry.Value,
                removedByTemplate[entry.Key],
                new PdfDecision(PdfDecision.HeuristicSource, Math.Round((double)entry.Value / pagesWithText, 2), $"at the page edges of {entry.Value} of {pagesWithText} pages")))
            .ToArray();
        return (cleaned, patterns, removedPerPage);
    }

    /// <summary>
    /// The furniture templates a paragraph consists of, or none when it holds anything else. A page without body text
    /// can print its page label and path as one paragraph, which is still only furniture.
    /// </summary>
    private static List<string> FurnitureParts(string template, string[] furnitureTemplates)
    {
        var parts = new List<string>();
        var remainder = template;
        foreach (var furniture in furnitureTemplates)
        {
            if (remainder.Contains(furniture, StringComparison.Ordinal))
            {
                parts.Add(furniture);
                remainder = remainder.Replace(furniture, " ", StringComparison.Ordinal);
            }
        }

        return remainder.Trim().Length == 0 ? parts : [];
    }

    private static bool IsEdge(int index, int count) => index < EdgeParagraphs || index >= count - EdgeParagraphs;

    private static string FurnitureTemplate(string paragraph) =>
        Whitespace().Replace(Digits().Replace(paragraph, "#"), " ").Trim().ToLowerInvariant();

    private static string FurnitureKind(string template)
    {
        if (template.Contains("://", StringComparison.Ordinal) || DrivePath().IsMatch(template) || template.Contains("file:", StringComparison.Ordinal))
        {
            return "file-path";
        }

        if (PageNumberLabel().IsMatch(template))
        {
            return "page-number";
        }

        return DateOrTime().IsMatch(template) ? "date" : "running-line";
    }

    /// <summary>
    /// Finds later pages that repeat earlier ones and marks them in <paramref name="duplicateOf"/> with the page they
    /// repeat. Candidate offsets come from exact repeats (a printed-twice book repeats at one constant offset) plus the
    /// adjacent offsets for accidentally repeated pages; a run of matching pages is accepted when it is long enough
    /// or, for tiny offsets, when the repeated pages are long and near identical.
    /// </summary>
    private static List<PdfDuplicateRange> FindDuplicateRanges(string[] keys, Func<int, HashSet<string>> words, int[] duplicateOf)
    {
        var ranges = new List<PdfDuplicateRange>();
        var votes = new Dictionary<int, int>();
        var lastSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < keys.Length; index++)
        {
            if (keys[index].Length < InformativeCharacters)
            {
                continue;
            }

            if (lastSeen.TryGetValue(keys[index], out var previous))
            {
                votes[index - previous] = votes.GetValueOrDefault(index - previous) + 1;
            }

            lastSeen[keys[index]] = index;
        }

        var offsets = votes
            .Where(vote => vote.Value >= MinDuplicateRun)
            .OrderByDescending(vote => vote.Value)
            .ThenBy(vote => vote.Key)
            .Select(vote => vote.Key)
            .Concat(Enumerable.Range(1, MaxAccidentalRepeatOffset))
            .Distinct()
            .Where(offset => offset < keys.Length);
        foreach (var offset in offsets)
        {
            var run = new List<(int Page, double Similarity)>();
            for (var page = offset; page <= keys.Length; page++)
            {
                double? similarity = page < keys.Length && duplicateOf[page] < 0 ? PageSimilarity(keys, words, page, page - offset) : null;
                if (similarity is { } matched)
                {
                    run.Add((page, matched));
                    continue;
                }

                AcceptRun(run, offset, keys, duplicateOf, ranges);
                run.Clear();
            }
        }

        return ranges.OrderBy(range => range.FirstPhysicalPage).ToList();
    }

    /// <summary>Similarity of two pages when they repeat each other; 0 for two pages without usable text (blank separators ride along in a run but never make one); null when they differ.</summary>
    private static double? PageSimilarity(string[] keys, Func<int, HashSet<string>> words, int page, int other)
    {
        var informative = keys[page].Length >= InformativeCharacters && keys[other].Length >= InformativeCharacters;
        if (!informative)
        {
            var blank = keys[page].Length < InformativeCharacters && keys[other].Length < InformativeCharacters;
            return blank && keys[page] == keys[other] ? 0 : null;
        }

        if (keys[page] == keys[other])
        {
            return 1;
        }

        var first = words(page);
        var second = words(other);
        var shared = first.Count(second.Contains);
        var union = first.Count + second.Count - shared;
        var similarity = union == 0 ? 0 : (double)shared / union;
        return similarity >= DuplicateSimilarity ? similarity : null;
    }

    private static void AcceptRun(List<(int Page, double Similarity)> run, int offset, string[] keys, int[] duplicateOf, List<PdfDuplicateRange> ranges)
    {
        var informative = run.Where(entry => entry.Similarity > 0).ToArray();
        var strong = informative.All(entry => entry.Similarity >= AccidentalRepeatSimilarity && keys[entry.Page].Length >= StrongRepeatCharacters);
        if (informative.Length == 0 || (informative.Length < MinDuplicateRun && !(offset <= MaxAccidentalRepeatOffset && strong)))
        {
            return;
        }

        foreach (var (page, _) in run)
        {
            duplicateOf[page] = duplicateOf[page - offset] >= 0 ? duplicateOf[page - offset] : page - offset;
        }

        var first = run[0].Page;
        var last = run[^1].Page;
        var confidence = Math.Round(informative.Average(entry => entry.Similarity), 2);
        ranges.Add(new PdfDuplicateRange(
            first + 1,
            last + 1,
            first - offset + 1,
            new PdfDecision(
                PdfDecision.HeuristicSource,
                confidence,
                string.Create(CultureInfo.InvariantCulture, $"{run.Count} pages repeat the pages {offset} earlier; {informative.Length} with matching text"))));
    }

    private static PdfClassification Classify(string[] pageTexts, string[][] cleaned, int duplicatePages, int furnitureParagraphs, out PdfAnalysisDiagnostics diagnostics)
    {
        var textLengths = pageTexts.Select(text => text.Length).Where(length => length >= TextPageCharacters).Order().ToArray();
        var coverage = (double)textLengths.Length / pageTexts.Length;
        var median = textLengths.Length == 0 ? 0 : (textLengths[(textLengths.Length - 1) / 2] + textLengths[textLengths.Length / 2]) / 2.0;
        var paragraphCount = cleaned.Sum(page => page.Length);
        var shortShare = paragraphCount == 0 ? 0 : (double)cleaned.Sum(page => page.Count(paragraph => paragraph.Length < ShortParagraphCharacters)) / paragraphCount;
        diagnostics = new PdfAnalysisDiagnostics(Math.Round(coverage, 3), textLengths.Length, Math.Round(median), Math.Round(shortShare, 3), duplicatePages, furnitureParagraphs);

        var evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{textLengths.Length} of {pageTexts.Length} pages carry text, median {median:0} characters per text page, {shortShare:P0} short paragraphs");
        if (coverage < ScannedCoverage)
        {
            return new PdfClassification(PdfDocumentClass.Scanned, new PdfDecision(PdfDecision.HeuristicSource, Math.Round(1 - coverage, 2), evidence));
        }

        if (coverage < DigitalCoverage)
        {
            return new PdfClassification(PdfDocumentClass.Mixed, new PdfDecision(PdfDecision.HeuristicSource, 0.6, evidence));
        }

        return median < LayoutHeavyMedianCharacters || shortShare > LayoutHeavyShortParagraphShare
            ? new PdfClassification(PdfDocumentClass.LayoutHeavy, new PdfDecision(PdfDecision.HeuristicSource, 0.6, evidence))
            : new PdfClassification(PdfDocumentClass.Digital, new PdfDecision(PdfDecision.HeuristicSource, Math.Round(coverage, 2), evidence));
    }

    private static string NormalizedKey(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static HashSet<string> WordSet(string text) =>
        Words().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"(?:^|\s)[a-z]:[\\/]")]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[-–\s]*(?:(?:seite|page|s\.|p\.)\s*)?#(?:\s*(?:von|of|/)\s*#)?[-–\s]*$")]
    private static partial Regex PageNumberLabel();

    [GeneratedRegex(@"#[./-]#[./-]#|#:#")]
    private static partial Regex DateOrTime();
}
