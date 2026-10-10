using System.Net;
using System.Text;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;

namespace Jularr.Tests;

/// <summary>Books from request to reader through the shared pipeline: a real Newznab indexer answering from a scripted server, the real executor, download client fake, import, library and reader.</summary>
[TestClass]
public sealed class BooksLifecycleTests
{
    private const string Title = "Atomic Habits";
    private const string Author = "James Clear";
    internal const string Good = "James Clear - Atomic Habits (2018) EPUB";

    private const string Caps = """
        <caps><server title="Book Indexer" />
          <searching><search available="yes" supportedParams="q" /><book-search available="yes" supportedParams="q,title,author" /></searching>
          <categories><category id="7000" name="Books"><subcat id="7020" name="Books/Ebook" /><subcat id="7030" name="Books/Comics" /></category></categories>
        </caps>
        """;

    internal sealed class BookIndexer(params string[] releases) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public List<string> Titles { get; } = [.. releases];

        public static string Param(Uri uri, string name) =>
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Where(part => Uri.UnescapeDataString(part[0]) == name).Select(part => part.Length > 1 ? Uri.UnescapeDataString(part[1].Replace('+', ' ')) : "").FirstOrDefault() ?? "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var body = Param(uri, "t") == "caps"
                ? Caps
                : "<rss version=\"2.0\"><channel>" + string.Concat(Titles.Select(title => $"<item><title>{WebUtility.HtmlEncode(title)}</title><guid>{Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(title)))}</guid><enclosure url=\"https://indexer.example/getnzb/{Uri.EscapeDataString(title)}.nzb\" length=\"4000000\" /></item>")) + "</channel></rss>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });
        }
    }

    internal static void WriteEpub(string folder, string name, string title = Title, string author = Author, string language = "en")
    {
        using var epub = File.Create(Path.Combine(folder, name));
        new EpubTestBuilder { Title = title, Author = author, Language = language }
            .Chapter("c1.xhtml", "The Surprising Power of Atomic Habits", "Habits compound.")
            .Chapter("c2.xhtml", "Make It Obvious", "Design your environment.")
            .Build()
            .CopyTo(epub);
    }

    internal static void WritePdf(string folder, string name) =>
        File.WriteAllBytes(Path.Combine(folder, name), BookPdfAcquisitionTests.TestPdf(pages: 3));

    internal static async Task<Env> StartAsync(BookIndexer indexer)
    {
        var environment = await Env.CreateAsync(newznab: indexer, canonicalWorks: true);
        await environment.AddNewznabIndexerAsync();
        return environment;
    }

    /// <summary>A request as the Books add dialog sends it: the catalog id with the edition and provider evidence of the result.</summary>
    private static Task<AcquisitionRequest> SubmitAsync(Env environment, string catalogId = BookPdfAcquisitionTests.CatalogId, string? language = null, string? isbn = null, string[]? identities = null) =>
        environment.Services.GetRequiredService<AcquisitionRequestService>().SubmitAsync(
            new AcquisitionRequestDraft(
                MediaAcquisitionKind.Book,
                BookCatalogService.CatalogRequestProvider,
                catalogId,
                Title,
                Author,
                null,
                System.Text.Json.JsonSerializer.Serialize(new BookRequestPayload(catalogId, Title, Author, language, isbn, 2018, identities), System.Text.Json.JsonSerializerOptions.Web)),
            CancellationToken.None);

    private static Task<int> BookWorksAsync(Env environment) => environment.Db.Works.CountAsync(work => work.MediaType == WorkMediaType.Book);

    [TestMethod]
    public async Task ABookIsRequestedFoundOnNewznabDownloadedImportedAndRead()
    {
        var indexer = new BookIndexer("James Clear - Atomic Habits (2018) EPUB");
        await using var environment = await StartAsync(indexer);

        var request = await environment.AddAsync();

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, request.Status, request.StatusMessage);
        var grab = Assert.ContainsSingle(environment.Sabnzbd.Grabs);
        StringAssert.Contains(grab.NzbName, "Atomic Habits");
        Assert.IsTrue(indexer.Requests.Where(uri => BookIndexer.Param(uri, "t") != "caps").All(uri => BookIndexer.Param(uri, "cat").Split(',').All(category => category is "7000" or "7020" or "")), "Books are searched in their own categories only.");

        var folder = environment.CompletedFolder("Atomic Habits");
        WriteEpub(folder, "Atomic Habits.epub");
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        var novel = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        Assert.AreEqual($"/Books/Library/{novel.Id}", stored.ResultUrl);
        var file = await environment.Db.BookFiles.AsNoTracking().SingleAsync();
        Assert.AreEqual("EPUB", file.Format);
        Assert.AreEqual(1, await BookWorksAsync(environment), "Exactly one canonical Work.");
        Assert.IsTrue(await environment.Db.WorkSourceLinks.AnyAsync(link => link.SourceKind == WorkSourceKind.BookEdition), "The imported edition belongs to the Work, so installed and upgrade state can be read from it.");

        var books = environment.Services.GetRequiredService<BookCatalogService>();
        var library = await books.GetLibraryAsync("owner", "en", CancellationToken.None);
        Assert.AreEqual(novel.Id, Assert.ContainsSingle(library).WorkId);
        var detail = (await books.GetLibraryBookAsync(novel.Id, "owner", "en", CancellationToken.None))!;
        Assert.HasCount(2, detail.Chapters);
        var chapter = (await books.GetReaderChapterAsync(detail.Chapters[1].Id, "owner", "en", CancellationToken.None))!;
        StringAssert.Contains(string.Join(' ', chapter.OriginalParagraphs), "Design your environment");

        await books.SaveProgressAsync("owner", novel.Id, detail.Chapters[1].Id, 420, "en", CancellationToken.None);
        var resumed = (await books.GetLibraryBookAsync(novel.Id, "owner", "en", CancellationToken.None))!.Progress!;
        Assert.AreEqual(detail.Chapters[1].Id, resumed.ChapterId);
        Assert.AreEqual(420, resumed.PositionPermille);
        Assert.IsNull((await books.GetLibraryBookAsync(novel.Id, "someone-else", "en", CancellationToken.None))!.Progress, "Progress is the profile's own.");
    }

    [TestMethod]
    public async Task WhenNoEpubExistsAPdfIsImportedAndTheBookStaysWantedForAnEpubThatTheUpgradeKeepsBesideIt()
    {
        var indexer = new BookIndexer("James Clear - Atomic Habits PDF");
        await using var environment = await StartAsync(indexer);

        var request = await environment.AddAsync();
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "PDF");
        WritePdf(environment.CompletedFolder("Atomic Habits"), "Atomic Habits.pdf");
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        Assert.AreEqual("PDF", (await environment.Db.BookFiles.AsNoTracking().SingleAsync()).Format);
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync());
        Assert.AreEqual(1, await BookWorksAsync(environment));

        // The profile wants an EPUB: the book stays wanted as an upgrade; an EPUB that shows up later is added to the same book and the PDF stays readable.
        indexer.Titles.Add("James Clear - Atomic Habits EPUB");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        var upgrading = await environment.RequestAsync(request.Id);
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count, $"{upgrading.Status} {upgrading.StatusMessage}");
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "EPUB");

        WriteEpub(environment.CompletedFolder("Atomic Habits EPUB"), "Atomic Habits.epub");
        await environment.CompleteDownloadAsync(upgrading, "/data/downloads/complete/books/Atomic Habits EPUB");

        var formats = await environment.Db.BookFiles.AsNoTracking().OrderBy(item => item.Format).Select(item => item.Format).ToListAsync();
        CollectionAssert.AreEqual(new[] { "EPUB", "PDF" }, formats, "Both copies stay readable.");
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync(), "The upgrade went into the same book.");
        Assert.AreEqual(1, await BookWorksAsync(environment));
        Assert.IsTrue((await environment.Db.BookFiles.AsNoTracking().ToListAsync()).All(item => File.Exists(item.StoragePath)));

        // With an EPUB in the library the profile is satisfied: nothing is wanted any more and nothing is searched again.
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(4));
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync());
    }

    [TestMethod]
    public async Task WrongBooksWrongEditionsAndWrongFormatsAreRejectedWithReasonsAndManualRankOneIsWhatAutomaticAcquisitionGrabs()
    {
        var indexer = new BookIndexer("James Clear - Atomic Habits German EPUB", "Chris Voss - Never Split the Difference EPUB", "James Clear - Atomic Habits Audiobook MP3", "James Clear - Atomic Habits MOBI");
        await using var environment = await StartAsync(indexer);

        var request = await SubmitAsync(environment, language: "en");

        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        Assert.IsEmpty(environment.Sabnzbd.Grabs, "Nothing eligible was found, so nothing was downloaded.");
        var manual = environment.Services.GetRequiredService<BookManualSearchService>();
        var rejected = (await manual.SearchAsync(request.Id, CancellationToken.None, refresh: true)).Search.Ranked;
        Assert.IsTrue(rejected.All(item => item.Score == 0 && item.Rank is null && !string.IsNullOrWhiteSpace(item.RejectedBecause)));
        StringAssert.Contains(rejected.Single(item => item.Release.Title.Contains("German", StringComparison.Ordinal)).RejectedBecause, "German");
        StringAssert.Contains(rejected.Single(item => item.Release.Title.Contains("Never Split", StringComparison.Ordinal)).RejectedBecause, "title");

        indexer.Titles.Add(Good);
        var ranked = (await manual.SearchAsync(request.Id, CancellationToken.None, refresh: true)).Search.Ranked;
        Assert.AreEqual(Good, ranked[0].Release.Title);
        Assert.AreEqual(1, ranked[0].Rank);
        Assert.AreEqual(4, ranked.Count(item => item.Rank is null), "The wrong ones keep their reasons and have no rank.");

        await environment.Services.GetRequiredService<AcquisitionRequestService>().ContinueAsync(request.Id, CancellationToken.None);

        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "2018", "Automatic acquisition took the release that Manual Search ranks first.");
    }

    [TestMethod]
    public void TheIsbnOfTheRequestedEditionIsTheStrongestEvidenceAndIsConvertedFromAnIsbnTen()
    {
        var releases = new[]
        {
            Candidate("James Clear - Atomic Habits (Paperback) EPUB", "a"),
            Candidate("Atomic Habits ISBN 9780735211292 EPUB", "b"),
            Candidate("James Clear - Atomic Habits EPUB", "c")
        };

        var ranked = BookReleaseSelector.Rank(releases, Title, Author, isbn: "0-7352-1129-9");

        var withIsbn = ranked.Single(item => item.Release.Title.Contains("9780735211292", StringComparison.Ordinal));
        Assert.IsTrue(withIsbn.ScoreReasons.Any(reason => reason.StartsWith("ISBN", StringComparison.Ordinal)), "The ISBN-10 of the request is the same edition as the ISBN-13 in the name.");
        Assert.IsTrue(ranked.Where(item => item != withIsbn).All(item => !item.ScoreReasons.Any(reason => reason.StartsWith("ISBN", StringComparison.Ordinal))));
        Assert.IsTrue(withIsbn.Score > ranked.Single(item => item.Release.Title == "James Clear - Atomic Habits (Paperback) EPUB").Score - 1);
        Assert.AreEqual("9780735211292", BookReleaseSelector.NormalizeIsbn("978-0-7352-1129-2"));
        Assert.IsNull(BookReleaseSelector.NormalizeIsbn("12345"));
    }

    private static AcquisitionCandidate Candidate(string title, string key) =>
        new(title, "Indexer", 1, "usenet", 4_000_000, null, null, DateTimeOffset.UtcNow, 1, 1, key, null, AnimeReleaseParser.Parse(title), [], new Uri($"https://indexer.example/{key}.nzb"), null);

    [TestMethod]
    public async Task TheSameBookRequestedThroughTwoCatalogsIsOneWorkAndAnUnrelatedBookIsAnother()
    {
        await using var environment = await StartAsync(new BookIndexer());

        var first = await SubmitAsync(environment, "ol:OL1W", identities: ["ol:OL1W", "gb:G1"], isbn: "9780735211292");
        var second = await SubmitAsync(environment, "gb:G1", identities: ["gb:G1", "ol:OL1W"]);
        var third = await SubmitAsync(environment, "gb:G2", identities: ["gb:G2"], isbn: "9781847941831");

        Assert.IsNotNull(first.WorkId);
        Assert.AreEqual(first.WorkId, second.WorkId, "Another provider record of the same book finds the Work that exists.");
        Assert.AreNotEqual(first.WorkId, third.WorkId, "A different book is never merged into it.");
        Assert.AreEqual(2, await BookWorksAsync(environment));
        var identities = await environment.Db.WorkExternalIdentities.AsNoTracking().Where(identity => identity.WorkId == first.WorkId).Select(identity => identity.Provider + ":" + identity.ExternalId).ToListAsync();
        CollectionAssert.IsSubsetOf(new[] { "isbn:9780735211292" }, identities);
        Assert.IsTrue(identities.Count >= 3, "Every provider id of the book is kept on its Work.");
    }

    [TestMethod]
    public async Task AFailedDownloadIsFollowedByTheNextReleaseAndThatOneIsImported()
    {
        var indexer = new BookIndexer("James Clear - Atomic Habits (2018) EPUB", "James Clear - Atomic Habits EPUB");
        await using var environment = await StartAsync(indexer);
        var request = await environment.AddAsync();
        var first = Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName;

        await environment.Operations.MarkFailedAsync(request.OperationId!.Value, "Out of retention");
        await environment.RecoverAsync(DateTime.UtcNow);

        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        Assert.AreNotEqual(first, environment.Sabnzbd.Grabs[1].NzbName, "The failed release is not sent again.");
        var retried = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, retried.Status, retried.StatusMessage);
        WriteEpub(environment.CompletedFolder("Atomic Habits"), "Atomic Habits.epub");
        await environment.CompleteDownloadAsync(retried, "/data/downloads/complete/books/Atomic Habits");
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.RequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await environment.Db.BookFiles.CountAsync());
    }

    [TestMethod]
    public async Task RepeatedPassesAndRestartsNeverDuplicateTheDownloadTheWorkOrTheFile()
    {
        await using var environment = await StartAsync(new BookIndexer(Good));
        var request = await environment.AddAsync();
        WriteEpub(environment.CompletedFolder("Atomic Habits"), "Atomic Habits.epub");
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        for (var pass = 1; pass <= 3; pass++)
        {
            await environment.RecoverAsync(DateTime.UtcNow.AddHours(pass * 3));
        }

        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
        Assert.AreEqual(1, await environment.Db.NovelWorks.CountAsync());
        Assert.AreEqual(1, await environment.Db.BookFiles.CountAsync());
        Assert.AreEqual(1, await BookWorksAsync(environment));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(environment.Root, "library-books"), "*.epub", SearchOption.AllDirectories).Length);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.RequestAsync(request.Id)).Status);
    }

    [TestMethod]
    public async Task ACompletedDownloadWaitsForOfflineStorageAndImportsOnceWithoutAnotherDownloadWhenItIsBack()
    {
        await using var environment = await StartAsync(new BookIndexer(Good));
        var request = await environment.AddAsync();
        WriteEpub(environment.CompletedFolder("Atomic Habits"), "Atomic Habits.epub");
        var library = Directory.CreateDirectory(Path.Combine(environment.Root, "library-books")).FullName;
        Directory.Move(library, library + "-away");
        File.WriteAllText(library, "not a folder: the share is gone");

        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");

        var waiting = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Importing, waiting.Status, waiting.StatusMessage);
        Assert.AreEqual(0, await environment.Db.BookFiles.CountAsync());
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "Offline storage never makes the release look bad.");

        File.Delete(library);
        Directory.Move(library + "-away", library);
        await environment.RecoverAsync(DateTime.UtcNow.AddMinutes(30));

        Assert.AreEqual(AcquisitionRequestStatus.Completed, (await environment.RequestAsync(request.Id)).Status);
        Assert.AreEqual(1, await environment.Db.BookFiles.CountAsync());
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
    }

    private sealed class FreeEditionSource(Func<BookCatalogService> books) : IDirectSource
    {
        public string Name => "free";

        public MediaAcquisitionKind Kind => MediaAcquisitionKind.Book;

        public Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(Jularr.Web.Features.Acquisition.Search.SearchIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AcquisitionCandidate>>(
            [
                new AcquisitionCandidate("Atomic Habits [EPUB]", "Free edition", null, "direct", null, null, null, null, null, null, null, null, AnimeReleaseParser.Parse("Atomic Habits [EPUB]"), [], null, null)
                {
                    Type = AcquisitionType.DirectImport,
                    Offer = new DirectOffer("free", "edition", IdentityIsExact: true)
                }
            ]);

        public async Task<AcquisitionExecution> ImportAsync(AcquisitionRequest request, DirectOffer offer, CancellationToken cancellationToken)
        {
            var epub = new EpubTestBuilder { Title = Title, Author = Author, Language = "en" }.Chapter("c1.xhtml", "One", "Free text.").Build();
            var id = await books().ImportUploadedEpubAsync(epub, "Atomic Habits.epub", cancellationToken);
            return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "Imported a direct/free edition.", ResultUrl: $"/Books/Library/{id}");
        }
    }

    [TestMethod]
    public async Task AFreeEditionIsImportedWithoutAnyIndexerOrDownloadClientAndBelongsToTheRequestsWork()
    {
        BookCatalogService? books = null;
        await using var environment = await Env.CreateAsync(new FreeEditionSource(() => books!), new BookIndexer(), canonicalWorks: true);
        books = environment.Books;

        var request = await environment.AddAsync();

        var stored = await environment.RequestAsync(request.Id);
        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        Assert.IsEmpty(environment.Sabnzbd.Grabs, "No Usenet was involved.");
        var novel = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();
        var work = await environment.Db.Works.AsNoTracking().SingleAsync(item => item.MediaType == WorkMediaType.Book);
        Assert.AreEqual(work.Id, stored.WorkId);
        Assert.IsTrue(await environment.Db.WorkSourceLinks.AnyAsync(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.NovelWork && link.SourceId == novel.Id), "The imported book is the Work's library entry.");
        Assert.IsTrue(await environment.Db.WorkSourceLinks.AnyAsync(link => link.WorkId == work.Id && link.SourceKind == WorkSourceKind.BookEdition), "Its edition and file belong to the Work too.");
    }

    [TestMethod]
    public async Task RequestingABookMonitorsItUnlessTheOwnerDecidedAndAnAudiobookRequestSaysNothingAboutTheBook()
    {
        await using var environment = await StartAsync(new BookIndexer());
        var resolver = environment.Services.GetRequiredService<MonitoringResolver>();

        var audiobook = await environment.AddAudiobookAsync();
        var work = audiobook.WorkId!.Value;
        Assert.IsNull((await resolver.LoadAsync(work, CancellationToken.None)).WorkDecision, "The audiobook request does not monitor the Book.");

        var book = await environment.AddAsync();
        Assert.AreEqual(work, book.WorkId, "Book and audiobook are targets of one Work.");
        Assert.IsTrue((await resolver.LoadAsync(work, CancellationToken.None)).IsWorkMonitored, "Requesting the Book monitors it.");

        await environment.Services.GetRequiredService<MonitoringCommands>().SetWorkAsync(work, false, CancellationToken.None);
        await SubmitAsync(environment, identities: [BookPdfAcquisitionTests.CatalogId]);
        Assert.IsFalse((await resolver.LoadAsync(work, CancellationToken.None)).IsWorkMonitored, "The owner's decision is not overwritten by a later request.");
    }
}
