using System.Net;
using Jularr.Web.Features.Acquisition.Core;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Reading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Tests;

/// <summary>
/// #404: EPUB and PDF are both book formats (EPUB preferred, PDF accepted as is), a completed
/// download without a usable book continues with the next release, and a request never stays
/// "Downloading" once its download finished — also across a restart.
/// </summary>
[TestClass]
public sealed class BookPdfAcquisitionTests
{
    private const string CatalogId = "ol:OL17930368W";

    [TestMethod]
    public void PdfReaderReturnsPagesInOrderWithTheirTextAndTheInfoMetadata()
    {
        var content = PdfDocumentReader.Read(TestPdf(pages: 4, title: "Atomic Habits", author: "James Clear"));

        Assert.AreEqual(4, content.Pages.Count);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, content.Pages.Select(page => page.Number).ToArray());
        Assert.AreEqual(
            "Page 2 starts with information that continues.\n\nA second paragraph.",
            content.Pages[1].Text,
            "Wrapped lines join (also across a hyphen); a larger gap starts a paragraph.");
        Assert.AreEqual("Atomic Habits", content.Title);
        Assert.AreEqual("James Clear", content.Author);
        Assert.AreEqual("en-GB", content.Language);
        Assert.IsNull(content.CoverJpeg);
        Assert.IsTrue(PdfDocumentReader.HasPdfHeader(TestPdf(pages: 1)));
        Assert.IsFalse(PdfDocumentReader.HasPdfHeader("PK\u0003\u0004 not a pdf"u8));
    }

    [TestMethod]
    public void PdfReaderReadsPackedObjectsCompressedContentUnicodeFontsAndThePageOneCover()
    {
        var content = PdfDocumentReader.Read(TestPdf(pages: 3, packPages: true, compressContent: true, unicodeLastPage: true, cover: true, hexTitle: "FEFF00C4007000660065006C"));

        Assert.AreEqual(3, content.Pages.Count, "Pages inside a compressed object stream are found through the page tree.");
        StringAssert.StartsWith(content.Pages[0].Text, "Page 1 starts with information");
        Assert.AreEqual("Hi!", content.Pages[2].Text, "Two-byte codes are mapped through the font's ToUnicode CMap.");
        Assert.AreEqual("Äpfel", content.Title);
        CollectionAssert.AreEqual(CoverJpeg, content.CoverJpeg);
        Assert.AreEqual(0, PdfDocumentReader.Read(Encoding.Latin1.GetBytes("%PDF-1.7\n%%EOF")).Pages.Count);
    }

    [TestMethod]
    public void DownloadsPreferEpubOverPdfAndTheInboxOnlySkipsPdfTwins()
    {
        var job = new[] { "/job/Atomic Habits.pdf", "/job/sub/Atomic Habits.epub", "/job/cover.jpg", "/job/book.mobi" };
        CollectionAssert.AreEqual(new[] { "/job/sub/Atomic Habits.epub" }, BookFileFormats.Select(job, singleBook: true).ToArray());
        CollectionAssert.AreEqual(new[] { "/job/Atomic Habits.pdf" }, BookFileFormats.Select(["/job/Atomic Habits.pdf", "/job/notes.txt"], singleBook: true).ToArray());

        var inbox = new[] { "/inbox/a.epub", "/inbox/a.pdf", "/inbox/b.pdf" };
        CollectionAssert.AreEqual(new[] { "/inbox/a.epub", "/inbox/b.pdf" }, BookFileFormats.Select(inbox, singleBook: false).ToArray());
    }

    [TestMethod]
    public async Task CompletedPdfDownloadIsImportedAsPdfBookAndClosesTheRequest()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status);

        // SABnzbd reports its own path; the remote path mapping translates it to Jularr's mount.
        var folder = environment.CompletedFolder("Atomic Habits");
        await File.WriteAllBytesAsync(Path.Combine(folder, "James Clear Atomic Habits.pdf"), TestPdf(pages: 3));
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        var stored = await environment.RequestAsync(request.Id);
        var work = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status);
        Assert.AreEqual($"/Books/Library/{work.Id}", stored.ResultUrl);
        Assert.AreEqual("PDF:en", work.Format);
        Assert.AreEqual("Atomic Habits", work.Title, "A PDF is named after the requested book.");
        Assert.AreEqual($"/Books/Cover/{work.Id}", work.CoverImageUrl, "Requested artwork is cached locally.");
        Assert.AreEqual(CatalogId, work.MetadataExternalId, "The work is linked to the requested catalog entry.");

        var file = await environment.Db.BookFiles.AsNoTracking().SingleAsync();
        Assert.AreEqual(BookFileFormats.Pdf, file.Format);
        Assert.AreEqual(BookFileFormats.PdfMediaType, file.MediaType);
        Assert.IsTrue(File.Exists(file.StoragePath));
        StringAssert.StartsWith(file.StoragePath, Path.Combine(environment.Root, "library-books"));

        var state = await environment.AddStateAsync();
        Assert.AreEqual(work.Id, state.LibraryWorkId, "The Add book dialog shows the book in the library, not Downloading.");
        Assert.IsFalse(state.IsInFlight);
    }

    [TestMethod]
    public async Task CompletedDownloadWithEpubAndPdfImportsTheEpub()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB PDF", "both"));
        var request = await environment.AddAsync();

        var folder = environment.CompletedFolder("Atomic Habits");
        await File.WriteAllBytesAsync(Path.Combine(folder, "Atomic Habits.pdf"), TestPdf(pages: 3));
        await using (var epub = File.Create(Path.Combine(folder, "Atomic Habits.epub")))
        {
            new EpubTestBuilder { Title = "Atomic Habits", Author = "James Clear", Language = "en" }
                .Chapter("c1.xhtml", "The Surprising Power of Atomic Habits", "Habits compound.")
                .Build()
                .CopyTo(epub);
        }

        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        var work = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.IsFalse(BookFileFormats.IsPdf(work));
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.RequestAsync(request.Id)).Status);
        Assert.AreEqual(0, await environment.Db.BookFiles.CountAsync(file => file.Format == BookFileFormats.Pdf));
    }

    // A direct source whose one candidate is the requested catalog edition; importing it records the call or fails like a dead mirror.
    private sealed class FakeDirectSource(bool fails) : IDirectSource
    {
        public int Imports { get; private set; }

        public string Name => "fake";

        public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

        public Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(Jularr.Web.Features.Acquisition.Search.SearchIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AcquisitionCandidate>>(
            [
                new AcquisitionCandidate("Atomic Habits [EPUB]", "Free edition", null, "direct", null, null, null, null, null, null, null, null, AnimeReleaseParser.Parse("Atomic Habits [EPUB]"), [], null, null)
                {
                    Type = AcquisitionType.DirectImport,
                    Offer = new DirectOffer("fake", "edition", IdentityIsExact: true)
                }
            ]);

        public Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken)
        {
            Imports++;
            return fails
                ? throw new InvalidOperationException("The mirror is gone.")
                : Task.FromResult(new AcquisitionExecution(AcquisitionRequestStatus.Completed, "Imported a direct/free edition."));
        }
    }

    [TestMethod]
    public async Task ADirectEditionAndAUsenetReleaseCompeteInOneSelectionAndTheWinnerIsRoutedByItsType()
    {
        var direct = new FakeDirectSource(fails: false);
        await using var environment = await BookAcquisitionEnvironment.CreateAsync(direct);
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));

        var request = await environment.AddAsync();

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        Assert.AreEqual(1, direct.Imports, "The free edition won the tie against an equal Usenet EPUB and was imported by its own source.");
        Assert.IsEmpty(environment.Sabnzbd.Grabs, "Nothing went to the download client.");
    }

    [TestMethod]
    public async Task AnAudiobookRequestGrabsTheBestAudioReleaseAndNeverAnEbook()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "ebook"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits MP3 Unabridged", "mp3"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits M4B Unabridged", "m4b"));

        var request = await environment.AddAudiobookAsync();

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, stored.Status, stored.StatusMessage);
        StringAssert.Contains(environment.Sabnzbd.Grabs.Single().NzbUrl.ToString(), "m4b", "The audiobook container is the quality; the e-book is no candidate.");
    }

    [TestMethod]
    public async Task ADirectEditionThatFailsIsTriedOnceAndTheNextCandidateIsTaken()
    {
        var direct = new FakeDirectSource(fails: true);
        await using var environment = await BookAcquisitionEnvironment.CreateAsync(direct);
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));

        var first = await environment.AddAsync();
        Assert.AreEqual(1, direct.Imports);
        var waiting = await environment.RequestAsync(first.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status, waiting.StatusMessage);
        StringAssert.Contains(waiting.StatusMessage, "The mirror is gone.");

        await environment.Services.GetRequiredService<AcquisitionRequestService>().ContinueAsync(first.Id, CancellationToken.None);

        Assert.AreEqual(1, direct.Imports, "The failed edition is never tried again.");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "The Usenet release is the next candidate.");
    }

    [TestMethod]
    public async Task UnsupportedDownloadContinuesWithTheNextReleaseAndNeverResendsABadOne()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();
        StringAssert.Contains(environment.Sabnzbd.Grabs.Single().NzbUrl.ToString(), "epub", "EPUB is sent first.");

        var folder = environment.CompletedFolder("Atomic Habits");
        await File.WriteAllTextAsync(Path.Combine(folder, "Atomic Habits.mobi"), "mobi");
        await File.WriteAllTextAsync(Path.Combine(folder, "release.nfo"), "nfo");
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        var next = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, next.Status, "The next release is on its way.");
        Assert.AreNotEqual(request.OperationId, next.OperationId);
        StringAssert.Contains(next.StatusMessage, BookCompletedDownloadImportAdapter.NoBookFileReason);
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbUrl.ToString(), "pdf");
        Assert.HasCount(2, BookAcquisitionExecutor.ReadPayload(next).TriedReleases!, "Both releases are remembered as tried.");

        // The PDF release is unusable too: nothing is left to try, the request waits for new releases.
        Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.pdf"), "not really a pdf");
        await environment.CompleteDownloadAsync(next, "/data/downloads/complete/books/Atomic Habits");

        var waiting = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Approved, waiting.Status);
        Assert.IsNotNull(BookAcquisitionExecutor.ReadPayload(waiting).NextSearchUtc);
        StringAssert.Contains(waiting.StatusMessage, BookCompletedDownloadImportAdapter.NoBookFileReason);
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count, "A tried release is never submitted again.");
        Assert.AreEqual(0, await environment.Db.NovelWorks.CountAsync());

        var state = await environment.AddStateAsync();
        Assert.AreEqual("searching", state.RequestStatus);
        Assert.IsTrue(state.IsInFlight);
    }

    [TestMethod]
    public async Task UnreadableDownloadPathWaitsWithTheReasonAndFailsWithoutBurningReleases()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();

        await environment.CompleteDownloadAsync(request, "/elsewhere/Atomic Habits");

        // Not the release's fault: the request waits (the share may come back) and says why.
        var waiting = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Importing, waiting.Status);
        StringAssert.Contains(waiting.StatusMessage, "/elsewhere/Atomic Habits");

        await environment.RecoverAsync(DateTime.UtcNow + WantedAcquisitionService.CompletedImportTimeout + TimeSpan.FromMinutes(1));

        var failed = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, failed.Status);
        StringAssert.Contains(failed.StatusMessage, "/elsewhere/Atomic Habits");
        StringAssert.Contains(failed.StatusMessage, "Gave up importing");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "No other release is grabbed for a path problem.");
        Assert.AreEqual("failed", (await environment.AddStateAsync()).RequestStatus);
    }

    [TestMethod]
    public async Task CancelledBookDownloadStopsWithoutGrabbingTheNextRelease()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();

        await environment.Operations.MarkCancelledAsync(request.OperationId!.Value, "Cancelled in SABnzbd by the owner.");
        await environment.RecoverAsync(DateTime.UtcNow);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Failed, stored.Status);
        Assert.AreEqual(WantedAcquisitionService.CancelledMessage, stored.StatusMessage);
        Assert.AreEqual(request.OperationId, stored.OperationId);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "Cancel means stop, never the next release.");
    }

    [TestMethod]
    public async Task FinishedDownloadWaitsAsImportingAndIsImportedAfterARestart()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();
        var folder = environment.CompletedFolder("Atomic Habits");
        await File.WriteAllBytesAsync(Path.Combine(folder, "Atomic Habits.pdf"), TestPdf(pages: 2));

        // SABnzbd finished, but the process stopped before the import ran.
        var operation = await environment.Operations.GetAsync(request.OperationId!.Value);
        await environment.Operations.MarkSucceededAsync(operation!.Id, "SABnzbd download and post-processing completed.");
        environment.Sabnzbd.History = History(operation.ExternalId!, "/data/downloads/complete/books/Atomic Habits");
        var importing = await environment.AddStateAsync();
        Assert.AreEqual(BookAddState.Importing, importing.RequestStatus);
        Assert.IsTrue(importing.IsInFlight);

        // The next Wanted pass after the restart imports it from the job's own folder.
        Assert.IsTrue(await environment.RecoverAsync(DateTime.UtcNow) > 0);

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status);
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync());

        var importedOperation = await environment.Operations.GetAsync(request.OperationId!.Value);
        Assert.IsTrue(DownloadOperationDetails.TryParse(importedOperation!.Details, out var details));
        Assert.AreEqual(DownloadImportState.Completed, details!.Import!.State);
        Assert.AreEqual("/data/downloads/complete/books/Atomic Habits", details.Import.ReportedPath);
        Assert.AreEqual(environment.CompletedFolder("Atomic Habits").Replace('\\', '/'), details.Import.LocalPath!.Replace('\\', '/'));
        Assert.AreEqual(ImportMode.Copy, details.Import.Mode);
    }

    [TestMethod]
    public async Task FailedDownloadFoundAfterARestartContinuesWithTheNextRelease()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits EPUB", "epub"));
        environment.Prowlarr.Releases.Add(Release("James Clear - Atomic Habits PDF", "pdf"));
        var request = await environment.AddAsync();
        await environment.Operations.MarkFailedAsync(request.OperationId!.Value, "Repair failed");

        Assert.AreEqual(1, await environment.RecoverAsync(DateTime.UtcNow));

        var next = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, next.Status);
        StringAssert.Contains(next.StatusMessage, "Repair failed");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task PdfBookIsAnOrdinaryBooksWorkWithOneChapterPerPage()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "Deep Work.pdf");
        await File.WriteAllBytesAsync(source, TestPdf(pages: 4, title: "Deep Work", author: "Cal Newport", cover: true));
        var books = environment.Books;
        var workId = await books.ImportPdfFileAsync(source, "Deep Work.pdf", "upload", hint: null, CancellationToken.None);
        Assert.AreEqual(workId, await books.ImportPdfFileAsync(source, "copy.pdf", "upload", hint: null, CancellationToken.None), "The same file is one book.");

        var work = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual("Deep Work", work.Title, "The PDF Info title names the book.");
        Assert.AreEqual("Cal Newport", work.Author);
        Assert.IsTrue(BookFileFormats.IsPdf(work));
        Assert.AreEqual("en-gb", BookFileFormats.Language("PDF:en-GB"));
        Assert.AreEqual("PDF:en", work.Format);
        Assert.AreEqual($"/Books/Cover/{workId}", work.CoverImageUrl, "Without another cover, page 1 gives it.");
        CollectionAssert.AreEqual(CoverJpeg, await File.ReadAllBytesAsync((await books.GetLocalCoverPathAsync(workId, null, CancellationToken.None))!));

        var detail = await books.GetLibraryBookAsync(workId, "alice", "en", CancellationToken.None);
        Assert.IsNotNull(detail);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, detail.Chapters.Select(chapter => chapter.Number).ToArray(), "Chapter number = page number.");
        Assert.AreEqual("Page 3 starts with information that continues.", detail.Chapters[2].Title, "A page is titled by its first line.");
        var page3 = await books.GetReaderChapterAsync(detail.Chapters[2].Id, "alice", "en", CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "Page 3 starts with information that continues.", "A second paragraph." },
            page3!.OriginalParagraphs.ToArray(),
            "The page text is the chapter text (translation, learning and read-aloud use it).");

        var file = await books.GetStoredFileAsync(workId, CancellationToken.None);
        Assert.IsNotNull(file);
        Assert.AreEqual(BookFileFormats.Pdf, file.Format);
        Assert.AreEqual(BookFileFormats.PdfMediaType, file.MediaType);
        Assert.AreEqual(4, file.PageCount);
        StringAssert.StartsWith(file.Path, environment.FilesPath);

        // Progress is the canonical chapter progress: the page's chapter and the position on it.
        await books.SaveProgressAsync("alice", workId, detail.Chapters[1].Id, 500, "en", CancellationToken.None);
        var resume = (await new ContinueReadingQuery(environment.Db).GetAsync("alice")).Single();
        Assert.AreEqual($"/Books/Read/{detail.Chapters[1].Id}?lang=en", resume.ResumeUrl, "Continue reading opens the normal Books reader.");
        Assert.AreEqual(2, resume.ChapterNumber);

        await books.DeleteImportedBookAsync(workId, CancellationToken.None);
        Assert.IsFalse(File.Exists(file.Path), "Removing the book removes Jularr's copy of the PDF.");
        Assert.IsNull(await books.GetLocalCoverPathAsync(workId, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task PdfTitleDropsLibraryLabelsAndTakesTheRequestedCatalogTitle()
    {
        Assert.AreEqual("Pride and Prejudice", PdfBookIdentity.UsableTitle("The Project Gutenberg eBook #1342: Pride and Prejudice"));
        Assert.AreEqual("Emma", PdfBookIdentity.UsableTitle("The Project Gutenberg EBook of Emma, by Jane Austen"));
        Assert.AreEqual("Deep Work", PdfBookIdentity.UsableTitle("Deep Work"));
        Assert.IsNull(PdfBookIdentity.UsableTitle("Microsoft Word - draft.docx"));

        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "pg33283.pdf");
        await File.WriteAllBytesAsync(source, TestPdf(pages: 2, title: "The Project Gutenberg eBook #33283: A Tale of Two Cities, by Charles Dickens"));
        var workId = await environment.Books.ImportPdfFileAsync(source, "pg33283.pdf", "inbox", hint: null, CancellationToken.None);
        Assert.AreEqual("A Tale of Two Cities", (await environment.Db.NovelWorks.AsNoTracking().SingleAsync()).Title);

        // The inbox import is then linked to the request it answers: the catalog names the book.
        await environment.Books.LinkRequestedWorkAsync(
            workId,
            new BookImportHint("ol-OL118421W", "A Tale of Two Cities: A Story of the French Revolution", "Charles Dickens", "https://covers.openlibrary.org/b/id/12345-L.jpg?default=false"),
            CancellationToken.None);
        var linked = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual("A Tale of Two Cities: A Story of the French Revolution", linked.Title);
        Assert.AreEqual(linked.Title, linked.MetadataTitle);
        Assert.AreEqual("ol-OL118421W", linked.MetadataExternalId);
        Assert.AreEqual($"/Books/Cover/{workId}", linked.CoverImageUrl, "Open Library artwork served by the Internet Archive is cached locally.");

        // A cover URL that ends up outside the cover providers is not stored.
        var other = Path.Combine(environment.Root, "other.pdf");
        await File.WriteAllBytesAsync(other, TestPdf(pages: 1, title: "Another Book"));
        var otherId = await environment.Books.ImportPdfFileAsync(other, "other.pdf", "inbox", hint: null, CancellationToken.None);
        await environment.Books.LinkRequestedWorkAsync(
            otherId,
            new BookImportHint("ol-OTHER", "Another Book", null, "https://covers.openlibrary.org/b/id/666-L.jpg?default=false"),
            CancellationToken.None);
        Assert.IsNull((await environment.Db.NovelWorks.AsNoTracking().SingleAsync(x => x.Id == otherId)).CoverImageUrl);
        Assert.IsNull(await environment.Books.GetLocalCoverPathAsync(otherId, null, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("file:///I:/eBooks/Stephen%20King/Stephen%20King%20-%20Pet%20Sematary.html")]
    [DataRow("I:/eBooks/Stephen King/book.html")]
    [DataRow("https://example.org/print?id=4")]
    [DataRow("Stephen%20King%20-%20Pet%20Sematary")]
    [DataRow("Microsoft Word - draft.docx")]
    public void PdfTitlesThatAreLocationsOrTheFileNameAreNotUsable(string title)
    {
        Assert.IsNull(PdfBookIdentity.UsableTitle(title));
    }

    [TestMethod]
    public void AGarbageInfoBlockFallsBackToTheAuthorTitleFileName()
    {
        var identity = PdfBookIdentity.Resolve("file:///I:/eBooks/Stephen%20King/Stephen%20King%20-%20Pet%20Sema.html", "Atterdag", "Stephen King - Pet Sematary.pdf");
        Assert.AreEqual("Pet Sematary", identity.Title);
        Assert.AreEqual("Stephen King", identity.Author, "A bad title makes the whole Info block untrustworthy, including its author.");

        var plain = PdfBookIdentity.Resolve("https://x.test/a", "Atterdag", "pet_sematary.pdf");
        Assert.AreEqual(("Pet Sematary", (string?)null), (plain.Title, plain.Author));

        var nameCopy = PdfBookIdentity.Resolve("Stephen King - Pet Sematary", "Atterdag", "Stephen King - Pet Sematary.pdf");
        Assert.AreEqual(("Pet Sematary", (string?)"Stephen King"), (nameCopy.Title, nameCopy.Author), "An Info title that merely repeats the file name pattern is split.");

        var genuine =PdfBookIdentity.Resolve("Deep Work", "Cal Newport", "download (3).pdf");
        Assert.AreEqual(("Deep Work", (string?)"Cal Newport"), (genuine.Title, genuine.Author));

        var badAuthor = PdfBookIdentity.Resolve("Deep Work", "https://example.org", "Cal Newport - Deep Work.pdf");
        Assert.AreEqual("Cal Newport", badAuthor.Author);
        Assert.IsNull(PdfBookIdentity.UsableAuthor("12345"));
        Assert.IsNull(PdfBookIdentity.UsableAuthor("unknown"));
        Assert.AreEqual("01 - Chapter One", PdfBookIdentity.FromFileName("01 - Chapter One.pdf").Title);
    }

    [TestMethod]
    public async Task ImportAndReanalysisRepairOnlyGarbageDerivedTitlesAndAuthors()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "Stephen King - Pet Sematary.pdf");
        await File.WriteAllBytesAsync(source, TestPdf(pages: 2, title: "file:///I:/eBooks/Stephen%20King/Pet%20Sema.html", author: "Atterdag"));
        var workId = await environment.Books.ImportPdfFileAsync(source, Path.GetFileName(source), "upload", hint: null, CancellationToken.None);
        var imported = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(("Pet Sematary", (string?)"Stephen King"), (imported.Title, imported.Author));

        // A work imported before the validation existed carries the garbage and is repaired by re-analysis.
        var work = await environment.Db.NovelWorks.SingleAsync();
        work.Title = work.MetadataTitle = "file://I:\\eBooks\\Stephen%20King\\Pet%20Sema";
        work.Author = "Atterdag";
        await environment.Db.SaveChangesAsync();
        await environment.Books.ReanalyzePdfAsync(workId, CancellationToken.None);
        environment.Db.ChangeTracker.Clear();
        var repaired = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(("Pet Sematary", (string?)"Stephen King"), (repaired.Title, repaired.Author));

        var edited = await environment.Db.NovelWorks.SingleAsync();
        edited.Title = edited.MetadataTitle = "My Own Title";
        edited.Author = "Somebody";
        await environment.Db.SaveChangesAsync();
        await environment.Books.ReanalyzePdfAsync(workId, CancellationToken.None);
        environment.Db.ChangeTracker.Clear();
        var kept = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual(("My Own Title", (string?)"Somebody"), (kept.Title, kept.Author), "Values the owner edited are never overwritten.");
    }

    [TestMethod]
    public void PdfPagesAreListedAsAboutADozenRoundRanges()
    {
        static IReadOnlyList<BookChapterItem> Pages(int count, int translated) =>
            Enumerable.Range(1, count).Select(page => new BookChapterItem(Guid.NewGuid(), page, $"Page {page}", page <= translated)).ToArray();

        var book = Pages(312, translated: 45);
        var ranges = BookPageRange.Group(book);
        Assert.AreEqual(11, ranges.Count, "312 pages in ranges of 30.");
        Assert.AreEqual((1, 30, 30, 30), (ranges[0].FirstPage, ranges[0].LastPage, ranges[0].PageCount, ranges[0].TranslatedCount));
        Assert.AreEqual((31, 60, 15), (ranges[1].FirstPage, ranges[1].LastPage, ranges[1].TranslatedCount));
        Assert.AreEqual((301, 312), (ranges[^1].FirstPage, ranges[^1].LastPage));
        Assert.AreEqual(book[0].Id, ranges[0].FirstChapterId, "A range opens at its first page.");

        CollectionAssert.AreEqual(new[] { 1, 11 }, BookPageRange.Group(Pages(12, 0)).Select(range => range.FirstPage).ToArray(), "Short PDFs use ranges of ten.");
        Assert.AreEqual(0, BookPageRange.Group([]).Count);
    }

    [TestMethod]
    public async Task EpubBooksHaveNoStoredFile()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        await using var epub = new EpubTestBuilder { Title = "Emma", Author = "Jane Austen", Language = "en" }
            .Chapter("c1.xhtml", "Volume I", "Emma Woodhouse, handsome, clever, and rich.")
            .Build();
        var workId = await environment.Books.ImportUploadedEpubAsync(epub, "emma.epub", CancellationToken.None);

        Assert.IsNull(await environment.Books.GetStoredFileAsync(workId, CancellationToken.None));
    }

    [TestMethod]
    public async Task NonPdfFilesAreNotImportedAsPdf()
    {
        await using var environment = await BookAcquisitionEnvironment.CreateAsync();
        var fake = Path.Combine(environment.Root, "fake.pdf");
        await File.WriteAllTextAsync(fake, "<html>not a pdf</html>");

        var imported = await environment.Books.ImportBooksFromPathAsync(environment.Root, "inbox", hint: null, singleBook: false, CancellationToken.None);

        Assert.AreEqual(0, imported.Count);
        Assert.AreEqual(0, await environment.Db.NovelWorks.CountAsync());
    }

    private static AcquisitionCandidate Release(string title, string key) =>
        new(title, "Test indexer", 1, "usenet", 4_000_000, null, null, DateTimeOffset.UtcNow, 1, 1, key, null,
            AnimeReleaseParser.Parse(title), [], new Uri($"https://indexer.example/{key}.nzb"), null);

    private static SabnzbdHistorySnapshot History(string nzoId, string storagePath) =>
        new([new SabnzbdHistoryJob(nzoId, Path.GetFileName(storagePath), "Completed", "books", storagePath, null, SabnzbdFailureKind.None, DateTimeOffset.UtcNow)]);

    // Not a decodable picture; the reader only checks the JPEG start marker.
    private static readonly byte[] CoverJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    /// <summary>
    /// A small, well-formed PDF: a page tree whose pages show two wrapped lines and a second
    /// paragraph. Optionally the pages are packed into a compressed object stream, the content
    /// streams are Flate-compressed, the last page uses a two-byte font with a ToUnicode CMap,
    /// and page 1 draws a full-page JPEG.
    /// </summary>
    private static byte[] TestPdf(
        int pages,
        string? title = null,
        string? author = null,
        string? hexTitle = null,
        bool packPages = false,
        bool compressContent = false,
        bool unicodeLastPage = false,
        bool cover = false)
    {
        var pdf = new PdfBuilder();
        var catalog = pdf.Reserve();
        var tree = pdf.Reserve();
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var cmap = Encoding.Latin1.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "2 beginbfchar <0001> <0048> <0002> <0069> endbfchar\n1 beginbfrange <0003> <0003> <0021> endbfrange\nendcmap end end");
        var unicodeFont = pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Custom /Encoding /Identity-H /ToUnicode {pdf.Add("<< >>", cmap)} 0 R >>");
        var image = cover
            ? pdf.Add("<< /Type /XObject /Subtype /Image /Width 600 /Height 900 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode >>", CoverJpeg)
            : 0;

        var kids = new List<int>();
        for (var number = 1; number <= pages; number++)
        {
            var unicode = unicodeLastPage && number == pages;
            var text = unicode
                ? "BT /F2 12 Tf 72 700 Td <000100020003> Tj ET"
                : $"BT /F1 12 Tf 72 700 Td (Page {number} starts with infor-) Tj 0 -14 Td (mation that continues.) Tj 0 -40 Td (A second paragraph.) Tj ET";
            if (number == 1 && cover)
            {
                text = "q 612 0 0 792 0 0 cm /Im1 Do Q " + text;
            }

            var contents = compressContent
                ? pdf.Add("<< /Filter /FlateDecode >>", Deflate(Encoding.Latin1.GetBytes(text)))
                : pdf.Add("<< >>", Encoding.Latin1.GetBytes(text));
            var images = number == 1 && cover ? $" /XObject << /Im1 {image} 0 R >>" : "";
            kids.Add(pdf.Add(
                $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R /F2 {unicodeFont} 0 R >>{images} >> /Contents {contents} 0 R >>",
                packed: packPages));
        }

        pdf.Define(catalog, $"<< /Type /Catalog /Pages {tree} 0 R /Lang (en-GB) >>");
        pdf.Define(tree, $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(kid => $"{kid} 0 R"))}] /Count {pages} >>", packed: packPages);

        var info = new StringBuilder("<<");
        if (title is not null)
        {
            info.Append($" /Title ({title})");
        }

        if (hexTitle is not null)
        {
            info.Append($" /Title <{hexTitle}>");
        }

        if (author is not null)
        {
            info.Append($" /Author ({author})");
        }

        return pdf.Build(catalog, pdf.Add(info + " >>"));
    }

    private static byte[] Deflate(byte[] data)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return packed.ToArray();
    }

    /// <summary>Writes numbered objects, plain or packed into one compressed object stream.</summary>
    private sealed class PdfBuilder
    {
        private readonly SortedDictionary<int, (string Body, byte[]? Stream, bool Packed)> objects = [];
        private int next;

        public int Reserve() => ++next;

        public int Add(string body, byte[]? stream = null, bool packed = false)
        {
            var number = Reserve();
            Define(number, body, stream, packed);
            return number;
        }

        public void Define(int number, string body, byte[]? stream = null, bool packed = false) =>
            objects[number] = (body, stream, packed);

        public byte[] Build(int catalog, int info)
        {
            var output = new List<byte>(Encoding.Latin1.GetBytes("%PDF-1.7\n%âãÏÓ\n"));
            void Write(string value) => output.AddRange(Encoding.Latin1.GetBytes(value));

            var packed = objects.Where(entry => entry.Value.Packed).ToArray();
            foreach (var (number, (body, stream, isPacked)) in objects)
            {
                if (isPacked)
                {
                    continue;
                }

                if (stream is null)
                {
                    Write($"{number} 0 obj\n{body}\nendobj\n");
                    continue;
                }

                Write($"{number} 0 obj\n{body[..^2]} /Length {stream.Length} >>\nstream\n");
                output.AddRange(stream);
                Write("\nendstream\nendobj\n");
            }

            if (packed.Length > 0)
            {
                var header = new StringBuilder();
                var bodies = new StringBuilder();
                foreach (var (number, (body, _, _)) in packed)
                {
                    header.Append($"{number} {bodies.Length} ");
                    bodies.Append(body).Append('\n');
                }

                var data = Deflate(Encoding.Latin1.GetBytes(header.ToString() + bodies));
                Write($"{++next} 0 obj\n<< /Type /ObjStm /N {packed.Length} /First {header.Length} /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
                output.AddRange(data);
                Write("\nendstream\nendobj\n");
            }

            Write($"trailer\n<< /Root {catalog} 0 R /Info {info} 0 R >>\n%%EOF\n");
            return [.. output];
        }
    }

    /// <summary>
    /// Books acquisition wired like Program.cs on an isolated database and data folder: a fake
    /// Prowlarr indexer and SABnzbd client, the real executor, request service and import.
    /// The free catalogs are unreachable, so every add goes to Usenet.
    /// </summary>
    private sealed class BookAcquisitionEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private BookAcquisitionEnvironment(string root, ServiceProvider services, AppDbContext db)
        {
            Root = root;
            this.services = services;
            Db = db;
        }

        public string Root { get; }
        public AppDbContext Db { get; }
        public FakeProwlarrClient Prowlarr => services.GetRequiredService<FakeProwlarrClient>();
        public FakeSabnzbdClient Sabnzbd => services.GetRequiredService<FakeSabnzbdClient>();
        public BookCatalogService Books => services.GetRequiredService<BookCatalogService>();
        public OperationStore Operations => new(Db);
        public string FilesPath => Books.FilesPath;

        public static async Task<BookAcquisitionEnvironment> CreateAsync(IDirectSource? direct = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-books-{Guid.NewGuid():N}");
            var data = Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "mnt", "complete", "books"));
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:FilesPath"] = Path.Combine(root, "books-files"),
                    ["Books:CoversPath"] = Path.Combine(root, "books-covers"),
                    ["Books:DerivedPath"] = Path.Combine(root, "books-derived"),
                    ["Books:Translation:MemoryPath"] = Path.Combine(root, "translation-memory")
                })
                .Build();
            var protection = new EphemeralDataProtectionProvider();
            var owner = new CurrentAccountContext(new FixedHttpContextAccessor(new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, AccountRoles.Owner)],
                    "test"))
            }));

            var collection = new ServiceCollection();
            collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            collection.AddSingleton(db);
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddSingleton<IBookTranslator, NoopBookTranslator>();
            collection.AddSingleton(provider => new BookCatalogService(
                new HttpClient(new UnreachableHandler()),
                db,
                provider.GetRequiredService<IBookTranslator>(),
                configuration));
            collection.AddSingleton(provider => new OperationRunner(db, provider));
            collection.AddSingleton(new AnimeImportSettingsStore(data.FullName));
            collection.AddSingleton<Jularr.Web.Features.Storage.LibraryRootRoutingService>();
            collection.AddSingleton<IHardLinkCreator, FileSystemHardLinkCreator>();
            collection.AddSingleton(new AcquisitionAccessStore(db));
            collection.AddSingleton<FakeProwlarrClient>();
            collection.AddSingleton<IProwlarrClient>(provider => provider.GetRequiredService<FakeProwlarrClient>());
            collection.AddSingleton<FakeSabnzbdClient>();
            collection.AddSingleton<ISabnzbdClient>(provider => provider.GetRequiredService<FakeSabnzbdClient>());
            collection.AddSingleton(new IndexerStore(protection, data));
            collection.AddSingleton(new DownloadClientStore(protection, data));
            collection.AddSingleton(new AcquisitionHealthStore(data));
            collection.AddSingleton(new SabnzbdAcquisitionStore(data));
            collection.AddSingleton<IReadOnlyDictionary<IndexerType, IIndexer>>(provider =>
                new Dictionary<IndexerType, IIndexer>
                {
                    [IndexerType.Prowlarr] = new ProwlarrIndexer(provider.GetRequiredService<IProwlarrClient>())
                });
            collection.AddSingleton<IDownloadClient>(provider => new SabnzbdDownloadClient(provider.GetRequiredService<ISabnzbdClient>()));
            collection.AddSingleton<IndexerSearchCoordinator>();
            collection.AddSingleton<IMediaAcquisitionRegistration, BookAcquisitionRegistration>();
            collection.AddSingleton<IMediaAcquisitionRegistration, AudiobookAcquisitionRegistration>();
            collection.AddSingleton<MediaAcquisitionRegistry>();
            collection.AddSingleton(provider => new QualityProfileStore(
                new DirectoryInfo(Path.Combine(data.FullName, "quality-profiles")),
                provider.GetRequiredService<MediaAcquisitionRegistry>()));
            collection.AddSingleton<BookSearchCoordinator>();
            collection.AddSingleton<DownloadClientSelector>();
            collection.AddSingleton<DownloadClientSubmissionService>();
            collection.AddSingleton<SabnzbdDownloadService>();
            collection.AddSingleton(owner);
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<ReleaseRequestTracker>();
            collection.AddSingleton<Jularr.Web.Features.Acquisition.Core.AcquisitionCore>();
            if (direct is not null)
            {
                collection.AddSingleton(direct);
            }

            collection.AddSingleton<IAcquisitionRequestExecutor, BookAcquisitionExecutor>();
            collection.AddSingleton<IAcquisitionRequestExecutor, Jularr.Web.Features.Audiobooks.AudiobookAcquisitionRequestExecutor>();
            collection.AddSingleton<Jularr.Web.Features.Events.IJularrEventPublisher, RecordingEventPublisher>();
            collection.AddSingleton<IMediaCapabilityService>(new MediaCapabilityService(new MediaCapabilityStore(data.FullName)));
            collection.AddSingleton(new AcquisitionRequestSettingsStore(data.FullName));
            collection.AddSingleton<AcquisitionRequestService>();
            // The shared Wanted lifecycle with the Book adapter behind the dispatcher.
            collection.AddSingleton<IWantedRequestHandler, BookWantedRequestHandler>();
            collection.AddSingleton<ICompletedDownloadImportAdapter, BookCompletedDownloadImportAdapter>();
            collection.AddSingleton<CompletedDownloadDispatcher>();
            collection.AddSingleton<ICompletedDownloadLocationResolver, CompletedDownloadLocationResolver>();
            collection.AddSingleton<CompletedDownloadImportService>();
            var services = collection.BuildServiceProvider();

            await services.GetRequiredService<IndexerStore>().SaveAsync(new IndexerEntry(
                Guid.NewGuid(),
                "Prowlarr",
                IndexerType.Prowlarr,
                Enabled: true,
                Priority: 1,
                IndexerSettings.CreateDefault("http://prowlarr:9696", IndexerType.Prowlarr),
                "prowlarr-key"));
            await services.GetRequiredService<DownloadClientStore>().SaveAsync(new DownloadClientEntry(
                Guid.NewGuid(),
                "SABnzbd",
                DownloadClientType.Sabnzbd,
                Enabled: true,
                Priority: 1,
                new DownloadClientSettings("http://sabnzbd:8080", new Dictionary<MediaAcquisitionKind, string?> { [MediaAcquisitionKind.Book] = "books", [MediaAcquisitionKind.Audiobook] = "audiobooks", [MediaAcquisitionKind.Anime] = "anime" }),
                "secret-key"));
            await services.GetRequiredService<AnimeImportSettingsStore>().UpdateAsync(
                state => state.WithRemotePathMappings(MediaAcquisitionKind.Book, [new RemotePathMapping("/data/downloads/complete", Path.Combine(root, "mnt", "complete"))]),
                CancellationToken.None);

            await ReadingTestRoots.AssignAsync(db, MediaAcquisitionKind.Book, Path.Combine(root, "library-books"), ImportMode.Copy);
            return new BookAcquisitionEnvironment(root, services, db);
        }

        public IServiceProvider Services => services;

        public Task<AcquisitionRequest> AddAsync() =>
            services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
                new AcquisitionRequestDraft(
                    MediaAcquisitionKind.Book,
                    BookCatalogService.CatalogRequestProvider,
                    CatalogId,
                    "Atomic Habits",
                    "James Clear",
                    "https://books.google.com/atomic-habits.jpg",
                    System.Text.Json.JsonSerializer.Serialize(
                        new BookRequestPayload(CatalogId, "Atomic Habits", "James Clear"),
                        System.Text.Json.JsonSerializerOptions.Web)),
                CancellationToken.None);

        public Task<AcquisitionRequest> AddAudiobookAsync() =>
            services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
                new AcquisitionRequestDraft(MediaAcquisitionKind.Audiobook, BookCatalogService.CatalogRequestProvider, CatalogId, "Atomic Habits", "James Clear", null),
                CancellationToken.None);

        public async Task<AcquisitionRequest> RequestAsync(Guid id) =>
            (await services.GetRequiredService<AcquisitionAccessStore>().GetAsync(id, CancellationToken.None))!;

        public async Task<BookAddState> AddStateAsync() =>
            (await new BookAddStateQuery(Db, services.GetRequiredService<AcquisitionAccessStore>())
                .GetAsync([new BookAddLookup(CatalogId)], CancellationToken.None))[CatalogId];

        /// <summary>The job folder as Jularr sees it (under the mapped mount).</summary>
        public string CompletedFolder(string job) =>
            Directory.CreateDirectory(Path.Combine(Root, "mnt", "complete", "books", job)).FullName;

        /// <summary>
        /// SABnzbd finishes the request's job at <paramref name="reportedPath"/> (the monitor
        /// marks the operation done) and the shared Wanted lifecycle imports it.
        /// </summary>
        public async Task CompleteDownloadAsync(AcquisitionRequest request, string reportedPath)
        {
            var operationId = request.OperationId!.Value;
            await Operations.MarkSucceededAsync(operationId, "SABnzbd download and post-processing completed.");
            var operation = (await Operations.GetAsync(operationId))!;
            Sabnzbd.History = History(operation.ExternalId!, reportedPath);
            await RecoverAsync(DateTime.UtcNow);
        }

        /// <summary>One pass of the shared Wanted lifecycle, as after a restart.</summary>
        public async Task<int> RecoverAsync(DateTime nowUtc)
        {
            var moved = await WantedAcquisitionService.ProcessOnceAsync(services, nowUtc, CancellationToken.None);
            Db.ChangeTracker.Clear();
            return moved;
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Db.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
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

    /// <summary>
    /// No metadata network; cover hosts answer with a picture. Open Library covers redirect
    /// (as the real service does) to the Internet Archive, cover 666 to a host that is not a
    /// cover provider.
    /// </summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host is "books.google.com" or "covers.openlibrary.org")
            {
                var content = new ByteArrayContent(
                    [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xD9]);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                var final = uri.Host != "covers.openlibrary.org" ? uri
                    : uri.AbsolutePath.Contains("/666-", StringComparison.Ordinal) ? new Uri("https://intranet.example/cover.jpg")
                    : new Uri("https://ia800500.us.archive.org/view_archive.php?file=12345-L.jpg");
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = content,
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, final)
                    });
            }

            throw new HttpRequestException("No network in tests.");
        }
    }
}
