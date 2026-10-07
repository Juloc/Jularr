using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

// Issue #406: Books covers move beside the EPUB/PDF on the configured Books NAS library root
// (#389/#545) instead of only living under /data/books/covers, through the one artwork service
// (BesideMediaArtworkStore) #570's future local cache will sit in front of.
[TestClass]
public sealed class BookCoverArtworkTests
{
    [TestMethod]
    public async Task EmbeddedCoverIsPersistedBesideTheBookOnTheConfiguredNasRootAndRecorded()
    {
        await using var fixture = await Fixture.CreateAsync(withLibraryRoot: true);
        var epubPath = Path.Combine(fixture.LibraryRoot!, "Dune", "Dune.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(epubPath)!);
        await File.WriteAllBytesAsync(epubPath, BuildEpubWithCover());

        var workId = (await fixture.Service.ImportBooksFromPathAsync(
            epubPath, "download", hint: null, singleBook: true, CancellationToken.None, preserveSourceFiles: true)).Single();

        var coverPath = Path.Combine(Path.GetDirectoryName(epubPath)!, "cover.png");
        Assert.IsTrue(File.Exists(coverPath), "The embedded cover is written beside the EPUB.");
        CollectionAssert.AreEqual(EpubTestBuilder.Png, await File.ReadAllBytesAsync(coverPath));

        var resolved = await fixture.Service.GetLocalCoverPathAsync(workId, null, CancellationToken.None);
        Assert.AreEqual(coverPath, resolved, "Library/detail rendering reads the same beside-media file.");

        var assets = new MediaArtworkAssetStore(fixture.Db);
        var rows = await assets.ListAsync(MediaArtworkScopes.Book, workId, CancellationToken.None);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("cover.png", rows[0].FileName);

        var dataCovers = fixture.CoversPath;
        Assert.IsFalse(
            Directory.Exists(dataCovers) && Directory.EnumerateFiles(dataCovers, workId.ToString("N") + ".*").Any(),
            "No leftover /data copy once the NAS copy is canonical.");
    }

    [TestMethod]
    public async Task UserPlacedCoverBesideTheBookIsNeverReplaced()
    {
        await using var fixture = await Fixture.CreateAsync(withLibraryRoot: true);
        var folder = Path.Combine(fixture.LibraryRoot!, "Dune");
        Directory.CreateDirectory(folder);
        var epubPath = Path.Combine(folder, "Dune.epub");
        await File.WriteAllBytesAsync(epubPath, BuildEpubWithCover());

        var custom = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9, 0xFF, 0xD9 };
        await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), custom);

        var workId = (await fixture.Service.ImportBooksFromPathAsync(
            epubPath, "download", hint: null, singleBook: true, CancellationToken.None, preserveSourceFiles: true)).Single();

        CollectionAssert.AreEqual(custom, await File.ReadAllBytesAsync(Path.Combine(folder, "cover.jpg")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, "cover.png")), "No provider copy is added next to the user's cover.");
        Assert.AreEqual($"/Books/Cover/{workId}", (await fixture.Db.NovelWorks.AsNoTracking().SingleAsync(x => x.Id == workId)).CoverImageUrl);
    }

    [TestMethod]
    public async Task TargetedEpubImportUpdatesExistingWorkWithoutCreatingDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync(withLibraryRoot: false);
        await using var original = new MemoryStream(
            new EpubTestBuilder
            {
                Title = "Dune",
                Author = "Frank Herbert",
                Language = "en",
                Identifier = "urn:uuid:dune-original"
            }
                .Chapter("c1.xhtml", "Book One", "Original chapter text.")
                .BuildBytes());

        var workId = await fixture.Service.ImportUploadedEpubAsync(
            original,
            "dune.epub",
            CancellationToken.None);

        var work = await fixture.Db.NovelWorks.SingleAsync(x => x.Id == workId);
        work.MetadataProvider = BookCatalogService.CatalogRequestProvider;
        work.MetadataExternalId = "ol-dune";
        work.MetadataTitle = "Dune";
        await fixture.Db.SaveChangesAsync();

        var replacementPath = Path.Combine(fixture.TempRoot, "dune-retail.epub");
        await File.WriteAllBytesAsync(
            replacementPath,
            new EpubTestBuilder
            {
                Title = "Dune",
                Author = "Frank Herbert",
                Language = "en",
                Identifier = "urn:uuid:dune-retail"
            }
                .Chapter("c1.xhtml", "Book One", "Replacement chapter text.")
                .BuildBytes());

        var imported = await fixture.Service.ImportBooksFromPathAsync(
            replacementPath,
            "download",
            new BookImportHint(
                "ol-dune",
                "Dune",
                "Frank Herbert",
                null,
                ExistingWorkId: workId),
            singleBook: true,
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { workId }, imported.ToArray());
        Assert.AreEqual(1, await fixture.Db.NovelWorks.CountAsync());

        fixture.Db.ChangeTracker.Clear();
        var preserved = await fixture.Db.NovelWorks.AsNoTracking().SingleAsync(x => x.Id == workId);
        Assert.AreEqual(BookCatalogService.CatalogRequestProvider, preserved.MetadataProvider);
        Assert.AreEqual("ol-dune", preserved.MetadataExternalId);
        Assert.AreEqual("Dune", preserved.MetadataTitle);
        Assert.AreEqual("EPUB:en", preserved.Format);

        var editions = await fixture.Db.BookEditions
            .AsNoTracking()
            .Where(x => x.WorkId == workId)
            .ToArrayAsync();
        Assert.AreEqual(2, editions.Length);
        Assert.AreEqual(1, editions.Count(x => x.IsPrimary));

        var chapter = await fixture.Db.NovelChapters
            .AsNoTracking()
            .SingleAsync(x => x.WorkId == workId && x.Number == 1);
        StringAssert.Contains(chapter.OriginalText, "Replacement chapter text.");
    }

    [TestMethod]
    public async Task TargetedPdfImportKeepsExistingCatalogIdentity()
    {
        await using var fixture = await Fixture.CreateAsync(withLibraryRoot: false);
        await using var original = new MemoryStream(
            new EpubTestBuilder
            {
                Title = "Dune",
                Author = "Frank Herbert",
                Language = "en",
                Identifier = "urn:uuid:dune-before-pdf"
            }
                .Chapter("c1.xhtml", "Book One", "Original chapter text.")
                .BuildBytes());

        var workId = await fixture.Service.ImportUploadedEpubAsync(
            original,
            "dune.epub",
            CancellationToken.None);
        var work = await fixture.Db.NovelWorks.SingleAsync(x => x.Id == workId);
        work.MetadataProvider = BookCatalogService.CatalogRequestProvider;
        work.MetadataExternalId = "ol-dune";
        work.MetadataTitle = "Dune";
        await fixture.Db.SaveChangesAsync();

        var pdfPath = Path.Combine(fixture.TempRoot, "dune.pdf");
        await File.WriteAllTextAsync(pdfPath, "%PDF-1.4\n%%EOF\n");

        var imported = await fixture.Service.ImportPdfFileAsync(
            pdfPath,
            "dune.pdf",
            "download",
            new BookImportHint(
                "ol-dune",
                "Dune",
                "Frank Herbert",
                null,
                ExistingWorkId: workId),
            CancellationToken.None);

        Assert.AreEqual(workId, imported);
        Assert.AreEqual(1, await fixture.Db.NovelWorks.CountAsync());

        fixture.Db.ChangeTracker.Clear();
        var preserved = await fixture.Db.NovelWorks.AsNoTracking().SingleAsync(x => x.Id == workId);
        Assert.AreEqual(BookCatalogService.CatalogRequestProvider, preserved.MetadataProvider);
        Assert.AreEqual("ol-dune", preserved.MetadataExternalId);
        Assert.AreEqual("Dune", preserved.MetadataTitle);
        StringAssert.StartsWith(preserved.Format, "PDF:");

        var primary = await (
            from edition in fixture.Db.BookEditions.AsNoTracking()
            join file in fixture.Db.BookFiles.AsNoTracking() on edition.Id equals file.EditionId
            where edition.WorkId == workId && edition.IsPrimary && file.IsPrimary
            select file).SingleAsync();
        Assert.AreEqual(BookFileFormats.Pdf, primary.Format);
    }

    [TestMethod]
    public async Task NoLibraryRootConfiguredKeepsUsingDataCovers()
    {
        await using var fixture = await Fixture.CreateAsync(withLibraryRoot: false);
        await using var epub = EpubWithCoverStream();

        var workId = await fixture.Service.ImportUploadedEpubAsync(epub, "dune.epub", CancellationToken.None);

        var resolved = await fixture.Service.GetLocalCoverPathAsync(workId, null, CancellationToken.None);
        Assert.IsNotNull(resolved);
        StringAssert.StartsWith(resolved, fixture.CoversPath, "No NAS root configured: unchanged /data behavior.");
    }

    private static byte[] BuildEpubWithCover() =>
        new EpubTestBuilder { Title = "Dune", Author = "Frank Herbert", Language = "en", Identifier = "urn:uuid:dune-nas", CoverPath = "cover.png" }
            .Image("cover.png")
            .Chapter("c1.xhtml", "Book One", "A beginning is the time for taking the most delicate care.")
            .BuildBytes();

    private static MemoryStream EpubWithCoverStream() =>
        new(new EpubTestBuilder { Title = "Dune", Author = "Frank Herbert", Language = "en", Identifier = "urn:uuid:dune-plain-data", CoverPath = "cover.png" }
            .Image("cover.png")
            .Chapter("c1.xhtml", "Book One", "Arrakis, the desert planet.")
            .BuildBytes());

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string tempRoot, AppDbContext db, BookCatalogService service, string? libraryRoot, string coversPath)
        {
            TempRoot = tempRoot;
            Db = db;
            Service = service;
            LibraryRoot = libraryRoot;
            CoversPath = coversPath;
        }

        public string TempRoot { get; }
        public AppDbContext Db { get; }
        public BookCatalogService Service { get; }
        public string? LibraryRoot { get; }
        public string CoversPath { get; }

        public static async Task<Fixture> CreateAsync(bool withLibraryRoot)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"jularr-book-artwork-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            var coversPath = Path.Combine(tempRoot, "data-covers");

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(tempRoot, "jularr.db")};Pooling=False")
                .Options;
            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var importSettings = new AnimeImportSettingsStore(tempRoot);
            string? libraryRoot = null;
            if (withLibraryRoot)
            {
                libraryRoot = Path.Combine(tempRoot, "media-books");
                Directory.CreateDirectory(libraryRoot);
                await ReadingTestRoots.AssignAsync(db, MediaAcquisitionKind.Book, libraryRoot);
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:CoversPath"] = coversPath,
                    ["Books:FilesPath"] = Path.Combine(tempRoot, "data-files"),
                    ["Books:DerivedPath"] = Path.Combine(tempRoot, "data-derived"),
                    ["Books:Translation:MemoryPath"] = Path.Combine(tempRoot, "translation-memory")
                })
                .Build();

            var service = new BookCatalogService(
                new HttpClient(),
                db,
                new NoopBookTranslator(),
                config,
                dataProtectionProvider: null,
                discoverySettingsDirectory: null,
                importSettings: importSettings,
                routing: new Jularr.Web.Features.Storage.LibraryRootRoutingService(db));

            return new Fixture(tempRoot, db, service, libraryRoot, coversPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class NoopBookTranslator : IBookTranslator
    {
        public string Id => "noop";

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken) =>
            Task.FromResult(sourceText);
    }
}
