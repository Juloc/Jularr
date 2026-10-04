using System.Text.RegularExpressions;
using Jint;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Infrastructure;
using Jularr.Web.Pages.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>
/// PDF books open in the paper Books reader: pdf.js (vendored under
/// wwwroot/lib/pdfjs) draws the pages inside the reader frame, never a browser
/// PDF viewer; pages are the work's chapters for progress and bookmarks.
/// </summary>
[TestClass]
public sealed class BookReaderPdfTests
{
    private const string PdfJsVersion = "6.3.289";
    private const string Profile = "pdf-reader";

    /// <summary>A PDF book must always offer the language menu and read or continue translating its pages as text.</summary>
    [TestMethod]
    public void PdfBooksAlwaysOfferTheLanguageMenuAndPageTranslation()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");

        Assert.IsFalse(page.Contains("hasLanguageChoice", StringComparison.Ordinal));
        StringAssert.Contains(page, "data-book-pdf-translation");
        StringAssert.Contains(page, "data-book-pdf-translate-form");
        StringAssert.Contains(script, "loadPdfTranslation");
        StringAssert.Contains(script, "\"TranslationStatus\"");
        StringAssert.Contains(script, "\"Translate\"");
    }

    /// <summary>Partial PDF translation never overlays the source page: pending keeps the original readable and ready content replaces it.</summary>
    [TestMethod]
    public void PdfPageTranslationUsesReplacementWithOriginalFallbackInsteadOfOverlay()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");
        var styles = Read("src", "Jularr.Web", "wwwroot", "css", "book-reader.css");

        StringAssert.Contains(page, "data-book-pdf-translation-label");
        StringAssert.Contains(page, "books.read.generatedTranslation");
        StringAssert.Contains(script, "books.read.translationRetry");
        StringAssert.Contains(script, "books.read.translationNotReady");
        StringAssert.Contains(styles, ".book-reader-page[data-book-format=\"pdf\"] .book-stage:has(> .book-pdf-translation[data-state=\"ready\"]:not([hidden])) > .book-pdf-spread");
        StringAssert.Contains(styles, ".book-pdf-translation {\n    position: relative;");
        StringAssert.Contains(styles, ".book-pdf-translation[data-state=\"pending\"]");
        Assert.IsFalse(styles.Contains(".book-pdf-translation {\n    position: absolute;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PdfBooksRenderInsideThePaperReaderFrame()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");
        var adapter = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader-pdf.js");

        StringAssert.Contains(page, "data-reader-frame");
        StringAssert.Contains(page, "data-book-pdf");
        StringAssert.Contains(page, "~/js/fixed-page-reader.js");
        StringAssert.Contains(page, "~/js/books-reader-pdf.js");
        Assert.IsTrue(
            page.IndexOf("~/js/fixed-page-reader.js", StringComparison.Ordinal) <
            page.IndexOf("~/js/books-reader-pdf.js", StringComparison.Ordinal),
            "The canonical fixed-page runtime must load before the PDF adapter.");
        StringAssert.Contains(page, "/lib/pdfjs/pdf.min.mjs");
        StringAssert.Contains(page, "/lib/pdfjs/pdf.worker.min.mjs");
        foreach (var source in new[] { page, adapter })
        {
            foreach (var viewer in new[] { "<iframe", "<embed", "<object", "application/pdf" })
            {
                Assert.IsFalse(source.Contains(viewer, StringComparison.OrdinalIgnoreCase), $"No browser PDF viewer ({viewer}).");
            }
        }

        // Text-only controls are not rendered for fixed pages; zoom replaces them.
        StringAssert.Contains(page, "@if (!isPdf)");
        StringAssert.Contains(page, "data-book-pdf-fit=\"width\"");
        StringAssert.Contains(page, "data-book-pdf-zoom=\"1\"");
        StringAssert.Contains(adapter, "[data-book-pdf-fit]");
        StringAssert.Contains(adapter, "[data-book-pdf-zoom]");

        // The text layer is the read-aloud paragraph; images keep their colours.
        StringAssert.Contains(adapter, "text.dataset.readerParagraph");
        StringAssert.Contains(adapter, "recordImages: true");
        StringAssert.Contains(adapter, "devicePixelRatio");

        // Every Read link of a book, PDF or EPUB, is the Books reader itself.
        var file = Read("src", "Jularr.Web", "Pages", "Books", "File.cshtml.cs");
        StringAssert.Contains(file, "EnableRangeProcessing = true");
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path).Contains("href=\"/Books/File/", StringComparison.Ordinal)),
            "Nothing links to the raw PDF file.");
    }

    [TestMethod]
    public void PdfJsIsVendoredWithItsLicenceAndPinnedVersion()
    {
        var lib = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "lib", "pdfjs");

        StringAssert.StartsWith(File.ReadAllText(Path.Combine(lib, "VERSION")), $"pdfjs-dist {PdfJsVersion} ");
        StringAssert.Contains(File.ReadAllText(Path.Combine(lib, "LICENSE")), "Apache License");
        StringAssert.Contains(File.ReadAllText(Path.Combine(lib, "pdf.min.mjs")), PdfJsVersion);
        StringAssert.Contains(File.ReadAllText(Path.Combine(lib, "pdf.worker.min.mjs")), PdfJsVersion);
        Assert.IsTrue(File.Exists(Path.Combine(lib, "wasm", "openjpeg.wasm")));
        Assert.IsTrue(File.Exists(Path.Combine(lib, "standard_fonts", "LiberationSans-Regular.ttf")));

        // The static file server does not know .bcmap; the reader loads "*.bcmap.bin".
        var cmaps = Directory.GetFiles(Path.Combine(lib, "cmaps"));
        Assert.IsTrue(cmaps.Any(path => path.EndsWith("Adobe-Japan1-UCS2.bcmap.bin", StringComparison.Ordinal)));
        Assert.IsFalse(cmaps.Any(path => path.EndsWith(".bcmap", StringComparison.Ordinal)));
        StringAssert.Contains(Read("src", "Jularr.Web", "wwwroot", "js", "books-reader-pdf.js"), "kind === \"cMapUrl\" ? \".bin\" : \"\"");
    }

    [TestMethod]
    public void FixedPageBooksReadAloudAndOfferZoomInsteadOfTypography()
    {
        var pdf = ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.Book, "PDF", layoutKind: ReaderLayoutKind.FixedPages);

        Assert.IsTrue(pdf.Capabilities.SupportsTts);
        Assert.IsTrue(pdf.Capabilities.SupportsZoom);
        Assert.IsTrue(pdf.Capabilities.SupportsPaged);
        Assert.IsTrue(pdf.Capabilities.SupportsTwoPage);
        Assert.IsFalse(pdf.Capabilities.SupportsTypography);
        Assert.IsFalse(pdf.Capabilities.SupportsAutoScroll);
        Assert.IsFalse(pdf.Capabilities.SupportsTranslation);
        Assert.IsFalse(ReaderDocumentDescriptor.Create(Guid.NewGuid(), ReaderContentType.FixedDocument, "t").Capabilities.SupportsTts);
    }

    [TestMethod]
    public void PagesMapToTheirChaptersAndBack()
    {
        var engine = CreateEngine();
        engine.Execute("""
            var exact = JularrBookPdf.pageMap(3, ["A", "B", "C"]);
            var linear = JularrBookPdf.pageMap(300, ["ONLY"]);
            var roundTrip = true;
            for (var page = 1; page <= 300; page++) {
                var position = linear.positionForPage(page);
                if (linear.pageForPosition(position.chapterId, position.positionPermille) !== page) roundTrip = false;
            }
            """);

        Assert.AreEqual("B", engine.Evaluate("exact.positionForPage(2).chapterId").AsString());
        Assert.AreEqual(0, engine.Evaluate("exact.positionForPage(2).positionPermille").AsNumber());
        Assert.AreEqual(3, engine.Evaluate("exact.pageForPosition('c', 0)").AsNumber(), "Chapter ids compare case-insensitively.");
        Assert.AreEqual(3, engine.Evaluate("exact.positionForPage(99) && exact.pageForPosition(exact.positionForPage(99).chapterId, 0)").AsNumber());
        Assert.IsTrue(engine.Evaluate("exact.pageForPosition('missing', 0) === null").AsBoolean());
        Assert.IsTrue(engine.Evaluate("roundTrip").AsBoolean(), "Every page survives a save and resume when pages outnumber chapters.");
    }

    [TestMethod]
    public void SpreadsKeepTheCoverAloneThenPairPages()
    {
        var engine = CreateEngine();

        Assert.AreEqual("[[1],[2,3],[4,5]]", engine.Evaluate("""
            (() => { const s = JularrBookPdf.spreads(5, 2); return JSON.stringify([0, 1, 2].map(s.pagesOfView)); })()
            """).AsString());
        Assert.AreEqual(3, engine.Evaluate("JularrBookPdf.spreads(4, 2).count").AsNumber());
        Assert.AreEqual("[4]", engine.Evaluate("JSON.stringify(JularrBookPdf.spreads(4, 2).pagesOfView(2))").AsString());
        Assert.AreEqual(2, engine.Evaluate("JularrBookPdf.spreads(5, 2).viewOfPage(5)").AsNumber());
        Assert.AreEqual(4, engine.Evaluate("JularrBookPdf.spreads(5, 1).viewOfPage(5)").AsNumber());
        Assert.AreEqual(5, engine.Evaluate("JularrBookPdf.spreads(5, 1).count").AsNumber());
    }

    [TestMethod]
    public void SearchMatchesKeepTextLayerOffsets()
    {
        var engine = CreateEngine();
        engine.Execute("""
            var text = "Chapter I. To deliver you from the preliminary terrors. The preliminary   terror chokes off most boys.";
            var hits = JularrBookPdf.findMatches(text, "PRELIMINARY", 12, 10);
            var capped = JularrBookPdf.findMatches(text, "preliminary", 12, 1);
            var tooShort = JularrBookPdf.findMatches(text, "t", 12, 10);
            """);

        Assert.AreEqual(2, engine.Evaluate("hits.length").AsNumber());
        Assert.AreEqual(12, engine.Evaluate("hits[0].page").AsNumber());
        Assert.AreEqual("preliminary", engine.Evaluate("text.slice(hits[1].start, hits[1].end)").AsString());
        Assert.AreEqual("preliminary", engine.Evaluate("hits[1].snippet.substr(hits[1].matchStart, hits[1].matchLength)").AsString());
        Assert.IsFalse(engine.Evaluate("hits[1].snippet").AsString().Contains("  ", StringComparison.Ordinal));
        Assert.AreEqual(1, engine.Evaluate("capped.length").AsNumber());
        Assert.AreEqual(0, engine.Evaluate("tooShort.length").AsNumber());
    }

    [TestMethod]
    public void ReadAloudTurnsToTheNextPdfPage()
    {
        var tts = Read("src", "Jularr.Web", "wwwroot", "js", "reader-tts.js");
        var adapter = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader-pdf.js");

        StringAssert.Contains(tts, "jularr:reader-tts-next-page");
        StringAssert.Contains(tts, "if (continueOnNextPage(currentPlan)) return;");
        StringAssert.Contains(adapter, "root.addEventListener(\"jularr:reader-tts-next-page\"");
        StringAssert.Contains(adapter, "request.ready = (async () => {");
    }

    [TestMethod]
    public void SliderValueIsTheLastPageShownSoItMatchesThePercentage()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");

        // "1 / 2 (50%)" with the handle at the start came from a view-index slider
        // and a page-based percentage. Both now use the last page on screen.
        StringAssert.Contains(script, "value: last,\n                max: layout.pageCount,\n                percent: Math.round(last / layout.pageCount * 100)");
        StringAssert.Contains(script, "goToView(viewForSliderPage(value), { animate: false })");
        StringAssert.Contains(script, "value: last,\n                    max: total,\n                    percent: Math.round(last / total * 100)");
        StringAssert.Contains(script, "addEventListener(\"change\"");
        Assert.IsFalse(script.Contains("max = layout.viewCount - 1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PdfReaderTextKeysExist()
    {
        foreach (var file in new[] { "books-reader-pdf.js", "books-reader.js" })
        {
            var script = Read("src", "Jularr.Web", "wwwroot", "js", file);
            foreach (Match key in Regex.Matches(script, "t\\(\"(?<key>(?:books\\.read|reader\\.frame)\\.[A-Za-z.]+)\""))
            {
                Assert.IsTrue(UiTranslationResources.TryGet(key.Groups["key"].Value, out _), $"{file}: missing {key.Groups["key"].Value}.");
            }
        }

        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");
        foreach (Match key in Regex.Matches(page, "ui\\[\"(?<key>books\\.read\\.[A-Za-z.]+)\"\\]"))
        {
            Assert.IsTrue(UiTranslationResources.TryGet(key.Groups["key"].Value, out _), $"Read.cshtml: missing {key.Groups["key"].Value}.");
        }
    }

    [TestMethod]
    public async Task ReadPageGivesPdfBooksTheirPagesAndFile()
    {
        await using var fixture = await Fixture.CreateAsync();

        var reader = fixture.CreateReadModel();
        await reader.OnGetAsync(fixture.PageIds[1], "en", null, null, null, CancellationToken.None);

        Assert.IsNotNull(reader.Pdf);
        Assert.AreEqual($"/Books/File/{fixture.WorkId}", reader.Pdf.FileUrl);
        CollectionAssert.AreEqual(fixture.PageIds, reader.Pdf.PageChapterIds.ToArray(), "Pages in page order.");
        Assert.AreEqual(3, reader.ChapterCount);
        Assert.AreEqual(ReaderLayoutKind.FixedPages, reader.ReaderDocument.LayoutKind);
        Assert.IsTrue(reader.ReaderDocument.Capabilities.SupportsTts);

        File.Delete(fixture.PdfPath);
        var missing = fixture.CreateReadModel();
        await missing.OnGetAsync(fixture.PageIds[0], "en", null, null, null, CancellationToken.None);
        Assert.IsNotNull(missing.Pdf);
        Assert.IsNull(missing.Pdf.FileUrl, "A missing file is reported, not replaced by the page text.");
    }

    [TestMethod]
    public async Task PdfReaderExposesSharedCachedTranslationLanguagesAcrossProfiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedTranslationAsync(fixture.PageIds[1], "de", "Übersetzte Seite.");

        var first = fixture.CreateReadModel("pdf-reader-a");
        await first.OnGetAsync(fixture.PageIds[1], "id", null, null, null, CancellationToken.None);
        var second = fixture.CreateReadModel("pdf-reader-b");
        await second.OnGetAsync(fixture.PageIds[1], "id", null, null, null, CancellationToken.None);

        CollectionAssert.Contains(first.CachedTranslationLanguages.ToArray(), "de");
        CollectionAssert.Contains(second.CachedTranslationLanguages.ToArray(), "de");
        Assert.IsTrue(second.ReaderDocument.Languages.Any(language => language.Tag.Equals("de", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ReaderResumesOnlyInTheChapterOfTheSavedProgress()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Books.SaveProgressAsync(Profile, fixture.WorkId, fixture.PageIds[1], 400, "en", CancellationToken.None);

        var saved = fixture.CreateReadModel();
        await saved.OnGetAsync(fixture.PageIds[1], "en", null, null, null, CancellationToken.None);
        Assert.AreEqual(400, saved.InitialPositionPermille);

        var other = fixture.CreateReadModel();
        await other.OnGetAsync(fixture.PageIds[2], "en", null, null, null, CancellationToken.None);
        Assert.AreEqual(0, other.InitialPositionPermille);

        var requested = fixture.CreateReadModel();
        await requested.OnGetAsync(fixture.PageIds[2], "en", 700, null, null, CancellationToken.None);
        Assert.AreEqual(700, requested.InitialPositionPermille);
    }

    private static Engine CreateEngine()
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "fixed-page-reader.js"));
        engine.Execute(Read("src", "Jularr.Web", "wwwroot", "js", "books-reader-pdf.js"));
        return engine;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ServiceProvider services;

        private Fixture(string directory, ServiceProvider services, AppDbContext db, Guid workId, Guid[] pageIds, string pdfPath)
        {
            this.directory = directory;
            this.services = services;
            Db = db;
            WorkId = workId;
            PageIds = pageIds;
            PdfPath = pdfPath;
            Books = NewBookCatalogService();
        }

        public AppDbContext Db { get; }
        public Guid WorkId { get; }
        public Guid[] PageIds { get; }
        public string PdfPath { get; }
        public BookCatalogService Books { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-pdf-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={Path.Combine(directory, "jularr.db")};Foreign Keys=True";
            var services = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString))
                .BuildServiceProvider();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var pdfPath = Path.Combine(directory, "book.pdf");
            await File.WriteAllTextAsync(pdfPath, "%PDF-1.4\n%%EOF\n");

            var work = new NovelWork
            {
                SourceProvider = BookCatalogService.ImportedBookProvider,
                SourceKey = "pdf-" + Guid.NewGuid().ToString("N"),
                SourceUrl = "upload://book.pdf",
                Title = "Calculus Made Easy",
                Format = BookFileFormats.Pdf + ":en"
            };
            var volume = new NovelVolume { WorkId = work.Id, Number = 1, Kind = NovelVolumeKinds.Book, SourceKey = "book" };
            // Inserted out of order: the reader must order pages by number.
            var pages = new[] { 3, 1, 2 }
                .Select(number => new NovelChapter
                {
                    WorkId = work.Id,
                    VolumeId = volume.Id,
                    Number = number,
                    Title = number.ToString(),
                    SourceUrl = $"book://{work.Id:N}/{number}",
                    OriginalText = $"Page {number} text.",
                    SourceHash = $"hash-{number}"
                })
                .ToArray();
            var edition = new BookEdition { WorkId = work.Id, EditionKey = work.SourceKey, Language = "en", Title = work.Title };
            var file = new BookFile
            {
                EditionId = edition.Id,
                FileKey = work.SourceKey,
                FileName = "book.pdf",
                Format = BookFileFormats.Pdf,
                MediaType = BookFileFormats.PdfMediaType,
                SourceKind = "upload",
                ContentHash = "hash",
                SizeBytes = 16,
                StoragePath = pdfPath
            };

            db.AddRange(work, volume, edition, file);
            db.AddRange(pages);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new Fixture(
                directory,
                services,
                db,
                work.Id,
                pages.OrderBy(x => x.Number).Select(x => x.Id).ToArray(),
                pdfPath);
        }

        public ReadModel CreateReadModel(string profileId = Profile)
        {
            var page = new ReadModel(
                Books,
                TestAccounts.Context(profileId),
                new BackgroundJobQueue(services.GetRequiredService<IServiceScopeFactory>()),
                Db);
            page.PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>()
                        .BuildServiceProvider()
                },
                ViewData = new ViewDataDictionary<ReadModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            };
            return page;
        }

        public async Task SeedTranslationAsync(Guid pageId, string targetLanguage, string text)
        {
            var chapter = await Db.NovelChapters.SingleAsync(x => x.Id == pageId);
            Db.NovelTranslations.Add(new NovelTranslation
            {
                ChapterId = pageId,
                TargetLanguage = BookLanguageCatalog.Normalize(targetLanguage),
                ProviderId = $"book-v{BookCatalogService.TranslationPromptVersion}-efficient",
                PromptVersion = BookCatalogService.TranslationPromptVersion,
                SourceHash = chapter.SourceHash,
                Text = text
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private BookCatalogService NewBookCatalogService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:Translation:MemoryPath"] = Path.Combine(directory, "translation-memory")
                })
                .Build();
            return new BookCatalogService(new HttpClient(), Db, new UnusedTranslator(), config);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await services.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private sealed class UnusedTranslator : IBookTranslator
        {
            public string Id => "unused";

            public Task<string> TranslateLiteraryAsync(
                string sourceText,
                string sourceLanguage,
                string targetLanguage,
                string context,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("No translation in PDF reader tests.");
        }
    }
}
