using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Novels;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReadingUnitTests
{
    private const string Provider = "anilist";

    private static ProviderUnit[] Units(params int[] numbers) => [.. numbers.Select(number => new ProviderUnit($"u{number}", number, $"Unit {number}"))];

    private static async Task<(Work Work, Guid NovelId)> NovelAsync(AppDbContext db, string key = "frieren")
    {
        var work = await new WorkService(db).CreateWorkAsync(WorkMediaType.LightNovel, "Frieren", 2020, CancellationToken.None);
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);
        var novel = new NovelWork { SourceProvider = "upload", SourceKey = key, SourceUrl = "u", Title = "Frieren" };
        db.NovelWorks.Add(novel);
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = WorkSourceKind.NovelWork, SourceId = novel.Id });
        await db.SaveChangesAsync();
        return (work, novel.Id);
    }

    private static async Task<string> LocalVolumeAsync(AppDbContext db, Guid novelId, int number)
    {
        var volume = new NovelVolume { WorkId = novelId, Number = number, SourceKey = $"v{number}" };
        db.NovelVolumes.Add(volume);
        await db.SaveChangesAsync();
        return volume.Id.ToString();
    }

    private static async Task<List<(WantedTargetKind Kind, Guid Id)>> WantedAsync(AppDbContext db, Work work)
    {
        await new WantedReconciler(db, TimeProvider.System).ReconcileAsync(work.Id, CancellationToken.None);
        return (await db.WantedItems.AsNoTracking().Where(item => item.WorkId == work.Id).ToListAsync()).Select(item => (item.TargetKind, item.TargetId)).ToList();
    }

    private static async Task<Guid> VolumeIdAsync(AppDbContext db, Guid workId, int number) =>
        (await db.WorkVolumes.AsNoTracking().SingleAsync(volume => volume.WorkId == workId && volume.Number == number)).Id;

    [TestMethod]
    public async Task EnrichmentIsKeyedByProviderIdentityAndNeverMergesByNumber()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var (work, _) = await NovelAsync(db);
        var units = new ReadingUnits(db);

        Assert.AreEqual(new ReadingUnitEnrichment(2, 0, 0), await units.EnrichVolumesAsync(work.Id, Provider, [.. Units(1, 2), new ProviderUnit("", 3, "No identity")], CancellationToken.None));
        Assert.AreEqual(new ReadingUnitEnrichment(0, 0, 0), await units.EnrichVolumesAsync(work.Id, Provider, Units(1, 2), CancellationToken.None), "Repeating it changes nothing.");
        Assert.AreEqual(new ReadingUnitEnrichment(0, 1, 0), await units.EnrichVolumesAsync(work.Id, Provider, [new ProviderUnit("u2", 2, "Renamed")], CancellationToken.None));
        Assert.AreEqual(new ReadingUnitEnrichment(0, 0, 1), await units.EnrichVolumesAsync(work.Id, Provider, [new ProviderUnit("other", 1, "Same number, other identity")], CancellationToken.None), "Another identity with a taken number is not merged into it.");

        var stored = await db.WorkVolumes.AsNoTracking().Where(volume => volume.WorkId == work.Id).OrderBy(volume => volume.Number).ToListAsync();
        CollectionAssert.AreEqual(new[] { "u1", "u2" }, stored.Select(volume => volume.ExternalId).ToArray());
        Assert.AreEqual("Renamed", stored[1].Title);
    }

    [TestMethod]
    public async Task AWorkWithIdentifiedVolumesIsWantedVolumeByVolumeAndABoundLocalVolumeCoversOnlyItsOwn()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var (work, novelId) = await NovelAsync(db);
        var units = new ReadingUnits(db);
        await units.EnrichVolumesAsync(work.Id, Provider, Units(1, 2, 3), CancellationToken.None);

        var wanted = await WantedAsync(db, work);
        Assert.AreEqual(3, wanted.Count(item => item.Kind == WantedTargetKind.Volume));
        Assert.IsFalse(wanted.Any(item => item.Kind == WantedTargetKind.Work), "The volumes replace the whole-Work target.");

        var local = await LocalVolumeAsync(db, novelId, 7);
        Assert.IsEmpty(await WantedAsync(db, work), "A local volume nothing ties to a unit keeps the Work whole, and it holds something.");

        Assert.IsTrue(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, local, await VolumeIdAsync(db, work.Id, 2), isOwnerMapping: true, CancellationToken.None));
        var afterBinding = await WantedAsync(db, work);
        CollectionAssert.AreEquivalent(new[] { await VolumeIdAsync(db, work.Id, 1), await VolumeIdAsync(db, work.Id, 3) }, afterBinding.Select(item => item.Id).ToArray(), "Local number 7 was mapped to volume 2 by the owner, so volumes 1 and 3 remain.");
    }

    [TestMethod]
    public async Task AnUnmappedLocalVolumeKeepsTheWorkWholeSoOwnedContentIsNotWantedAgain()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var (work, novelId) = await NovelAsync(db);
        await LocalVolumeAsync(db, novelId, 1);
        await new ReadingUnits(db).EnrichVolumesAsync(work.Id, Provider, Units(1, 2), CancellationToken.None);

        Assert.IsEmpty(await WantedAsync(db, work), "Local volume 1 is not taken for provider volume 1 by number.");
    }

    [TestMethod]
    public async Task AWorkWithoutIdentifiedUnitsIsWantedAsAWhole()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var (work, _) = await NovelAsync(db);
        db.WorkVolumes.Add(new WorkVolume { WorkId = work.Id, Number = 1 });
        await db.SaveChangesAsync();

        Assert.AreEqual(WantedTargetKind.Work, (await WantedAsync(db, work)).Single().Kind);
    }

    [TestMethod]
    public async Task AVolumeDecisionOverridesTheWorkAndAMappingNeedsUnitsOfTheSameWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var (work, novelId) = await NovelAsync(db);
        var (other, otherNovelId) = await NovelAsync(db, "other");
        var units = new ReadingUnits(db);
        await units.EnrichVolumesAsync(work.Id, Provider, Units(1, 2), CancellationToken.None);
        await units.EnrichVolumesAsync(other.Id, Provider, Units(1), CancellationToken.None);
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Volume, await VolumeIdAsync(db, work.Id, 2), false, CancellationToken.None);
        Assert.AreEqual(await VolumeIdAsync(db, work.Id, 1), (await WantedAsync(db, work)).Single().Id);

        var local = await LocalVolumeAsync(db, novelId, 1);
        var foreignLocal = await LocalVolumeAsync(db, otherNovelId, 1);
        Assert.IsFalse(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, local, await VolumeIdAsync(db, other.Id, 1), true, CancellationToken.None), "A unit of another Work.");
        Assert.IsFalse(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, foreignLocal, await VolumeIdAsync(db, work.Id, 1), true, CancellationToken.None), "A local volume of another Work.");

        var first = await VolumeIdAsync(db, work.Id, 1);
        var second = await VolumeIdAsync(db, work.Id, 2);
        Assert.IsTrue(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, local, first, true, CancellationToken.None));
        Assert.IsFalse(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, local, second, false, CancellationToken.None), "An import never replaces the owner's mapping.");
        Assert.AreEqual(first, (await db.WorkUnitBindings.AsNoTracking().SingleAsync(binding => binding.LocalId == local)).WorkVolumeId);
        Assert.IsTrue(await units.TieAsync(work.Id, WorkUnitLocalKind.NovelVolume, local, second, true, CancellationToken.None), "The owner can correct it.");
        Assert.AreEqual(second, (await db.WorkUnitBindings.AsNoTracking().SingleAsync(binding => binding.LocalId == local)).WorkVolumeId);
    }

    [TestMethod]
    public async Task MangaChaptersFollowTheSameRules()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var work = await new WorkService(db).CreateWorkAsync(WorkMediaType.Manga, "Frieren", 2020, CancellationToken.None);
        await MonitoringTestSupport.Commands(db).SetAsync(MonitoringTargetKind.Work, work.Id, true, CancellationToken.None);
        var series = Guid.NewGuid();
        db.WorkSourceLinks.Add(new WorkSourceLink { WorkId = work.Id, SourceKind = WorkSourceKind.MangaSeries, SourceId = series });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "MangaSeries" ("Id", "Title", "SourcePath", "Direction", "CreatedAt", "UpdatedAt") VALUES ({series.ToString()}, 'Frieren', '/manga/frieren', 'rtl', 'now', 'now')""");
        var units = new ReadingUnits(db);
        await units.EnrichChaptersAsync(work.Id, Provider, [new ProviderUnit("c1", 1, "One"), new ProviderUnit("c1.5", 1.5, "Half", IsSpecial: true), new ProviderUnit("c2", 2, "Two")], CancellationToken.None);
        Assert.AreEqual(3, (await WantedAsync(db, work)).Count(item => item.Kind == WantedTargetKind.Chapter));

        var chapterId = Guid.NewGuid().ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "MangaChapters" ("Id", "SeriesId", "Number", "Title", "SourcePath", "SourceKind", "PageCount", "SourceUpdatedAt", "CreatedAt", "UpdatedAt") VALUES ({chapterId}, {series.ToString()}, 1, 'c1', '/manga/frieren/c1.cbz', 'cbz', 10, 'now', 'now', 'now')""");
        Assert.IsEmpty(await WantedAsync(db, work), "The local chapter is not tied to a unit, so the Work is whole and installed.");

        var chapterOne = (await db.WorkChapters.AsNoTracking().SingleAsync(chapter => chapter.ExternalId == "c1")).Id;
        Assert.IsTrue(await units.TieAsync(work.Id, WorkUnitLocalKind.MangaChapter, chapterId, chapterOne, true, CancellationToken.None));
        var wanted = await WantedAsync(db, work);
        Assert.AreEqual(2, wanted.Count);
        Assert.IsFalse(wanted.Any(item => item.Id == chapterOne));
    }
}
