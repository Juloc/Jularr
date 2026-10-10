using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.MangaLifecycleTests;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaAdminPageTests
{
    private const string Job = "/data/downloads/complete/manga/";

    // Volumes 1 and 2 are in the library, volume 3 is still missing.
    private static async Task<(Env Environment, MangaAdminPageHost Host, long WorkId, string Page)> PartlyInLibraryAsync(BookIndexer indexer, Lifecycle.FakeAniList? aniList = null)
    {
        var environment = await Lifecycle.StartAsync(indexer, aniList ?? new Lifecycle.FakeAniList { Volumes = 3 });
        var request = await Lifecycle.SubmitAsync(environment);
        var folder = environment.MangaFolder("Frieren Vol 1-2");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        await environment.CompleteDownloadAsync(request, Job + "Frieren Vol 1-2");
        var host = await MangaAdminPageHost.CreateAsync(environment);
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        return (environment, host, workId, $"/Admin/Manga/Work/{workId}");
    }

    [TestMethod]
    public async Task ThePageShowsTheTitleItsIdentityVolumesWithTheirStateLibraryLinksAndWhatIsStillWanted()
    {
        var (environment, host, workId, page) = await PartlyInLibraryAsync(new BookIndexer("Frieren Vol 1-2 CBZ"));
        await using var _1 = environment;
        await using var _2 = host;
        var series = await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == workId && link.SourceKind == Jularr.Web.Features.MediaCore.WorkSourceKind.MangaSeries);

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, "Frieren");
        StringAssert.Contains(html, "葬送のフリーレン", "The native title is shown.");
        StringAssert.Contains(html, "https://anilist.co/manga/154587");
        StringAssert.Contains(html, "2 of 3 volumes");
        StringAssert.Contains(html, "data-manga-volume=\"1\" data-state=\"installed\"");
        StringAssert.Contains(html, "data-manga-volume=\"3\" data-state=\"missing\"");
        StringAssert.Contains(html, "Wanted", "The missing monitored volume is wanted.");
        StringAssert.Contains(html, $"href=\"/Manga/Series/{series.SourceId}\"");
        StringAssert.Contains(html, "name=\"profileId\"");
        StringAssert.Contains(html, "aria-checked=\"true\"", "Requesting the Manga monitored it.");
        StringAssert.Contains(html, "CBZ");
        Assert.IsFalse(html.Contains("Not readable", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SwitchingAVolumeOffRemovesItFromWantedAndSwitchingTheWorkBackOnRestoresItWithoutLosingTheLibrary()
    {
        var (environment, host, workId, page) = await PartlyInLibraryAsync(new BookIndexer("Frieren Vol 1-2 CBZ"));
        await using var _1 = environment;
        await using var _2 = host;
        var third = await environment.Db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == workId && volume.Number == 3);
        Assert.IsTrue(await environment.Db.WantedItems.AnyAsync(item => item.TargetId == third.Id));

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", third.Id.ToString()), new("state", "false")]));

        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync(item => item.WorkId == workId), "A volume that is off is not wanted.");
        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "data-manga-volume=\"3\" data-state=\"missing\"");
        StringAssert.Contains(html, "Follow series", "A unit with its own decision can return to following the title.");
        Assert.AreEqual(2, (await Lifecycle.CoverageAsync(environment, workId)).Volumes.Count(volume => volume.State == ReadingCoverageState.Installed), "The library is untouched.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", third.Id.ToString()), new("state", "inherit")]));
        Assert.IsTrue(await environment.Db.WantedItems.AnyAsync(item => item.TargetId == third.Id), "Following the title again wants the missing volume.");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "work"), new("state", "false")]));
        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync(item => item.WorkId == workId));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", Guid.NewGuid().ToString()), new("state", "true")]), "A volume of another Work cannot be switched here.");
    }

    [TestMethod]
    public async Task SearchNowForOneVolumeRequestsOnlyThatVolumeThroughTheSharedPipeline()
    {
        var indexer = new BookIndexer("Frieren Vol 1-2 CBZ");
        var (environment, host, workId, page) = await PartlyInLibraryAsync(indexer);
        await using var _1 = environment;
        await using var _2 = host;
        indexer.Titles.AddRange(["Frieren v03 CBZ", "Frieren v02 CBZ"]);
        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count);

        var status = await host.PostAsync(page, $"{page}?handler=Search", [new("volume", "3")]);

        Assert.AreEqual(HttpStatusCode.Found, status);
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v03", "Only the requested volume is searched for.");
        var latest = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.Manga, "anilist", Lifecycle.AniListId, CancellationToken.None);
        Assert.AreEqual(AcquisitionRequestStatus.Downloading, latest!.Status);
        Assert.AreEqual(workId, latest.WorkId);
        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "Downloading");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Search", []), "A second Search now is refused with a message, never a second download.");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task SearchNowForAVolumeTheOwnerSwitchedOffStillSearchesExactlyThatVolume()
    {
        var indexer = new BookIndexer("Frieren Vol 1-2 CBZ");
        var (environment, host, workId, page) = await PartlyInLibraryAsync(indexer);
        await using var _1 = environment;
        await using var _2 = host;
        var third = await environment.Db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == workId && volume.Number == 3);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", third.Id.ToString()), new("state", "false")]));
        indexer.Titles.AddRange(["Frieren v03 CBZ", "Frieren v02 CBZ"]);

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Search", [new("volume", "3")]));

        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v03", "The owner asked for volume 3, so it is searched although Monitoring is off.");
    }

    [TestMethod]
    public async Task AFailedRequestIsExplainedWithItsReasonRetryRunsItAgainAndManualSearchIsOneClickAway()
    {
        var indexer = new BookIndexer();
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList { Volumes = 2 });
        await using var _1 = environment;
        var request = await Lifecycle.SubmitAsync(environment);
        await environment.Services.GetRequiredService<AcquisitionAccessStore>().UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "No release found on the indexers.", null, null, null, CancellationToken.None);
        await using var host = await MangaAdminPageHost.CreateAsync(environment);
        var page = $"/Admin/Manga/Work/{request.WorkId}";

        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "The last request failed: No release found on the indexers.");
        StringAssert.Contains(html, "Retry");
        StringAssert.Contains(html, $"/Admin/ReadingManualSearch/{request.Id:D}", "Manual Search is one click away for a failed request.");

        indexer.Titles.Add("Frieren Vol 1-2 CBZ");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Retry", []));

        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "Retry ran the request again and took the release.");
    }

    [TestMethod]
    public async Task TheProfileCanBeAssignedOnlyFromThoseThatTakeMangaAndRefreshAddsWhatAniListNowStates()
    {
        var aniList = new Lifecycle.FakeAniList();
        var (environment, host, workId, page) = await PartlyInLibraryAsync(new BookIndexer("Frieren Vol 1-2 CBZ"), aniList);
        await using var _1 = environment;
        await using var _2 = host;
        var profiles = environment.Services.GetRequiredService<QualityProfileStore>();
        var manga = (await profiles.LoadAsync(CancellationToken.None)).Profiles.First(profile => profile.Id == ReadingQualityProfiles.DefaultMangaId);

        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "AniList does not state volumes or chapters", "An ongoing title with no stated structure is searched whole.");

        aniList.Volumes = 3;
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Refresh", []));
        Assert.AreEqual(3, await environment.Db.WorkVolumes.CountAsync(volume => volume.WorkId == workId));
        html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "data-manga-volume=\"3\"");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", manga.Id)]));
        Assert.AreEqual(manga.Id, (await profiles.LoadAsync(CancellationToken.None)).WorkAssignments[workId.ToString("D")]);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", "does-not-exist")]));
        Assert.AreEqual(manga.Id, (await profiles.LoadAsync(CancellationToken.None)).WorkAssignments[workId.ToString("D")], "An unknown or unsuitable profile is refused.");
    }

    [TestMethod]
    public async Task AFileThatCannotBeReadIsReportedAndLongChapterListsArePaged()
    {
        var environment = await Lifecycle.StartAsync(new BookIndexer("Frieren c001-002 CBZ"), new Lifecycle.FakeAniList { Chapters = 95 });
        await using var _1 = environment;
        var request = await Lifecycle.SubmitAsync(environment);
        Lifecycle.WriteVolume(environment.MangaFolder("Frieren c001-002"), "Frieren c001-002.cbz");
        await environment.CompleteDownloadAsync(request, Job + "Frieren c001-002");
        await using var host = await MangaAdminPageHost.CreateAsync(environment);
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        var page = $"/Admin/Manga/Work/{workId}";

        var first = await host.GetHtmlAsync(page);
        var second = await host.GetHtmlAsync($"{page}?pageNumber=2");

        Assert.AreEqual(40, System.Text.RegularExpressions.Regex.Matches(first, "data-manga-chapter=").Count, "A page carries 40 chapters.");
        StringAssert.Contains(first, "data-manga-chapter=\"1\"");
        Assert.IsFalse(first.Contains("data-manga-chapter=\"41\"", StringComparison.Ordinal));
        StringAssert.Contains(second, "data-manga-chapter=\"41\"");
        StringAssert.Contains(first, "1 of 3");

        var local = await environment.Db.Database.SqlQuery<string>($"""SELECT chapter."SourcePath" AS "Value" FROM "MangaChapters" chapter LIMIT 1""").FirstAsync();
        File.Delete(local);
        Assert.IsFalse(Directory.Exists(local) || File.Exists(local));
        StringAssert.Contains(await host.GetHtmlAsync(page), "Not readable");
    }

    [TestMethod]
    public async Task OnlyTheOwnerReachesThePageAndWithTheMangaModuleOffThereIsNoPage()
    {
        var (environment, host, _, page) = await PartlyInLibraryAsync(new BookIndexer("Frieren Vol 1-2 CBZ"));
        await using var _1 = environment;
        await using var _2 = host;

        Assert.AreNotEqual(HttpStatusCode.OK, await host.GetStatusAsync(page, asOwner: false), "A plain user does not reach the Admin page.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Admin/Manga/Work/999999"));
        await host.Modules.SetAsync(InstanceModule.Manga, false);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(page), "With the Manga module off there is no Manga page.");
        await host.Modules.SetAsync(InstanceModule.Manga, true);
        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync(page));
    }
}
