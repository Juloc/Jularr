using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.LightNovelLifecycleTests;

namespace Jularr.Tests;

[TestClass]
public sealed class LightNovelAdminPageTests
{
    private const string Release = Lifecycle.Series + " Vol 1-2 EPUB";

    // Volumes 1 and 2 are in the library, volume 3 is still missing.
    private static async Task<(Env Environment, MangaAdminPageHost Host, long WorkId, string Page)> PartlyInLibraryAsync(BookIndexer indexer, Lifecycle.FakeNovelAniList? aniList = null)
    {
        var environment = await Lifecycle.StartAsync(indexer, aniList ?? new Lifecycle.FakeNovelAniList { Volumes = 3 });
        var request = await Lifecycle.SubmitAsync(environment);
        await Lifecycle.DownloadAsync(environment, request, Release, ($"{Lifecycle.Series} v01.epub", 1, "retail"), ($"{Lifecycle.Series} v02.epub", 2, "retail"));
        var host = await MangaAdminPageHost.CreateAsync(environment);
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        return (environment, host, workId, $"/Admin/Novels/Work/{workId}");
    }

    [TestMethod]
    public async Task ThePageShowsTitlesAuthorIdentityVolumesEditionsAndTheLibraryAndReaderLinks()
    {
        var (environment, host, workId, page) = await PartlyInLibraryAsync(new BookIndexer(Release));
        await using var _1 = environment;
        await using var _2 = host;
        var novelId = (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.NovelWork)).SourceId;
        var firstChapter = await environment.Db.NovelChapters.AsNoTracking().OrderBy(chapter => chapter.Number).Select(chapter => chapter.Id).FirstAsync();

        var html = await host.GetHtmlAsync(page);

        StringAssert.Contains(html, Lifecycle.Series);
        StringAssert.Contains(html, "転生の賢者", "The native title is shown.");
        StringAssert.Contains(html, "Magonote", "The author is shown.");
        StringAssert.Contains(html, "https://anilist.co/manga/" + Lifecycle.AniListId);
        StringAssert.Contains(html, "2 of 3 volumes");
        StringAssert.Contains(html, "data-manga-volume=\"1\" data-state=\"installed\"");
        StringAssert.Contains(html, "data-manga-volume=\"3\" data-state=\"missing\"");
        StringAssert.Contains(html, "Wanted", "The missing monitored volume is wanted.");
        StringAssert.Contains(html, $"href=\"/Novels/Work/{novelId}\"");
        StringAssert.Contains(html, $"href=\"/Novels/Read/{firstChapter}\"");
        StringAssert.Contains(html, "Published EPUB volumes", "The published edition is named as such, apart from a web novel.");
        StringAssert.Contains(html, "name=\"profileId\"");
        StringAssert.Contains(html, "aria-checked=\"true\"", "Requesting the Light Novel monitored it.");
        StringAssert.Contains(html, "EPUB");
        StringAssert.Contains(html, "2 chapters");
        Assert.IsFalse(html.Contains("Not readable", StringComparison.Ordinal));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/Manga/Work/{workId}"), "The Manga page does not serve a Light Novel.");
    }

    [TestMethod]
    public async Task VolumesAreSwitchedOffAndBackOnAndSearchNowRequestsOnlyTheMissingVolume()
    {
        var indexer = new BookIndexer(Release);
        var (environment, host, workId, page) = await PartlyInLibraryAsync(indexer);
        await using var _1 = environment;
        await using var _2 = host;
        var third = await environment.Db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == workId && volume.Number == 3);
        Assert.IsTrue(await environment.Db.WantedItems.AnyAsync(item => item.TargetId == third.Id));

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", third.Id.ToString()), new("state", "false")]));
        Assert.IsFalse(await environment.Db.WantedItems.AnyAsync(item => item.WorkId == workId), "A volume that is off is not wanted.");
        StringAssert.Contains(await host.GetHtmlAsync(page), "Follow series");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", Guid.NewGuid().ToString()), new("state", "true")]));
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Monitor", [new("scope", "volume"), new("targetId", third.Id.ToString()), new("state", "inherit")]));
        Assert.IsTrue(await environment.Db.WantedItems.AnyAsync(item => item.TargetId == third.Id));

        indexer.Titles.AddRange([$"{Lifecycle.Series} v03 EPUB", $"{Lifecycle.Series} v02 EPUB"]);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Search", [new("volume", "3")]));

        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "v03", "Only the requested volume is searched for.");
        StringAssert.Contains(await host.GetHtmlAsync(page), "Downloading");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Search", []), "A second Search now is refused with a message, never a second download.");
        Assert.AreEqual(2, environment.Sabnzbd.Grabs.Count);
    }

    [TestMethod]
    public async Task AFailedRequestIsExplainedWithItsReasonRetryRunsItAgainAndManualSearchIsOneClickAway()
    {
        var indexer = new BookIndexer();
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeNovelAniList { Volumes = 2 });
        await using var _1 = environment;
        var request = await Lifecycle.SubmitAsync(environment);
        await environment.Services.GetRequiredService<AcquisitionAccessStore>().UpdateStatusAsync(request.Id, AcquisitionRequestStatus.Failed, "No release found on the indexers.", null, null, null, CancellationToken.None);
        await using var host = await MangaAdminPageHost.CreateAsync(environment);
        var page = $"/Admin/Novels/Work/{request.WorkId}";

        var html = await host.GetHtmlAsync(page);
        StringAssert.Contains(html, "The last request failed: No release found on the indexers.");
        StringAssert.Contains(html, "Retry");
        StringAssert.Contains(html, $"/Admin/ReadingManualSearch/{request.Id:D}");

        indexer.Titles.Add(Release);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Retry", []));

        Assert.AreEqual(1, environment.Sabnzbd.Grabs.Count, "Retry ran the request again and took the release.");
    }

    [TestMethod]
    public async Task EditionsShowTheOlderOneAsKeptAndTheProfileIsAssignedOnlyFromThoseThatTakeEpub()
    {
        var indexer = new BookIndexer("Reincarnated Sage Vol 1-2");
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeNovelAniList { Volumes = 2 });
        await using var _1 = environment;
        var request = await Lifecycle.SubmitAsync(environment);
        await Lifecycle.DownloadAsync(environment, request, "Reincarnated Sage Vol 1-2", ($"{Lifecycle.Series} v01.epub", 1, "scan"), ($"{Lifecycle.Series} v02.epub", 2, "scan"));
        indexer.Titles.Add(Release);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        var upgrade = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.LightNovel, "anilist", Lifecycle.AniListId, CancellationToken.None);
        await Lifecycle.DownloadAsync(environment, upgrade!, Release, ($"{Lifecycle.Series} v01 retail.epub", 1, "retail"), ($"{Lifecycle.Series} v02 retail.epub", 2, "retail"));
        await using var host = await MangaAdminPageHost.CreateAsync(environment);
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        var page = $"/Admin/Novels/Work/{workId}";

        var html = await host.GetHtmlAsync(page);

        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(html, "Older version").Count, "The two older editions are marked as kept.");
        StringAssert.Contains(html, "2 of 2 volumes");
        Assert.IsFalse(html.Contains("Upgrade wanted", StringComparison.Ordinal), "A library at the cutoff wants no upgrade.");

        var profiles = environment.Services.GetRequiredService<QualityProfileStore>();
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", ReadingQualityProfiles.DefaultLightNovelId)]));
        Assert.AreEqual(ReadingQualityProfiles.DefaultLightNovelId, (await profiles.LoadAsync(CancellationToken.None)).WorkAssignments[workId.ToString("D")]);
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync(page, $"{page}?handler=Profile", [new("profileId", ReadingQualityProfiles.DefaultMangaId)]));
        Assert.AreEqual(ReadingQualityProfiles.DefaultLightNovelId, (await profiles.LoadAsync(CancellationToken.None)).WorkAssignments[workId.ToString("D")], "A profile that takes no EPUB is refused.");
    }

    [TestMethod]
    public async Task OnlyTheOwnerReachesThePageAndWithTheNovelModuleOffThereIsNoPage()
    {
        var (environment, host, _, page) = await PartlyInLibraryAsync(new BookIndexer(Release));
        await using var _1 = environment;
        await using var _2 = host;

        Assert.AreNotEqual(HttpStatusCode.OK, await host.GetStatusAsync(page, asOwner: false), "A plain user does not reach the Admin page.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Admin/Novels/Work/999999"));
        await host.Modules.SetAsync(InstanceModule.Novel, false);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync(page), "With the Light Novel module off there is no page.");
        await host.Modules.SetAsync(InstanceModule.Novel, true);
        Assert.AreEqual(HttpStatusCode.OK, await host.GetStatusAsync(page));
    }
}
