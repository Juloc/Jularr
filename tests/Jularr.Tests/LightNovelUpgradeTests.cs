using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BookIndexer = Jularr.Tests.BooksLifecycleTests.BookIndexer;
using Env = Jularr.Tests.BookPdfAcquisitionTests.BookAcquisitionEnvironment;
using Lifecycle = Jularr.Tests.LightNovelLifecycleTests;

namespace Jularr.Tests;

[TestClass]
public sealed class LightNovelUpgradeTests
{
    private const string Unlabelled = Lifecycle.Series + " Vol 1-2";
    private const string Epub = Lifecycle.Series + " Vol 1-2 EPUB";

    // Volumes 1 and 2 are in the library from a release that states no format, so the profile's EPUB cutoff is still open.
    private static async Task<(Env Environment, BookIndexer Indexer, long WorkId)> UnlabelledLibraryAsync()
    {
        var indexer = new BookIndexer(Unlabelled);
        var environment = await Lifecycle.StartAsync(indexer, new Lifecycle.FakeNovelAniList { Volumes = 2 });
        var request = await Lifecycle.SubmitAsync(environment);
        StringAssert.Contains(Assert.ContainsSingle(environment.Sabnzbd.Grabs).NzbName, "Vol 1-2");
        await Lifecycle.DownloadAsync(environment, request, Unlabelled, ($"{Lifecycle.Series} v01.epub", 1, "scan"), ($"{Lifecycle.Series} v02.epub", 2, "scan"));
        return (environment, indexer, (await Lifecycle.WorkAsync(environment)).Id);
    }

    private static async Task<string> ShownTextAsync(Env environment, int volumeNumber) =>
        await (from volume in environment.Db.NovelVolumes
               join chapter in environment.Db.NovelChapters on volume.Id equals chapter.VolumeId
               where volume.Number == volumeNumber
               orderby chapter.Number
               select chapter.OriginalText).FirstAsync();

    private static string LibraryFolder(Env environment) => Path.Combine(environment.Root, "library-novels");

    [TestMethod]
    public async Task AnUnlabelledEditionIsUpgradedToTheProfilesEpubKeepingBothEditionsAndTheReadersPlace()
    {
        var (environment, indexer, workId) = await UnlabelledLibraryAsync();
        await using var scope = environment;
        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed && volume.UpgradeWanted), "An edition below the profile's EPUB cutoff is an upgrade candidate.");
        Assert.AreEqual(2, await environment.Db.WantedItems.CountAsync(item => item.WorkId == workId && item.TargetKind == WantedTargetKind.Volume), "Both installed volumes are wanted for an upgrade.");
        StringAssert.Contains(await ShownTextAsync(environment, 1), "(scan)");

        var firstChapter = await (from volume in environment.Db.NovelVolumes join chapter in environment.Db.NovelChapters on volume.Id equals chapter.VolumeId where volume.Number == 1 orderby chapter.Number select chapter).FirstAsync();
        var novelId = firstChapter.WorkId;
        await new NovelProgressService(environment.Db).SaveProgressAsync("owner", firstChapter.Id, 420, "en", 0, 5, CancellationToken.None);
        await new NovelAnnotationService(environment.Db).AddBookmarkAsync("owner", firstChapter.Id, 420, "en", 0, 5, "keep me", null, null, CancellationToken.None);

        await environment.RecoverAsync(DateTime.UtcNow);
        Assert.HasCount(1, environment.Sabnzbd.Grabs, "Nothing better exists yet, and the same release is never downloaded again.");

        indexer.Titles.AddRange([Epub, Unlabelled]);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        StringAssert.Contains(environment.Sabnzbd.Grabs[1].NzbName, "EPUB");
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The running upgrade is not started twice.");

        var upgrade = await environment.Services.GetRequiredService<Jularr.Web.Features.Acquisition.Access.AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.LightNovel, "anilist", Lifecycle.AniListId, CancellationToken.None);
        var stored = await Lifecycle.DownloadAsync(environment, upgrade!, Epub, ($"{Lifecycle.Series} v01 retail.epub", 1, "retail"), ($"{Lifecycle.Series} v02 retail.epub", 2, "retail"));

        Assert.AreEqual(AcquisitionRequestStatus.Completed, stored.Status, stored.StatusMessage);
        Assert.AreEqual(2, await environment.Db.NovelVolumes.CountAsync(), "No volume is duplicated.");
        Assert.AreEqual(4, await environment.Db.NovelVolumeEditions.CountAsync(), "Both editions of both volumes are kept.");
        Assert.AreEqual(4, Directory.EnumerateFiles(LibraryFolder(environment), "*.epub", SearchOption.AllDirectories).Count(), "The older editions stay on disk.");
        StringAssert.Contains(await ShownTextAsync(environment, 1), "(retail)", "The labelled EPUB is the edition the reader shows.");
        var progress = (await new NovelProgressService(environment.Db).GetProgressAsync("owner", novelId, CancellationToken.None))!;
        Assert.AreEqual(firstChapter.Id, progress.ChapterId, "Reading progress stays on the same chapter after the edition changed.");
        Assert.AreEqual(420, progress.PositionPermille);
        Assert.AreEqual(1, await environment.Db.NovelBookmarks.CountAsync(bookmark => bookmark.ChapterId == firstChapter.Id && bookmark.Label == "keep me"));
        Assert.AreEqual(4, await environment.Db.NovelChapters.CountAsync(), "The chapters are the same rows, not copies.");

        var after = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(after.Volumes.All(volume => volume.InstalledQuality == "EPUB" && !volume.UpgradeWanted));
        Assert.AreEqual(0, await environment.Db.WantedItems.CountAsync(item => item.WorkId == workId));
        for (var pass = 1; pass <= 3; pass++)
        {
            await environment.RecoverAsync(DateTime.UtcNow.AddDays(3 + pass));
        }

        Assert.HasCount(2, environment.Sabnzbd.Grabs, "An equal or inferior release is never taken, also after restarts.");
        Assert.AreEqual(4, await environment.Db.NovelVolumeEditions.CountAsync());
    }

    [TestMethod]
    public async Task ADamagedUpgradeIsRejectedItsCopyRemovedAndTheExistingEditionStaysReadable()
    {
        var (environment, indexer, workId) = await UnlabelledLibraryAsync();
        await using var scope = environment;
        indexer.Titles.Add(Epub);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        Assert.HasCount(2, environment.Sabnzbd.Grabs);
        var folder = environment.LightNovelFolder(Epub);
        File.WriteAllBytes(Path.Combine(folder, $"{Lifecycle.Series} v01.epub"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(folder, $"{Lifecycle.Series} v02.epub"), [5, 6, 7, 8]);
        var upgrade = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.LightNovel, "anilist", Lifecycle.AniListId, CancellationToken.None);

        await environment.CompleteDownloadAsync(upgrade!, Lifecycle.Job + Epub);

        Assert.AreEqual(2, await environment.Db.NovelVolumeEditions.CountAsync(), "The damaged files left no editions.");
        Assert.AreEqual(2, Directory.EnumerateFiles(LibraryFolder(environment), "*.epub", SearchOption.AllDirectories).Count(), "The damaged copies were removed; the older editions stay.");
        StringAssert.Contains(await ShownTextAsync(environment, 2), "(scan)");
        var coverage = await Lifecycle.CoverageAsync(environment, workId);
        Assert.IsTrue(coverage.Volumes.All(volume => volume.State == ReadingCoverageState.Installed && volume.InstalledQuality == "UNKNOWN-UNKNOWN"));

        await environment.RecoverAsync(DateTime.UtcNow.AddDays(4));
        Assert.HasCount(2, environment.Sabnzbd.Grabs, "The damaged release is not tried again.");
    }

    [TestMethod]
    public async Task ChangingTheProfileOfTheWorkShowsTheEditionItPrefers()
    {
        var (environment, indexer, workId) = await UnlabelledLibraryAsync();
        await using var scope = environment;
        indexer.Titles.Add(Epub);
        await environment.RecoverAsync(DateTime.UtcNow.AddDays(2));
        var upgrade = await environment.Services.GetRequiredService<AcquisitionAccessStore>().FindLatestAsync(MediaAcquisitionKind.LightNovel, "anilist", Lifecycle.AniListId, CancellationToken.None);
        await Lifecycle.DownloadAsync(environment, upgrade!, Epub, ($"{Lifecycle.Series} v01 retail.epub", 1, "retail"), ($"{Lifecycle.Series} v02 retail.epub", 2, "retail"));
        StringAssert.Contains(await ShownTextAsync(environment, 1), "(retail)");

        // A profile that prefers the unlabelled edition makes the reader show it again; nothing is deleted or duplicated.
        var profiles = environment.Services.GetRequiredService<Jularr.Web.Features.Acquisition.Quality.QualityProfileStore>();
        var state = await profiles.LoadAsync(CancellationToken.None);
        var original = state.Profiles.First(profile => profile.AllowedQualities.Contains("EPUB", StringComparer.OrdinalIgnoreCase));
        var reversed = original with { Id = "unlabelled-first", Name = "Unlabelled first", QualityOrder = ["UNKNOWN-UNKNOWN", "EPUB"], AllowedQualities = ["UNKNOWN-UNKNOWN", "EPUB"], UpgradeCutoffQuality = "UNKNOWN-UNKNOWN" };
        await profiles.UpsertAsync(reversed, CancellationToken.None);
        await profiles.AssignWorkAsync(workId, reversed.Id, CancellationToken.None);
        await environment.Services.GetRequiredService<LightNovelVersionSelector>().ReselectAsync(workId, CancellationToken.None);

        StringAssert.Contains(await ShownTextAsync(environment, 1), "(scan)");
        Assert.AreEqual(4, await environment.Db.NovelVolumeEditions.CountAsync());
        Assert.AreEqual(2, await environment.Db.NovelVolumes.CountAsync());
    }
}
