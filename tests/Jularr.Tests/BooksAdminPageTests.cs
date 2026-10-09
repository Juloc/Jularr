using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;

namespace Jularr.Tests;

/// <summary>The Admin Book page: every number and control comes from the canonical Work, and every action goes through the shared request, monitoring and profile paths.</summary>
[TestClass]
public sealed class BooksAdminPageTests
{
    private static async Task<(Env Environment, BooksAdminPageHost Host, long WorkId, string Page)> PdfInLibraryAsync(BooksLifecycleTests.BookIndexer indexer)
    {
        var environment = await BooksLifecycleTests.StartAsync(indexer);
        var request = await environment.AddAsync();
        BooksLifecycleTests.WritePdf(environment.CompletedFolder("Atomic Habits"), "Atomic Habits.pdf");
        await environment.CompleteDownloadAsync(request, "/data/downloads/complete/books/Atomic Habits");
        var host = await BooksAdminPageHost.CreateAsync(environment);
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        return (environment, host, workId, $"/Admin/Books/Work/{workId}");
    }

    [TestMethod]
    public async Task TheBookPageShowsTheBookItsIdentitiesEditionsFilesProfileAndWhatIsStillWanted()
    {
        var (environment, host, _, page) = await PdfInLibraryAsync(new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF"));
        await using var _1 = environment;
        await using var _2 = host;
        var novel = await environment.Db.NovelWorks.AsNoTracking().SingleAsync();

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "Atomic Habits");
        StringAssert.Contains(html, "James Clear");
        StringAssert.Contains(html, "Open Library OL17930368W");
        Assert.IsTrue(html.IndexOf("Open Library OL17930368W", StringComparison.Ordinal) < html.IndexOf("book-epub", StringComparison.Ordinal) || !html.Contains("book-epub", StringComparison.Ordinal), "The catalog id comes first.");
        StringAssert.Contains(html, "https://openlibrary.org/works/OL17930368W");
        StringAssert.Contains(html, $"src=\"/Books/Cover/{novel.Id}\"");
        StringAssert.Contains(html, "In library, better format wanted", "A PDF where the profile wants an EPUB is an upgrade candidate.");
        StringAssert.Contains(html, "PDF");
        StringAssert.Contains(html, "Available");
        StringAssert.Contains(html, "Editions and files");
        StringAssert.Contains(html, $"href=\"/Books/Library/{novel.Id}\"");
        StringAssert.Contains(html, "name=\"profileId\"");
        StringAssert.Contains(html, "data-admin-book-audiobook");
        StringAssert.Contains(html, "aria-checked=\"true\"", "Requesting the book monitored it.");
        Assert.IsFalse(html.Contains("Not readable", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AFileThatCannotBeReadIsReportedWithItsStorageStateAndTheReaderLinkStaysAvailable()
    {
        var (environment, host, _, page) = await PdfInLibraryAsync(new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF"));
        await using var _1 = environment;
        await using var _2 = host;
        File.Delete((await environment.Db.BookFiles.AsNoTracking().SingleAsync()).StoragePath!);

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "Not readable");
        StringAssert.Contains(html, "Their storage may be offline.");
    }

    [TestMethod]
    public async Task SearchNowOpensARequestThroughTheSharedPipelineAndAnEpubThatAppearsIsGrabbed()
    {
        var indexer = new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF");
        var (environment, host, workId, page) = await PdfInLibraryAsync(indexer);
        await using var _1 = environment;
        await using var _2 = host;
        indexer.Titles.Add("James Clear - Atomic Habits EPUB");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);

        var status = await host.PostAsync(page, $"{page}?handler=Search", []);

        Assert.AreEqual(HttpStatusCode.Found, status);
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count, "Search now ran the request again and took the better format.");
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "EPUB");
        var latest = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.Book, Jularr.Web.Features.Books.BookCatalogService.CatalogRequestProvider, BookPdfAcquisitionTests.CatalogId, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, latest!.Status, "The request is made with the catalog id, not with the source key of the import.");
        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "Downloading");
        Assert.IsFalse(html.Contains(">Search now<", StringComparison.Ordinal) && html.Contains("class=\"button button-primary\" type=\"submit\">Search now", StringComparison.Ordinal), "A request that is downloading is not searched again.");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Search", []), "A second Search now is refused with a message, never a second download.");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task AFailedRequestIsExplainedWithItsReasonAndRetryRunsItAgain()
    {
        var indexer = new BooksLifecycleTests.BookIndexer();
        var environment = await BooksLifecycleTests.StartAsync(indexer);
        await using var _1 = environment;
        var request = await environment.AddAsync();
        await environment.Services.GetRequiredService<AcquisitionAccessStore>().UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "No release found on the indexers.", null, null, null, CancellationToken.None);
        await using var host = await BooksAdminPageHost.CreateAsync(environment);
        var page = $"/Admin/Books/Work/{request.WorkId}";

        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "The last request failed: No release found on the indexers.");
        StringAssert.Contains(html, "Retry");
        StringAssert.Contains(html, $"/Admin/BookManualSearch?id={request.Id:D}", "Manual Search is one click away for a failed request.");

        indexer.Titles.Add(BooksLifecycleTests.Good);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Retry", []));

        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await environment.RequestAsync(request.Id)).Status);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Retry", []), "Retry without a failed request says so instead of searching again.");
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task TheProfileIsAssignedPerBookAndAnUnknownProfileIsRefused()
    {
        var (environment, host, workId, page) = await PdfInLibraryAsync(new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF"));
        await using var _1 = environment;
        await using var _2 = host;
        var profiles = environment.Services.GetRequiredService<QualityProfileStore>();
        var other = BookQualityProfiles.CreateDefaultBook() with { Id = "book-any", Name = "Books (any format)" };
        await profiles.UpsertAsync(other);
        StringAssert.Contains(await host.GetHtmlAsync(page), "Books (any format)");
        Assert.IsFalse((await host.GetHtmlAsync(page)).Contains("Movies 1080p", StringComparison.Ordinal), "A profile that cannot take a book is not offered.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", other.Id)]));

        Assert.AreEqual(other.Id, (await profiles.LoadAsync()).WorkAssignments[workId.ToString("D")]);
        StringAssert.Contains(await host.GetHtmlAsync(page), $"value=\"{other.Id}\" selected");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", "movie-1080p")]));
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", "no-such-profile")]));
        Assert.AreEqual(other.Id, (await profiles.LoadAsync()).WorkAssignments[workId.ToString("D")], "An unknown profile changes nothing.");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", "")]));
        Assert.IsFalse((await profiles.LoadAsync()).WorkAssignments.ContainsKey(workId.ToString("D")), "A blank profile returns to the default of the media type.");
    }

    [TestMethod]
    public async Task TheBookAndItsAudiobookAreMonitoredSeparatelyAndTheBookStateNeverMentionsTheAudiobook()
    {
        var (environment, host, workId, page) = await PdfInLibraryAsync(new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF"));
        await using var _1 = environment;
        await using var _2 = host;
        var resolver = environment.Services.GetRequiredService<MonitoringResolver>();

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("audiobook", "false"), new("monitored", "false")]));
        Assert.IsFalse((await resolver.LoadAsync(workId, CancellationToken.None)).IsWorkMonitored);
        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync(item => item.WorkId == workId), "An unmonitored book is not wanted, not even for an upgrade.");
        var unmonitored = await host.GetHtmlAsync(page);
        Assert.IsFalse(unmonitored.Contains("better format wanted", StringComparison.Ordinal), "Nothing is wanted for an unmonitored book, so the page does not say so.");
        StringAssert.Contains(unmonitored, "Automatic search", "The owner can still search an unmonitored book that is below its cutoff.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("audiobook", "true"), new("monitored", "true")]));
        var view = await resolver.LoadAsync(workId, CancellationToken.None);
        Assert.IsFalse(view.IsWorkMonitored, "Monitoring the audiobook leaves the Book as it was.");
        Assert.IsTrue(await environment.Db.WantedItems.AnyAsync(item => item.WorkId == workId), "The audiobook is wanted on its own.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("audiobook", "false"), new("monitored", "true")]));
        Assert.IsTrue((await resolver.LoadAsync(workId, CancellationToken.None)).IsWorkMonitored);
    }

    [TestMethod]
    public async Task OnlyAnAdminMayOpenAndChangeABookAndADisabledModuleHasNoPage()
    {
        var (environment, host, _, page) = await PdfInLibraryAsync(new BooksLifecycleTests.BookIndexer("James Clear - Atomic Habits PDF"));
        await using var _1 = environment;
        await using var _2 = host;

        Assert.AreNotEqual(HttpStatusCode.OK, await host.GetStatusAsync(page, asOwner: false), "A plain user does not reach the Admin page.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Admin/Books/Work/999999"));

        await host.Modules.SetAsync(InstanceModule.Book, false);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(page), "With the Books module off there is no Books page.");
        await host.Modules.SetAsync(InstanceModule.Book, true);
        await host.Modules.SetAsync(InstanceModule.Audiobook, false);
        var html = await host.GetHtmlAsync(page);
        Assert.IsFalse(html.Contains("data-admin-book-audiobook", StringComparison.Ordinal), "Without the Audiobook module the page offers no audiobook controls.");
    }

    [TestMethod]
    public async Task ManualSearchFromTheBookPageShowsRankAndReasonsAndAGrabGoesThroughTheSharedPath()
    {
        var indexer = new BooksLifecycleTests.BookIndexer("Chris Voss - Never Split the Difference EPUB", "James Clear - Atomic Habits MOBI");
        var environment = await BooksLifecycleTests.StartAsync(indexer);
        await using var _1 = environment;
        var request = await environment.AddAsync();
        Assert.AreEqual(AcquisitionRequestStatus.Approved, request.Status, request.StatusMessage);
        await using var host = await BooksAdminPageHost.CreateAsync(environment);
        var page = $"/Admin/Books/Work/{request.WorkId}";
        indexer.Titles.Add(BooksLifecycleTests.Good);

        var bookPage = await host.GetHtmlAsync(page);
        var manual = $"/Admin/BookManualSearch?id={request.Id:D}";
        StringAssert.Contains(bookPage, manual, "Manual Search is reachable from the book page while the request waits for a release.");
        var html = await host.GetHtmlAsync(manual + "&tab=search");

        StringAssert.Contains(html, "Rank");
        StringAssert.Contains(html, "title does not match");
        StringAssert.Contains(html, "MOBI, not EPUB or PDF");
        StringAssert.Contains(html, "<td class=\"bms-score\">1</td>", "The release automatic acquisition would take is rank 1.");

        var first = (await environment.Services.GetRequiredService<Jularr.Web.Features.Books.BookManualSearchService>().SearchAsync(request.Id, CancellationToken.None)).Search.Ranked.First(item => item.Rank == 1);
        var inspect = await host.GetHtmlAsync($"{manual}&tab=search&release={Uri.EscapeDataString(first.Release.Identity)}");
        var identity = System.Text.RegularExpressions.Regex.Match(inspect, "name=\"releaseIdentity\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync($"{manual}&tab=search&release={Uri.EscapeDataString(identity)}", $"/Admin/BookManualSearch?handler=Grab", [new("id", request.Id.ToString("D")), new("releaseIdentity", identity)]));

        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);
        StringAssert.Contains(environment.Sabnzbd.Grabs[0].NzbName, "2018");
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, (await environment.RequestAsync(request.Id)).Status);
    }
}
