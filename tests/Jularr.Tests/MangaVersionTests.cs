using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.MangaLifecycleTests;
using Upgrades = Jularr.Tests.MangaUpgradeTests;

namespace Jularr.Tests;

[TestClass]
public sealed class MangaVersionTests
{
    private const string Job = "/data/downloads/complete/manga/";

    private static async Task UpgradeToCbzAsync(Env environment, BookIndexer indexer, long workId)
    {
        indexer.Titles.Add("Frieren Vol 1-2 CBZ");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        var folder = environment.MangaFolder("Frieren Vol 1-2 CBZ");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        await environment.CompleteDownloadAsync(await Upgrades.LatestRequestAsync(environment, workId), Job + "Frieren Vol 1-2 CBZ");
    }

    private static string PathOf(Env environment, Guid chapterId) =>
        environment.Db.Database.SqlQuery<string>($"""SELECT "SourcePath" AS "Value" FROM "MangaChapters" WHERE "Id" = {chapterId.ToString()}""").Single();

    [TestMethod]
    public async Task ThePreferredVersionFollowsTheProfileAndReadingProgressFollowsTheShownVersion()
    {
        var (environment, indexer, workId, seriesId) = await Upgrades.ZipLibraryAsync();
        await using var scope = environment;
        await UpgradeToCbzAsync(environment, indexer, workId);
        var repository = new MangaRepository(environment.Db);
        var cbz = (await repository.GetChaptersAsync(seriesId, CancellationToken.None)).First(chapter => chapter.VolumeNumber == 2);
        await repository.SaveProgressAsync("owner", (await repository.GetChapterAsync(cbz.Id, CancellationToken.None))!, 1, CancellationToken.None);

        var profiles = environment.Services.GetRequiredService<QualityProfileStore>();
        await profiles.UpsertAsync(ReadingQualityProfiles.CreateDefaultManga() with { Id = "zip-first", Name = "ZIP first", AllowedQualities = ["ZIP", "CBZ"], QualityOrder = ["ZIP", "CBZ"], UpgradeAllowed = false, UpgradeCutoffQuality = null });
        await profiles.AssignWorkAsync(workId, "zip-first", CancellationToken.None);
        await environment.Services.GetRequiredService<MangaVersionSelector>().ReselectAsync(workId, CancellationToken.None);

        var shown = await repository.GetChaptersAsync(seriesId, CancellationToken.None);
        Assert.HasCount(2, shown);
        Assert.IsTrue(shown.All(chapter => PathOf(environment, chapter.Id).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "The profile prefers the ZIP, so the ZIP is shown.");
        var progress = (await repository.GetProgressAsync("owner", seriesId, CancellationToken.None))!;
        Assert.AreEqual(2, shown.Single(chapter => chapter.Id == progress.ChapterId).VolumeNumber, "Progress stays on volume 2, now in its ZIP version.");
        Assert.AreEqual(1, progress.PageIndex);
        Assert.IsNotNull(await repository.GetSupersedingChapterIdAsync(cbz.Id, CancellationToken.None), "A link to the hidden CBZ leads to the shown version.");

        await profiles.AssignWorkAsync(workId, null, CancellationToken.None);
        await environment.Services.GetRequiredService<MangaVersionSelector>().ReselectAsync(workId, CancellationToken.None);
        Assert.AreEqual(cbz.Id, (await repository.GetProgressAsync("owner", seriesId, CancellationToken.None))!.ChapterId, "Back to the default profile the CBZ is shown and progress returns to it.");
        Assert.AreEqual(4, await Upgrades.VersionsOfAsync(environment, seriesId));
    }

    [TestMethod]
    public async Task APackageOfChaptersAndAVolumeThatOverlapAreNotVersionsOfEachOther()
    {
        await using var environment = await Lifecycle.StartAsync(new BookIndexer("Frieren v01 CBZ"), new Lifecycle.FakeAniList { Volumes = 1 });
        var workId = (await environment.Services.GetRequiredService<RequestWorkBinder>().ResolveAsync(MediaAcquisitionKind.Manga, "anilist", Lifecycle.AniListId, "Frieren", CancellationToken.None))!.Value;
        await environment.Services.GetRequiredService<ReadingStructureService>().RefreshAsync(workId, CancellationToken.None);
        await environment.Services.GetRequiredService<ReadingUnits>().EnrichChaptersAsync(workId, "anilist", [.. Enumerable.Range(1, 4).Select(number => new ProviderUnit($"{Lifecycle.AniListId}:c{number}", number, null))], CancellationToken.None);
        var volume = await environment.Db.WorkVolumes.SingleAsync(item => item.WorkId == workId);
        foreach (var chapter in await environment.Db.WorkChapters.Where(item => item.WorkId == workId).ToListAsync())
        {
            chapter.VolumeId = volume.Id;
        }

        await environment.Db.SaveChangesAsync();
        environment.Db.ChangeTracker.Clear();
        var request = await Lifecycle.SubmitAsync(environment);
        var folder = environment.MangaFolder("Frieren v01 CBZ");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren c001-002.cbz");
        Lifecycle.WriteVolume(folder, "Frieren c001-002.zip");
        await environment.CompleteDownloadAsync(request, Job + "Frieren v01 CBZ");

        var seriesId = (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries)).SourceId;
        Assert.HasCount(2, await new MangaRepository(environment.Db).GetChaptersAsync(seriesId, CancellationToken.None), "The volume and the chapter package are both shown; only the two copies of the package are versions of each other.");
        Assert.AreEqual(3, await Upgrades.VersionsOfAsync(environment, seriesId));
        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.AreEqual(ReadingCoverageState.Installed, coverage.Volumes[0].State);
        Assert.IsTrue(coverage.Volumes[0].Chapters.All(chapter => chapter.Installed));
    }

    [TestMethod]
    public async Task ATitleWithoutAStructureIsUpgradedAsAWholeAndItsVersionsAreMatchedByName()
    {
        var indexer = new BookIndexer("Frieren Complete ZIP");
        await using var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeAniList());
        var request = await Lifecycle.SubmitAsync(environment);
        var folder = environment.MangaFolder("Frieren Complete ZIP");
        Lifecycle.WriteVolume(folder, "Frieren v01.zip");
        Lifecycle.WriteVolume(folder, "Frieren v02.zip");
        await environment.CompleteDownloadAsync(request, Job + "Frieren Complete ZIP");
        var workId = (await environment.RequestAsync(request.Id)).WorkId!.Value;
        var seriesId = (await environment.Db.WorkSourceLinks.AsNoTracking().SingleAsync(link => link.WorkId == workId && link.SourceKind == WorkSourceKind.MangaSeries)).SourceId;
        Assert.AreEqual("ZIP", (await Lifecycle.CoverageAsync(environment, workId)).InstalledQuality);

        await environment.RecoverAsync(DateTime.UtcNow);
        Assert.HasCount(1, environment.Sabnzbd.Grabs, "Only the same ZIP exists, so nothing is downloaded.");

        indexer.Titles.Add("Frieren Complete CBZ");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        var upgrade = environment.MangaFolder("Frieren Complete CBZ");
        Lifecycle.WriteVolume(upgrade, "Frieren v01.cbz");
        Lifecycle.WriteVolume(upgrade, "Frieren v02.cbz");
        await environment.CompleteDownloadAsync(await Upgrades.LatestRequestAsync(environment, workId), Job + "Frieren Complete CBZ");

        Assert.AreEqual(4, await Upgrades.VersionsOfAsync(environment, seriesId));
        Assert.HasCount(2, await new MangaRepository(environment.Db).GetChaptersAsync(seriesId, CancellationToken.None), "Each volume is listed once.");
        Assert.AreEqual("CBZ", (await Lifecycle.CoverageAsync(environment, workId)).InstalledQuality);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(5));
        Assert.HasCount(2, environment.Sabnzbd.Grabs);
    }

    [TestMethod]
    public async Task TheAdminPageShowsTheUpgradeWantedTheRunningDownloadPerVolumeAndTheOlderVersions()
    {
        var (environment, indexer, workId, _) = await Upgrades.ZipLibraryAsync();
        await using var scope = environment;
        await using var host = await MangaAdminPageHost.CreateAsync(environment);
        var page = $"/Admin/Manga/Work/{workId}";
        var before = await host.GetHtmlAsync(page);
        StringAssert.Contains(before, "Upgrade wanted");
        Assert.IsFalse(before.Contains("data-manga-transfer", StringComparison.Ordinal));

        indexer.Titles.Add("Frieren Vol 1-2 CBZ");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        var running = await host.GetHtmlAsync(page);
        Assert.AreEqual(2, Regex.Matches(running, "data-manga-transfer").Count, "Both volumes the running release holds say that they are downloading.");
        StringAssert.Contains(running, "Downloading");

        var folder = environment.MangaFolder("Frieren Vol 1-2 CBZ");
        Lifecycle.WriteVolume(folder, "Frieren v01.cbz");
        Lifecycle.WriteVolume(folder, "Frieren v02.cbz");
        await environment.CompleteDownloadAsync(await Upgrades.LatestRequestAsync(environment, workId), Job + "Frieren Vol 1-2 CBZ");
        var done = await host.GetHtmlAsync(page);
        Assert.IsFalse(done.Contains("data-manga-transfer", StringComparison.Ordinal));
        Assert.IsFalse(done.Contains("Upgrade wanted", StringComparison.Ordinal));
        Assert.AreEqual(2, Regex.Matches(done, ">Older version<").Count, "The ZIP files stay listed as older versions.");
    }
}
