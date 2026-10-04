using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

/// <summary>
/// PDF Smart Book foundation (#819): the derived document maps logical pages to physical pages, leaves page
/// furniture out of the reading text and never loses reader data when a work is re-analysed.
/// </summary>
[TestClass]
public sealed class BookPdfAnalysisTests
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string DatePath = "28.01.2004 file:///C:/books/novel.htm";

    [TestMethod]
    public void ABookPrintedTwiceHasOneLogicalPagePerPrintedPageAndTheFurnitureIsLeftOut()
    {
        var once = Enumerable.Range(1, 12).Select(number => PrintedPage(number, 12)).ToArray();
        var analysis = PdfDocumentAnalyzer.Analyze(Pages(once.Concat(once)), Hash);
        var document = analysis.Document;

        Assert.AreEqual(24, document.PhysicalPageCount);
        Assert.AreEqual(12, document.LogicalPages.Count);
        CollectionAssert.AreEqual(new[] { 1, 13 }, document.LogicalPages[0].PhysicalPages.ToArray());
        CollectionAssert.AreEqual(new[] { 12, 24 }, document.LogicalPages[^1].PhysicalPages.ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(1, 12).Concat(Enumerable.Range(1, 12)).ToArray(), document.LogicalPageByPhysicalPage());

        var range = document.DuplicateRanges.Single();
        Assert.AreEqual((13, 24, 1), (range.FirstPhysicalPage, range.LastPhysicalPage, range.OfPhysicalPage));
        Assert.AreEqual(PdfDecision.HeuristicSource, range.Decision.Source);
        Assert.IsTrue(range.Decision.Confidence >= 0.9);
        Assert.AreEqual(12, document.Diagnostics.DuplicatePagesRemoved);

        CollectionAssert.AreEquivalent(new[] { "page-number", "file-path", "running-line" }, document.Furniture.Select(pattern => pattern.Kind).ToArray());
        Assert.AreEqual(3 * 24, document.Diagnostics.FurnitureParagraphsRemoved);
        Assert.AreEqual(PdfDocumentClass.Digital, document.Classification.Class);
        Assert.AreEqual(PdfDocumentAnalyzer.Version, document.AnalyzerVersion);
        Assert.AreEqual(Hash, document.SourceHash);

        Assert.AreEqual(12, analysis.LogicalPageTexts.Count);
        foreach (var text in analysis.LogicalPageTexts)
        {
            Assert.IsFalse(text.Contains("Seite", StringComparison.Ordinal) || text.Contains("file:", StringComparison.Ordinal) || text.Contains("THE NOVEL", StringComparison.Ordinal));
            Assert.AreEqual(2, text.Split("\n\n").Length, "Only the two body paragraphs are reading text.");
        }
    }

    [TestMethod]
    public void AnAccidentallyRepeatedPageSharesTheLogicalPageOfItsOriginal()
    {
        var pages = Enumerable.Range(1, 10).Select(number => Body(number, 2)).ToList();
        pages.Insert(5, pages[4]);

        var document = PdfDocumentAnalyzer.Analyze(Pages(pages), Hash).Document;

        Assert.AreEqual(11, document.PhysicalPageCount);
        Assert.AreEqual(10, document.LogicalPages.Count);
        CollectionAssert.AreEqual(new[] { 5, 6 }, document.LogicalPages[4].PhysicalPages.ToArray());
        var range = document.DuplicateRanges.Single();
        Assert.AreEqual((6, 6, 5), (range.FirstPhysicalPage, range.LastPhysicalPage, range.OfPhysicalPage));
    }

    [TestMethod]
    public void DistinctPagesAndRepeatedBodyTextAreNeverRemoved()
    {
        var pages = Enumerable.Range(1, 12).Select(number => Body(number, 8)).ToList();
        // The same refrain in the middle of many pages is body text, not a header or footer.
        var refrain = "She said the same words again, slowly, as if the room could still hear them.";
        for (var index = 0; index < pages.Count; index++)
        {
            var paragraphs = pages[index].Split("\n\n").ToList();
            paragraphs.Insert(4, refrain);
            pages[index] = string.Join("\n\n", paragraphs);
        }

        var analysis = PdfDocumentAnalyzer.Analyze(Pages(pages), Hash);

        Assert.AreEqual(12, analysis.Document.LogicalPages.Count);
        Assert.AreEqual(0, analysis.Document.DuplicateRanges.Count);
        Assert.AreEqual(0, analysis.Document.Furniture.Count);
        CollectionAssert.AreEqual(pages, analysis.LogicalPageTexts.ToArray());
    }

    [TestMethod]
    public void DocumentsBelowTheFurnitureMinimumKeepEveryParagraph()
    {
        var pages = Enumerable.Range(1, 4).Select(number => "THE NOVEL\n\n" + Body(number, 2)).ToArray();

        var analysis = PdfDocumentAnalyzer.Analyze(Pages(pages), Hash);

        Assert.AreEqual(0, analysis.Document.Furniture.Count);
        CollectionAssert.AreEqual(pages, analysis.LogicalPageTexts.ToArray());
    }

    [TestMethod]
    public void TextCoverageClassifiesDigitalMixedScannedAndLayoutHeavyDocuments()
    {
        var textless = Enumerable.Repeat("", 10).Select(page => page).ToArray();
        var scanned = PdfDocumentAnalyzer.Analyze(Pages(textless), Hash).Document;
        Assert.AreEqual(PdfDocumentClass.Scanned, scanned.Classification.Class);
        Assert.AreEqual(10, scanned.LogicalPages.Count, "Blank or scanned pages are never collapsed into each other.");

        var mixed = PdfDocumentAnalyzer.Analyze(Pages(Enumerable.Range(1, 5).Select(number => Body(number, 4)).Concat(textless.Take(5))), Hash).Document;
        Assert.AreEqual(PdfDocumentClass.Mixed, mixed.Classification.Class);
        Assert.AreEqual(0.5, mixed.Diagnostics.TextCoverage);

        var slides = Enumerable.Range(1, 10).Select(number => string.Join("\n\n", Enumerable.Range(0, 5).Select(bullet => Word((number * 31) + bullet) + " " + Word((number * 17) + bullet)))).ToArray();
        Assert.AreEqual(PdfDocumentClass.LayoutHeavy, PdfDocumentAnalyzer.Analyze(Pages(slides), Hash).Document.Classification.Class);
        Assert.IsTrue(PdfDocumentAnalyzer.Analyze(Pages(slides), Hash).Document.Classification.Decision.Detail.Contains("median", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DerivedDocumentsAreStoredPerSourceHashAndAnalyzerVersionAndRebuildable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-derived-{Guid.NewGuid():N}");
        var store = new PdfDerivedDocumentStore(root);
        try
        {
            var document = PdfDocumentAnalyzer.Analyze(Pages(Enumerable.Range(1, 12).Select(number => PrintedPage(number, 12))), Hash).Document;
            Assert.IsNull(await store.TryLoadLatestAsync(Hash, CancellationToken.None));

            await store.SaveAsync(document, CancellationToken.None);
            await store.SaveAsync(document with { AnalyzerVersion = document.AnalyzerVersion + 1 }, CancellationToken.None);

            var latest = await store.TryLoadLatestAsync(Hash.ToLowerInvariant(), CancellationToken.None);
            Assert.IsNotNull(latest);
            Assert.AreEqual(document.AnalyzerVersion + 1, latest.AnalyzerVersion);
            Assert.AreEqual(document.LogicalPages.Count, latest.LogicalPages.Count);
            CollectionAssert.AreEqual(document.LogicalPageByPhysicalPage(), latest.LogicalPageByPhysicalPage());
            Assert.AreEqual(document.Classification.Class, latest.Classification.Class);

            await File.WriteAllTextAsync(Path.Combine(root, $"{Hash.ToLowerInvariant()}.v{document.AnalyzerVersion + 1}.json"), "{ not json");
            Assert.IsNull(await store.TryLoadLatestAsync(Hash, CancellationToken.None), "A damaged derived file is disposable and counts as missing.");
            Assert.IsNull(await store.TryLoadLatestAsync(new string('A', 64), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportingAPdfPrintedTwiceCreatesOneChapterPerLogicalPageAndMapsEveryPhysicalPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workId = await fixture.ImportAsync(PrintedTwice(12));

        var chapters = await fixture.ChaptersAsync(workId);
        Assert.AreEqual(12, chapters.Count);
        Assert.IsTrue(chapters.All(chapter => !chapter.OriginalText.Contains("Seite", StringComparison.Ordinal)), chapters[0].OriginalText[^150..]);
        CollectionAssert.AreEqual(Enumerable.Range(1, 12).ToArray(), chapters.Select(chapter => chapter.Number).ToArray());
        Assert.AreEqual($"book://{workId:N}/1", chapters[0].SourceUrl);

        var pageChapters = await fixture.Service.GetPdfPageChapterIdsAsync(workId, CancellationToken.None);
        Assert.AreEqual(24, pageChapters.Count);
        for (var page = 0; page < 12; page++)
        {
            Assert.AreEqual(chapters[page].Id, pageChapters[page]);
            Assert.AreEqual(chapters[page].Id, pageChapters[page + 12], "A repeated physical page shares the chapter of its logical page.");
        }

        var analysis = await fixture.Service.GetPdfAnalysisAsync(workId, CancellationToken.None);
        Assert.AreEqual(12, analysis!.LogicalPages.Count);
    }

    [TestMethod]
    public async Task ReanalysisMergesRepeatedPageChaptersAndKeepsTranslationsProgressAndBookmarks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pdf = PrintedTwice(12);
        var workId = await fixture.ImportAsync(pdf);

        // Recreate how a work imported before logical pages looks: one chapter per physical page with furniture.
        var rawPages = PdfDocumentReader.Read(pdf).Pages;
        var work = await fixture.Db.NovelWorks.SingleAsync(x => x.Id == workId);
        var volume = await fixture.Db.NovelVolumes.SingleAsync(x => x.WorkId == workId);
        await NovelVolumeContent.SyncChaptersAsync(
            fixture.Db,
            work,
            volume,
            rawPages.Select(page => new NovelVolumeChapterInput($"book://{workId:N}/{page.Number}", page.Text.Split('\n')[0], page.Text)).ToArray(),
            CancellationToken.None);
        var legacy = await fixture.ChaptersAsync(workId);
        Assert.AreEqual(24, legacy.Count);
        Assert.IsTrue(legacy.All(chapter => chapter.OriginalText.Contains("Seite", StringComparison.Ordinal)));

        var survivors = legacy.Take(12).ToArray();
        Translate(fixture.Db, legacy[0], "Seite eins");
        Translate(fixture.Db, legacy[1], "Seite zwei");
        Translate(fixture.Db, legacy[13], "Seite zwei, zweite Kopie");
        Translate(fixture.Db, legacy[14], "Seite drei, nur auf der Kopie");
        fixture.Db.NovelProgress.Add(new NovelProgress { ProfileId = "alice", WorkId = workId, ChapterId = legacy[15].Id, PositionPermille = 400, AnchorLanguage = "en" });
        fixture.Db.NovelBookmarks.Add(new NovelBookmark { ProfileId = "alice", WorkId = workId, ChapterId = legacy[16].Id, PositionPermille = 100, Language = "en" });
        await fixture.Db.SaveChangesAsync();

        var document = await fixture.Service.ReanalyzePdfAsync(workId, CancellationToken.None);

        Assert.AreEqual(12, document.LogicalPages.Count);
        var chapters = await fixture.ChaptersAsync(workId);
        CollectionAssert.AreEqual(survivors.Select(chapter => chapter.Id).ToArray(), chapters.Select(chapter => chapter.Id).ToArray(), "The first occurrence of each page keeps its chapter.");
        Assert.IsTrue(chapters.All(chapter => !chapter.OriginalText.Contains("Seite", StringComparison.Ordinal)), chapters[0].OriginalText[^150..]);
        Assert.IsTrue(chapters.All(chapter => chapter.SourceHash == NovelVolumeContent.Hash(chapter.OriginalText)));

        var translations = await fixture.Db.NovelTranslations.AsNoTracking().ToListAsync();
        Assert.AreEqual(3, translations.Count);
        foreach (var translation in translations)
        {
            var chapter = chapters.Single(x => x.Id == translation.ChapterId);
            Assert.AreEqual(chapter.SourceHash, translation.SourceHash, "The cached translation stays valid for the cleaned chapter text.");
        }

        Assert.AreEqual("Seite eins", translations.Single(x => x.ChapterId == chapters[0].Id).Text);
        Assert.AreEqual("Seite zwei", translations.Single(x => x.ChapterId == chapters[1].Id).Text, "The surviving chapter's own translation wins over the repeated page's.");
        Assert.AreEqual("Seite drei, nur auf der Kopie", translations.Single(x => x.ChapterId == chapters[2].Id).Text, "A translation made only on a repeated page moves to the surviving chapter.");

        Assert.AreEqual(chapters[3].Id, (await fixture.Db.NovelProgress.AsNoTracking().SingleAsync()).ChapterId);
        Assert.AreEqual(400, (await fixture.Db.NovelProgress.AsNoTracking().SingleAsync()).PositionPermille);
        Assert.AreEqual(chapters[4].Id, (await fixture.Db.NovelBookmarks.AsNoTracking().SingleAsync()).ChapterId);

        var again = await fixture.Service.ReanalyzePdfAsync(workId, CancellationToken.None);
        Assert.AreEqual(12, again.LogicalPages.Count);
        Assert.AreEqual(3, await fixture.Db.NovelTranslations.CountAsync(), "Re-analysing an up-to-date work changes nothing.");
    }

    private static void Translate(AppDbContext db, NovelChapter chapter, string text) =>
        db.NovelTranslations.Add(new NovelTranslation { ChapterId = chapter.Id, TargetLanguage = "de", ProviderId = "test", PromptVersion = 1, SourceHash = chapter.SourceHash, Text = text });

    private static IReadOnlyList<PdfPageText> Pages(IEnumerable<string> texts) =>
        texts.Select((text, index) => new PdfPageText(index + 1, text)).ToArray();

    /// <summary>A page of a browser-printed book: running header, two body paragraphs, page label and a dated file path.</summary>
    private static string PrintedPage(int number, int total) =>
        string.Join("\n\n", "THE NOVEL", Body(number, 2), $"Seite {number} von {total}", DatePath);

    /// <summary>Distinct body paragraphs of a page; the random words make two pages share almost no vocabulary.</summary>
    private static string Body(int number, int paragraphs)
    {
        var random = new Random(number * 7919);
        return string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(paragraph => string.Join(' ', Enumerable.Range(0, 100).Select(word => Word(random.Next(0, 800))))));
    }

    /// <summary>A word of letters only: digits are masked when furniture is detected, so numbered words would look identical.</summary>
    private static string Word(int value) =>
        new([(char)('a' + (value % 26)), (char)('a' + ((value / 26) % 26)), (char)('a' + ((value / 676) % 26)), 'x']);

    private static byte[] PrintedTwice(int pages)
    {
        var once = Enumerable.Range(1, pages).Select(number => PrintedPage(number, pages)).ToArray();
        return BuildPdf(once.Concat(once).ToArray());
    }

    /// <summary>A minimal well-formed PDF whose pages show the paragraphs of each text, far enough apart to read back as paragraphs.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> pageTexts)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pageTexts.Select((_, index) => $"{4 + (index * 2)} 0 R"))}] /Count {pageTexts.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var index = 0; index < pageTexts.Count; index++)
        {
            var paragraphs = pageTexts[index].Split("\n\n");
            var stream = new StringBuilder("BT /F1 12 Tf 72 740 Td");
            for (var paragraph = 0; paragraph < paragraphs.Length; paragraph++)
            {
                // The reader starts a paragraph at a gap well above the page's usual line gap, so long paragraphs wrap.
                var lines = paragraphs[paragraph].Split(' ').Chunk(15).Select(words => string.Join(' ', words));
                var first = true;
                foreach (var line in lines)
                {
                    stream.Append(paragraph == 0 && first ? "" : first ? " 0 -40 Td" : " 0 -14 Td").Append(" (").Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj");
                    first = false;
                }
            }

            stream.Append(" ET");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (index * 2)} 0 R >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        for (var index = 0; index < objects.Count; index++)
        {
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        pdf.Append("trailer\n<< /Root 1 0 R >>\n%%EOF");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;

        private Fixture(string root, AppDbContext db, BookCatalogService service)
        {
            this.root = root;
            Db = db;
            Service = service;
        }

        public AppDbContext Db { get; }
        public BookCatalogService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-pdf-analysis-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True;Pooling=False")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:FilesPath"] = Path.Combine(root, "files"),
                    ["Books:CoversPath"] = Path.Combine(root, "covers"),
                    ["Books:DerivedPath"] = Path.Combine(root, "derived"),
                    ["Books:Translation:MemoryPath"] = Path.Combine(root, "memory")
                })
                .Build();
            return new Fixture(root, db, new BookCatalogService(new HttpClient(), db, new NoopBookTranslator(), configuration));
        }

        public async Task<Guid> ImportAsync(byte[] pdf)
        {
            var source = Path.Combine(root, "novel.pdf");
            await File.WriteAllBytesAsync(source, pdf);
            return await Service.ImportPdfFileAsync(source, "novel.pdf", "upload", hint: null, CancellationToken.None);
        }

        public async Task<List<NovelChapter>> ChaptersAsync(Guid workId)
        {
            Db.ChangeTracker.Clear();
            return await Db.NovelChapters.AsNoTracking().Where(x => x.WorkId == workId).OrderBy(x => x.Number).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoopBookTranslator : IBookTranslator
    {
        public string Id => "noop";

        public Task<string> TranslateLiteraryAsync(string sourceText, string sourceLanguage, string targetLanguage, string context, CancellationToken cancellationToken) =>
            Task.FromResult(sourceText);
    }
}
