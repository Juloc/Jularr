using System.Text.Json.Serialization;

namespace Jularr.Web.Features.Books;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfDocumentClass
{
    Digital,
    LayoutHeavy,
    Scanned,
    Mixed
}

/// <summary>
/// How an uncertain derived decision was produced. Low-confidence decisions stay visible as such;
/// later analysis stages (OCR, AI review, admin correction) record their own <see cref="Source"/>.
/// </summary>
public sealed record PdfDecision(string Source, double Confidence, string Detail)
{
    public const string HeuristicSource = "heuristic";
}

public sealed record PdfClassification(PdfDocumentClass Class, PdfDecision Decision);

/// <summary>
/// One page of the reading sequence. <see cref="PhysicalPages"/> lists every PDF page that shows it,
/// first occurrence first; the first one gives the page its stable chapter identity.
/// </summary>
public sealed record PdfLogicalPage(int Number, IReadOnlyList<int> PhysicalPages, int FurnitureParagraphsRemoved)
{
    [JsonIgnore]
    public int FirstPhysicalPage => PhysicalPages[0];
}

/// <summary>Physical pages <see cref="FirstPhysicalPage"/>..<see cref="LastPhysicalPage"/> repeat the pages starting at <see cref="OfPhysicalPage"/>.</summary>
public sealed record PdfDuplicateRange(int FirstPhysicalPage, int LastPhysicalPage, int OfPhysicalPage, PdfDecision Decision)
{
    [JsonIgnore]
    public int PageCount => LastPhysicalPage - FirstPhysicalPage + 1;
}

/// <summary>A paragraph that repeats at the page edges (running header/footer, printed page number, browser-print path/date) and is excluded from reading text.</summary>
public sealed record PdfFurniturePattern(string Kind, string Template, int PageCount, int ParagraphsRemoved, PdfDecision Decision);

public sealed record PdfAnalysisDiagnostics(
    double TextCoverage,
    int PagesWithText,
    double MedianCharactersPerTextPage,
    double ShortParagraphShare,
    int DuplicatePagesRemoved,
    int FurnitureParagraphsRemoved);

/// <summary>
/// What Jularr derives from a PDF without touching it: classification, the logical page sequence with its
/// mapping to physical pages, and the page furniture that was left out of the reading text. It is disposable
/// and rebuildable from the source file; it holds mappings and decisions, never the chapter text (which stays
/// with the Novel chapters). Later slices of the PDF Smart Book work (outline structure, blocks, OCR,
/// regions) extend this record and bump <see cref="PdfDocumentAnalyzer.Version"/>.
/// </summary>
public sealed record PdfDerivedDocument(
    int AnalyzerVersion,
    string SourceHash,
    int PhysicalPageCount,
    PdfClassification Classification,
    IReadOnlyList<PdfLogicalPage> LogicalPages,
    IReadOnlyList<PdfDuplicateRange> DuplicateRanges,
    IReadOnlyList<PdfFurniturePattern> Furniture,
    PdfAnalysisDiagnostics Diagnostics)
{
    /// <summary>The logical page (1-based) shown by every physical page; index 0 is physical page 1.</summary>
    public int[] LogicalPageByPhysicalPage()
    {
        var map = new int[PhysicalPageCount];
        foreach (var logical in LogicalPages)
        {
            foreach (var physical in logical.PhysicalPages)
            {
                map[physical - 1] = logical.Number;
            }
        }

        return map;
    }
}
