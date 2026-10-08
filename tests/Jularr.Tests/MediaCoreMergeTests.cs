using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class MediaCoreMergeTests
{
    [TestMethod]
    public async Task MergePreservesProgressByMovingLegacyBridgeToSurvivor()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var survivor = await works.CreateWorkAsync(WorkMediaType.Anime, "Frieren", 2023, CancellationToken.None);
        var duplicate = await works.CreateWorkAsync(WorkMediaType.Anime, "Frieren (dup)", 2023, CancellationToken.None);

        // The absorbed work owns the legacy anime record that watch progress / notes / wanted key on.
        var legacyAnimeId = Guid.NewGuid();
        await works.LinkSourceAsync(duplicate.Id, WorkSourceKind.Anime, legacyAnimeId, CancellationToken.None);

        var result = await works.MergeWorksAsync(survivor.Id, duplicate.Id, "owner", CancellationToken.None);

        Assert.AreEqual(1, result.SourceLinks);
        Assert.AreEqual(
            survivor.Id,
            await query.ResolveWorkForSourceAsync(WorkSourceKind.Anime, legacyAnimeId, CancellationToken.None),
            "The bridged legacy record — and therefore its progress/notes/wanted — must now hang off the survivor.");
        Assert.IsNull(await query.GetWorkAsync(duplicate.Id, CancellationToken.None), "The absorbed work is removed.");
        Assert.IsFalse(await db.Works.AsNoTracking().AnyAsync(x => x.Id == duplicate.Id));
    }

    [TestMethod]
    public async Task MergeMovesIdentitiesAndKeepsManualProvenance()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var survivor = await works.CreateWorkAsync(WorkMediaType.Anime, "Survivor", null, CancellationToken.None);
        var duplicate = await works.CreateWorkAsync(WorkMediaType.Anime, "Duplicate", null, CancellationToken.None);

        await works.LinkExternalIdentityAsync(
            duplicate.Id, WorkMediaType.Anime, "anilist", "9001", 1.0, "provider id", true, false,
            MappingReviewState.Confirmed, CancellationToken.None);
        await works.SetManualFieldOverrideAsync(duplicate.Id, "description", CancellationToken.None);

        await works.MergeWorksAsync(survivor.Id, duplicate.Id, "owner", CancellationToken.None);

        Assert.AreEqual(
            survivor.Id,
            await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Anime, "anilist", "9001", CancellationToken.None));

        var provenance = (await query.GetProvenanceAsync(survivor.Id, CancellationToken.None))
            .Single(p => p.FieldKey == "description");
        Assert.IsTrue(provenance.IsManualOverride, "A manual override survives the merge onto the survivor.");
        Assert.AreEqual(MetadataFieldSources.Owner, provenance.Source);
    }

    [TestMethod]
    public async Task MergeDedupesSharedTitlesAgainstTheUniqueIndex()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var survivor = await works.CreateWorkAsync(WorkMediaType.Manga, "Berserk", null, CancellationToken.None);
        var duplicate = await works.CreateWorkAsync(WorkMediaType.Manga, "Berserk", null, CancellationToken.None);
        // Both carry the same primary title; the merge must fold it into one row, not violate the index.
        await works.AddOrUpdateTitleAsync(survivor.Id, WorkTitleType.Primary, "und", "Berserk", "local", true, CancellationToken.None);
        await works.AddOrUpdateTitleAsync(duplicate.Id, WorkTitleType.Primary, "und", "Berserk", "local", true, CancellationToken.None);

        await works.MergeWorksAsync(survivor.Id, duplicate.Id, "owner", CancellationToken.None);

        var titles = await query.GetTitlesAsync(survivor.Id, CancellationToken.None);
        Assert.AreEqual(1, titles.Count(t => t is { TitleType: WorkTitleType.Primary, NormalizedValue: "berserk" }));
    }

    [TestMethod]
    public async Task MergeRecordsIdentityChangeHistory()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var survivor = await works.CreateWorkAsync(WorkMediaType.Movie, "Akira", 1988, CancellationToken.None);
        var duplicate = await works.CreateWorkAsync(WorkMediaType.Movie, "Akira", 1988, CancellationToken.None);

        await works.MergeWorksAsync(survivor.Id, duplicate.Id, "owner", CancellationToken.None);

        var history = await query.GetIdentityChangesAsync(survivor.Id, 10, CancellationToken.None);
        var merge = history.Single(x => x.ChangeType == WorkIdentityChangeType.Merge);
        Assert.AreEqual(survivor.Id, merge.TargetWorkId);
        Assert.AreEqual(duplicate.Id, merge.SourceWorkId);
        Assert.AreEqual("owner", merge.Actor);
    }

    [TestMethod]
    public async Task MergeIntoItselfIsRejected()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var work = await works.CreateWorkAsync(WorkMediaType.Anime, "One", null, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => works.MergeWorksAsync(work.Id, work.Id, "owner", CancellationToken.None));
    }

    [TestMethod]
    public async Task SplitPeelsIdentityIntoANewManualWork()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var original = await works.EnsureWorkByExternalIdentityAsync(
            WorkMediaType.Anime, "anilist", "100", "Wrongly bundled", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            original.Id, WorkMediaType.Anime, "tvdb", "200", 0.5, "weak", false, false,
            MappingReviewState.NeedsReview, CancellationToken.None);

        var split = await works.SplitExternalIdentityToNewWorkAsync(
            WorkMediaType.Anime, "tvdb", "200", "Separate show", "owner", "not the same show", CancellationToken.None);

        Assert.IsNotNull(split);
        Assert.AreNotEqual(original.Id, split!.Id);
        Assert.AreEqual(
            split.Id,
            await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Anime, "tvdb", "200", CancellationToken.None));
        // The identity that stayed behind is untouched.
        Assert.AreEqual(
            original.Id,
            await query.FindWorkIdByExternalIdentityAsync(WorkMediaType.Anime, "anilist", "100", CancellationToken.None));

        var moved = db.WorkExternalIdentities.Single(x => x.Provider == "tvdb" && x.ExternalId == "200");
        Assert.IsTrue(moved.IsManualOverride, "A split is an owner correction protected from refreshes.");
        Assert.AreEqual(MappingReviewState.Confirmed, moved.ReviewState);

        var history = await query.GetIdentityChangesAsync(split.Id, 10, CancellationToken.None);
        Assert.IsTrue(history.Any(x => x.ChangeType == WorkIdentityChangeType.Split));
    }

    [TestMethod]
    public async Task ReassignRecordsIdentityChangeHistory()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var wrong = await works.CreateWorkAsync(WorkMediaType.Anime, "Wrong", null, CancellationToken.None);
        var right = await works.CreateWorkAsync(WorkMediaType.Anime, "Right", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(
            wrong.Id, WorkMediaType.Anime, "mal", "42", 0.4, "weak", false, false,
            MappingReviewState.NeedsReview, CancellationToken.None);

        await works.ReassignExternalIdentityAsync(
            WorkMediaType.Anime, "mal", "42", right.Id, "owner", "owner confirmed", CancellationToken.None);

        var history = await query.GetIdentityChangesAsync(right.Id, 10, CancellationToken.None);
        var reassign = history.Single(x => x.ChangeType == WorkIdentityChangeType.Reassign);
        Assert.AreEqual(right.Id, reassign.TargetWorkId);
        Assert.AreEqual(wrong.Id, reassign.SourceWorkId);
        Assert.AreEqual("mal", reassign.Provider);
        Assert.AreEqual("42", reassign.ExternalId);
    }

    [TestMethod]
    public async Task ManualFieldPinSurvivesAProviderRefresh()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);
        var work = await works.CreateWorkAsync(WorkMediaType.Anime, "Work", null, CancellationToken.None);

        Assert.IsTrue(await works.SetManualFieldOverrideAsync(work.Id, "title", CancellationToken.None));
        // A later provider refresh of the same field must be refused.
        Assert.IsFalse(await works.SetFieldProvenanceAsync(
            work.Id, "title", "anilist", "1", 1.0, false, "anilist", CancellationToken.None));

        var provenance = (await query.GetProvenanceAsync(work.Id, CancellationToken.None)).Single(p => p.FieldKey == "title");
        Assert.AreEqual(MetadataFieldSources.Owner, provenance.Source);
        Assert.IsTrue(provenance.IsManualOverride);
    }

    [TestMethod]
    public async Task DuplicateDetectionAcrossTheLibraryUsesSharedTitleAndMediaType()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var query = new WorkQueryService(db);

        var a = await works.CreateWorkAsync(WorkMediaType.Anime, "Steins;Gate", 2011, CancellationToken.None);
        var b = await works.CreateWorkAsync(WorkMediaType.Anime, "Steins Gate!", 2011, CancellationToken.None);
        await works.AddOrUpdateTitleAsync(a.Id, WorkTitleType.Primary, "und", "Steins;Gate", "local", true, CancellationToken.None);
        await works.AddOrUpdateTitleAsync(b.Id, WorkTitleType.Primary, "und", "Steins Gate!", "local", true, CancellationToken.None);

        // A manga that shares the folded title is a different media type — NOT a duplicate.
        var manga = await works.CreateWorkAsync(WorkMediaType.Manga, "Steins Gate", null, CancellationToken.None);
        await works.AddOrUpdateTitleAsync(manga.Id, WorkTitleType.Primary, "und", "Steins Gate", "local", true, CancellationToken.None);

        var suggestions = await query.FindDuplicateSuggestionsAsync(50, CancellationToken.None);

        var pair = suggestions.Single();
        CollectionAssert.AreEquivalent(
            new[] { a.Id, b.Id },
            new[] { pair.KeepWorkId, pair.MergeWorkId });
        Assert.AreEqual(WorkMediaType.Anime, pair.MediaType);
        Assert.IsTrue(pair.Score >= 0.6);
    }

    [TestMethod]
    public async Task TheConflictQueueListsOnlyIdentitiesFlaggedForReviewOrderedByWorkTitle()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var zeta = await works.CreateWorkAsync(WorkMediaType.Book, "Zeta", null, CancellationToken.None);
        var alpha = await works.CreateWorkAsync(WorkMediaType.Book, "Alpha", null, CancellationToken.None);
        var settled = await works.CreateWorkAsync(WorkMediaType.Book, "Settled", null, CancellationToken.None);
        await works.LinkExternalIdentityAsync(zeta.Id, WorkMediaType.Book, "books-catalog", "z", 0.5, "test", true, false, MappingReviewState.NeedsReview, CancellationToken.None);
        await works.LinkExternalIdentityAsync(alpha.Id, WorkMediaType.Book, "books-catalog", "a", 0.5, "test", true, false, MappingReviewState.NeedsReview, CancellationToken.None);
        await works.LinkExternalIdentityAsync(settled.Id, WorkMediaType.Book, "books-catalog", "s", 1.0, "test", true, false, MappingReviewState.Confirmed, CancellationToken.None);

        var conflicts = await new WorkQueryService(db).ListConflictIdentitiesAsync(50, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, conflicts.Select(conflict => conflict.WorkTitle).ToArray());
    }

    [TestMethod]
    public void DuplicateDetectorSuppressesAlreadyRelatedWorks()
    {
        var keep = Guid.NewGuid();
        var other = Guid.NewGuid();
        var titles = new Dictionary<Guid, string> { [keep] = "A", [other] = "A alt" };
        var probes = new List<WorkTitleProbe>
        {
            new(keep, WorkMediaType.Anime, 2020, "shared"),
            new(other, WorkMediaType.Anime, 2020, "shared")
        };

        var none = WorkDuplicateDetection.Suggest(
            titles, probes, new HashSet<(Guid, Guid)> { WorkDuplicateDetection.Key(keep, other) });
        Assert.AreEqual(0, none.Count, "Works already carrying a relation edge are not re-suggested.");

        var suggested = WorkDuplicateDetection.Suggest(titles, probes, new HashSet<(Guid, Guid)>());
        Assert.AreEqual(1, suggested.Count);
        Assert.IsTrue(suggested[0].Score > 0.6, "A shared year lifts the score above the shared-title base.");
    }
}
