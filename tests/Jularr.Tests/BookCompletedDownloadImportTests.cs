using System.IO.Compression;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// Books on the shared pipeline (#389): completed downloads go through the Book adapter behind
/// the completed-download dispatcher, manual downloads and inbox folders use the same importer,
/// and the old Books inbox moves once into the per-media inbox folders.
/// </summary>
[TestClass]
public sealed class BookCompletedDownloadImportTests
{
    [TestMethod]
    public async Task InboxScanImportsEveryBookOnceAsARecordedOperation()
    {
        await using var host = await Host.CreateAsync();
        var inbox = host.Folder("inbox");
        var job = Directory.CreateDirectory(Path.Combine(inbox, "Some.Book.EPUB-GROUP")).FullName;
        await WriteEpubAsync(Path.Combine(job, "book.epub"));
        await host.SetInboxAsync(MediaAcquisitionKind.Book, inbox);

        var first = await host.Inboxes.RunAsync(MediaAcquisitionKind.Book, "owner", CancellationToken.None);
        var second = await host.Inboxes.RunAsync(MediaAcquisitionKind.Book, "owner", CancellationToken.None);

        Assert.AreEqual(1, first.Imported, first.Message);
        Assert.AreEqual(1, await host.Db.NovelWorks.CountAsync(), "A rescan never duplicates a book.");
        StringAssert.StartsWith(first.ResultUrl, "/Books/Library/");
        Assert.AreEqual(first.ResultUrl, second.ResultUrl);

        var operations = await new OperationStore(host.Db).ListAsync(
            new OperationListFilter(Kind: MediaInboxImportService.OperationKind),
            CancellationToken.None);
        Assert.AreEqual(2, operations.Count);
        Assert.IsTrue(operations.All(operation => operation.Status == OperationStatus.Succeeded && operation.ProfileId == "owner"));
        Assert.AreEqual(Path.GetFullPath(inbox), operations[0].Subject);
    }

    [TestMethod]
    public async Task BooksInboxLeavesANestedLightNovelInboxAlone()
    {
        await using var host = await Host.CreateAsync();
        var inbox = host.Folder("books-inbox");
        var lightNovels = Directory.CreateDirectory(Path.Combine(inbox, "light-novels")).FullName;
        await WriteEpubAsync(Path.Combine(lightNovels, "volume.epub"));
        await host.SetInboxAsync(MediaAcquisitionKind.Book, inbox);
        await host.SetInboxAsync(MediaAcquisitionKind.LightNovel, lightNovels);

        var result = await host.Inboxes.RunAsync(MediaAcquisitionKind.Book, "owner", CancellationToken.None);

        Assert.AreEqual(0, result.Imported, result.Message);
        Assert.AreEqual(0, await host.Db.NovelWorks.CountAsync());
    }

    [TestMethod]
    public async Task InboxScanNeedsAConfiguredAndAvailableFolder()
    {
        await using var host = await Host.CreateAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.Inboxes.RunAsync(MediaAcquisitionKind.Book, "owner", CancellationToken.None));

        await host.SetInboxAsync(MediaAcquisitionKind.Book, Path.Combine(host.Root, "offline"));
        var offline = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.Inboxes.RunAsync(MediaAcquisitionKind.Book, "owner", CancellationToken.None));
        StringAssert.Contains(offline.Message, "offline");
    }

    [TestMethod]
    public async Task CompletedDownloadIsImportedFromItsJobFolderAndLinkedToTheRequest()
    {
        await using var host = await Host.CreateAsync();
        var job = Directory.CreateDirectory(Path.Combine(host.Folder("complete"), "Dune.2021", "nested")).FullName;
        await WriteEpubAsync(Path.Combine(job, "dune.epub"));
        var request = await host.Requests.CreateAsync(
            new AcquisitionRequestDraft(MediaAcquisitionKind.Book, "books-catalog", "ol:dune", "Something else entirely", null, null),
            "alice",
            AcquisitionRequestStatus.Downloading,
            "owner",
            CancellationToken.None);
        var phases = new List<CompletedDownloadImportPhase>();

        var result = await host.Adapter.ImportAsync(
            new CompletedDownloadImportRequest(
                request,
                null,
                Path.GetDirectoryName(job)!,
                ProgressReporter: progress =>
                {
                    phases.Add(progress.Phase);
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        var work = await host.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        Assert.AreEqual($"/Books/Library/{work.Id}", result.ResultUrl);
        Assert.AreEqual("ol:dune", work.MetadataExternalId, "The only book of the job answers the request.");
        Assert.AreEqual(ImportMode.Copy, result.Placement!.Mode, "Book files are read, never moved.");
        Assert.IsTrue(File.Exists(Path.Combine(job, "dune.epub")));
        CollectionAssert.AreEqual(
            new[] { CompletedDownloadImportPhase.MatchingMetadata },
            phases);
    }

    [TestMethod]
    public async Task CompletedDownloadKeepsItsOriginalBookFileInTheConfiguredLibrary()
    {
        await using var host = await Host.CreateAsync();
        var job = host.Folder("complete-book");
        await WriteEpubAsync(Path.Combine(job, "dune.epub"));
        var library = host.Folder("media-books");
        await host.SetLibraryAsync(MediaAcquisitionKind.Book, library, ImportMode.Copy);

        var result = await host.Adapter.ImportAsync(
            new CompletedDownloadImportRequest(null, null, job, MediaAcquisitionKind.Book),
            CancellationToken.None);

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, result.Disposition, result.Message);
        var placement = result.Placement ?? throw new AssertFailedException("The import records its library placement.");
        Assert.AreEqual(Path.Combine(library, "complete-book"), placement.Destination);
        Assert.AreEqual(ImportMode.Copy, placement.Mode);
        Assert.IsTrue(File.Exists(Path.Combine(library, "complete-book", "dune.epub")));
        Assert.IsTrue(File.Exists(Path.Combine(job, "dune.epub")), "Copy leaves the completed download intact.");
        var stored = await host.Db.BookFiles.AsNoTracking().SingleAsync();
        Assert.AreEqual(
            Path.Combine(library, "complete-book", "dune.epub"),
            stored.StoragePath);
    }

    [TestMethod]
    public async Task MissingOrUnsuitableDownloadsAreToldApart()
    {
        await using var host = await Host.CreateAsync();

        var missing = await host.Adapter.ImportAsync(
            new CompletedDownloadImportRequest(null, null, Path.Combine(host.Root, "elsewhere"), MediaAcquisitionKind.Book),
            CancellationToken.None);
        Assert.AreEqual(CompletedDownloadImportDisposition.RetryLater, missing.Disposition, "An unreadable path is not the release's fault.");
        StringAssert.Contains(missing.Message, "elsewhere");

        var job = host.Folder("complete-mobi");
        await File.WriteAllTextAsync(Path.Combine(job, "book.mobi"), "mobi");
        var unsuitable = await host.Adapter.ImportAsync(
            new CompletedDownloadImportRequest(null, null, job, MediaAcquisitionKind.Book),
            CancellationToken.None);
        Assert.AreEqual(CompletedDownloadImportDisposition.RejectedRelease, unsuitable.Disposition);
        Assert.AreEqual(BookCompletedDownloadImportAdapter.NoBookFileReason, unsuitable.Message);
    }

    [TestMethod]
    public async Task ManualDownloadIsImportedOnceAndRecordsTheImportOnTheOperation()
    {
        await using var host = await Host.CreateAsync();
        var job = host.Folder("mnt-books-manual");
        await WriteEpubAsync(Path.Combine(job, "manual.epub"));
        var store = new OperationStore(host.Db);
        var operationId = await store.CreateAsync(
            new OperationDescriptor(
                CompletedDownloadImportService.ManualDownloadOperationKind,
                DownloadClientSubmissionService.OperationCategory,
                "SABnzbd download",
                "Manual book",
                "owner",
                IsDownload: true,
                ExternalProvider: SabnzbdClient.ProviderId,
                ExternalId: "nzo_manual",
                Details: new DownloadOperationDetails(Guid.NewGuid(), MediaAcquisitionKind.Book, "books").Serialize()));
        await store.MarkRunningAsync(operationId);
        await store.MarkSucceededAsync(operationId, "Downloaded.");
        var imports = host.ImportService(new FixedLocation("/downloads/books/manual", job));

        Assert.AreEqual(1, await imports.ImportManualDownloadsAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.AreEqual(0, await imports.ImportManualDownloadsAsync(DateTime.UtcNow, CancellationToken.None), "A finished import is never repeated.");

        Assert.AreEqual(1, await host.Db.NovelWorks.CountAsync());
        var operation = (await store.GetAsync(operationId))!;
        Assert.IsTrue(DownloadOperationDetails.TryParse(operation.Details, out var details));
        var import = details!.Import!;
        Assert.AreEqual(DownloadImportState.Completed, import.State);
        Assert.AreEqual("/downloads/books/manual", import.ReportedPath);
        Assert.AreEqual(job, import.LocalPath);
        Assert.AreEqual(host.Books.FilesPath, import.Destination);
        Assert.AreEqual(ImportMode.Copy, import.Mode);
        Assert.AreEqual(MediaAcquisitionKind.Book, details.MediaKind, "The routing details survive the import record.");
        var logs = await store.ListLogsAsync(new OperationLogFilter(OperationId: operationId));
        CollectionAssert.AreEqual(
            new[]
            {
                "Verifying completed files before import.",
                "Importing completed files into the library.",
                "Imported 1 book(s)."
            },
            logs.Where(entry => entry.Module == "Import").Reverse().Select(entry => entry.Message).ToArray());
    }

    [TestMethod]
    public async Task ManualDownloadWhoseFilesNeverAppearGivesUpAfterTheTimeout()
    {
        await using var host = await Host.CreateAsync();
        var store = new OperationStore(host.Db);
        var operationId = await store.CreateAsync(
            new OperationDescriptor(
                CompletedDownloadImportService.ManualDownloadOperationKind,
                DownloadClientSubmissionService.OperationCategory,
                "SABnzbd download",
                IsDownload: true,
                ExternalProvider: SabnzbdClient.ProviderId,
                ExternalId: "nzo_lost",
                Details: new DownloadOperationDetails(Guid.NewGuid(), MediaAcquisitionKind.Book, "books").Serialize()));
        await store.MarkRunningAsync(operationId);
        await store.MarkSucceededAsync(operationId, "Downloaded.");
        var imports = host.ImportService(new FixedLocation("/downloads/books/lost", Path.Combine(host.Root, "lost")));

        await imports.ImportManualDownloadsAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.IsTrue(DownloadOperationDetails.TryParse((await store.GetAsync(operationId))!.Details, out var waiting));
        Assert.AreEqual(DownloadImportState.Waiting, waiting!.Import!.State);

        await imports.ImportManualDownloadsAsync(
            DateTime.UtcNow + CompletedDownloadImportService.CompletedImportTimeout + TimeSpan.FromMinutes(1),
            CancellationToken.None);
        Assert.IsTrue(DownloadOperationDetails.TryParse((await store.GetAsync(operationId))!.Details, out var gaveUp));
        Assert.AreEqual(DownloadImportState.GaveUp, gaveUp!.Import!.State);
        StringAssert.Contains(gaveUp.Import.Result, "Gave up importing");
    }

    private static async Task WriteEpubAsync(string path)
    {
        await using var file = File.Create(path);
        BuildTestEpub().CopyTo(file);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "jularr-book-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedLocation(string reported, string local) : ICompletedDownloadLocationResolver
    {
        public Task<CompletedDownloadLocation> ResolveAsync(
            OperationSnapshot operation,
            MediaAcquisitionKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CompletedDownloadLocation(true, local, "Completed path resolved.", reported));
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private Host(string root, ServiceProvider services)
        {
            Root = root;
            this.services = services;
        }

        public string Root { get; }
        public AppDbContext Db => services.GetRequiredService<AppDbContext>();
        public BookCatalogService Books => services.GetRequiredService<BookCatalogService>();
        public AcquisitionAccessStore Requests => services.GetRequiredService<AcquisitionAccessStore>();
        public BookCompletedDownloadImportAdapter Adapter => services.GetRequiredService<BookCompletedDownloadImportAdapter>();
        public MediaInboxImportService Inboxes => services.GetRequiredService<MediaInboxImportService>();

        public static async Task<Host> CreateAsync()
        {
            var root = TempDirectory();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:FilesPath"] = Path.Combine(root, "books-files"),
                    ["Books:CoversPath"] = Path.Combine(root, "books-covers"),
                    ["Books:Translation:MemoryPath"] = Path.Combine(root, "translation-memory")
                })
                .Build();

            var services = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<IBookTranslator, NoopBookTranslator>()
                .AddSingleton(provider => new BookCatalogService(
                    new HttpClient(),
                    db,
                    provider.GetRequiredService<IBookTranslator>(),
                    configuration))
                .AddSingleton(provider => new OperationRunner(db, provider))
                .AddSingleton(new AnimeImportSettingsStore(root))
                .AddSingleton<IHardLinkCreator, FileSystemHardLinkCreator>()
                .AddSingleton(new AcquisitionAccessStore(db))
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<BookCompletedDownloadImportAdapter>()
                .AddSingleton<IMediaInboxImportAdapter>(provider => provider.GetRequiredService<BookCompletedDownloadImportAdapter>())
                .AddSingleton<Jularr.Web.Features.Storage.LibraryRootRoutingService>()
                .AddSingleton<MediaInboxImportService>()
                .BuildServiceProvider();
            return new Host(root, services);
        }

        public string Folder(string name) =>
            Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        public Task SetInboxAsync(MediaAcquisitionKind kind, string inbox) =>
            services.GetRequiredService<AnimeImportSettingsStore>().UpdateAsync(state =>
            {
                var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries)
                {
                    [kind] = new MediaLibraryTarget(InboxRoot: inbox)
                };
                return state with { MediaLibraries = libraries };
            });

        public Task SetLibraryAsync(MediaAcquisitionKind kind, string library, ImportMode mode) =>
            services.GetRequiredService<AnimeImportSettingsStore>().UpdateAsync(state =>
            {
                var libraries = new Dictionary<MediaAcquisitionKind, MediaLibraryTarget>(state.MediaLibraries)
                {
                    [kind] = new MediaLibraryTarget(library, mode)
                };
                return state with { MediaLibraries = libraries };
            });

        public CompletedDownloadImportService ImportService(ICompletedDownloadLocationResolver locations) =>
            new(
                locations,
                new CompletedDownloadDispatcher([Adapter]),
                Db,
                Requests,
                NullLogger<CompletedDownloadImportService>.Instance);

        public async ValueTask DisposeAsync()
        {
            var db = Db;
            await services.DisposeAsync();
            await db.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NoopBookTranslator : IBookTranslator
    {
        public string Id => "noop-books";

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken) =>
            Task.FromResult(sourceText);
    }

    private static MemoryStream BuildTestEpub()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            AddEntry(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);

            AddEntry(
                archive,
                "OEBPS/content.opf",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf"
                         xmlns:dc="http://purl.org/dc/elements/1.1/"
                         version="3.0">
                  <metadata>
                    <dc:title>Test Book</dc:title>
                    <dc:creator>Test Author</dc:creator>
                    <dc:language>en</dc:language>
                    <dc:description>A test story.</dc:description>
                    <dc:identifier>urn:isbn:978-0-306-40615-7</dc:identifier>
                    <dc:publisher>Test Publisher</dc:publisher>
                    <dc:date>2026-09-25</dc:date>
                    <dc:subject>Fantasy</dc:subject>
                  </metadata>
                  <manifest>
                    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml" />
                    <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml" />
                  </manifest>
                  <spine>
                    <itemref idref="c1" />
                    <itemref idref="c2" />
                  </spine>
                </package>
                """);

            AddEntry(
                archive,
                "OEBPS/chapter1.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <h1>Chapter One</h1>
                    <p>Hello <em>world</em>.</p>
                    <p>Next paragraph.</p>
                  </body>
                </html>
                """);

            AddEntry(
                archive,
                "OEBPS/chapter2.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <h1>Chapter Two</h1>
                    <p>The story continues here.</p>
                  </body>
                </html>
                """);
        }

        stream.Position = 0;
        return stream;
    }
    private static void AddEntry(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Trim());
    }
}
