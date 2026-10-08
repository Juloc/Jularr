using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// The Book and Light Novel library entries that exist without a Work get one only when their provider id says which (#432): the id binds an entry to the
/// Work it already identifies or creates the one Work it identifies, a title never does, and anything unclear is left for the owner instead of being
/// merged or moved. The same Work then serves the request, the import and the profile.
/// </summary>
[TestClass]
public sealed class LibraryWorkBackfillTests
{
    private const string BookProvider = "books-catalog";
    private const string BookId = "OL82563W";

    public static IEnumerable<object[]> Kinds() =>
    [
        [MediaAcquisitionKind.Book],
        [MediaAcquisitionKind.LightNovel]
    ];

    private static LibraryWorkBackfill Backfill(AppDbContext db)
    {
        var works = new WorkService(db);
        return new LibraryWorkBackfill(db, works, new LegacyWorkBridge(db, works, new WorkStructureService(db)), NullLogger<LibraryWorkBackfill>.Instance);
    }

    private static async Task<NovelWork> AddEntryAsync(AppDbContext db, MediaAcquisitionKind kind, string? title = null, string? sourceKey = null, string? provider = "", string? externalId = "")
    {
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var isBook = kind == MediaAcquisitionKind.Book;
        var entry = new NovelWork
        {
            SourceProvider = isBook ? BookCatalogService.ImportedBookProvider : "epub",
            SourceKey = sourceKey ?? $"{(isBook ? "book" : "series")}-{Guid.NewGuid():N}",
            Title = title ?? evidence.Title,
            Author = "An Author",
            MetadataProvider = provider == "" ? evidence.Provider : provider,
            MetadataExternalId = externalId == "" ? evidence.ExternalId : externalId
        };
        db.NovelWorks.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static Task<Guid?> WorkOfAsync(AppDbContext db, NovelWork entry) => RequestWorkTestSupport.WorkOfLegacyAsync(db, WorkSourceKind.NovelWork, entry.Id);

    private static Task<int> WorkCountAsync(AppDbContext db, MediaAcquisitionKind kind) => db.Set<Work>().CountAsync(work => work.MediaType == RequestWorkBinder.MediaTypeOf(kind));

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task AnEntryWithAProviderIdGetsTheOneWorkThatIdIdentifiesAndEveryLaterRequestImportAndProfileUsesIt(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var entry = await AddEntryAsync(db, kind);

        var result = await Backfill(db).RunAsync(CancellationToken.None);
        var workId = await WorkOfAsync(db, entry);
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var request = await RequestWorkTestSupport.CreateRequestAsync(db, kind, bound: true);

        Assert.AreEqual(1, result.Created);
        Assert.IsNotNull(workId);
        Assert.AreEqual(workId, request.WorkId, "The request for the same provider id resolves to the Work the library entry got.");
        Assert.AreEqual(1, await WorkCountAsync(db, kind), "Library entry first, request later: no second Work.");
        Assert.IsTrue(await db.Set<WorkExternalIdentity>().AnyAsync(identity => identity.WorkId == workId && identity.ExternalId == evidence.ExternalId));
        var target = await RequestWorkTestSupport.Binder(db).LegacyTargetAsync(request, WorkSourceKind.NovelWork, kind == MediaAcquisitionKind.Book ? BookCatalogService.ImportedBookProvider : null, CancellationToken.None);
        Assert.AreEqual(entry.Id, target, "The import goes into the library entry that carries the Work.");

        var directory = Directory.CreateTempSubdirectory("jularr-backfill-profile-");
        try
        {
            var registry = new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration(), new BookAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration()]);
            var profiles = new QualityProfileStore(directory, registry);
            await profiles.UpsertAsync(registry.DefaultProfileFor(kind) with { Id = "strict", Name = "Strict" });
            await profiles.AssignWorkAsync(workId!.Value, "strict");

            Assert.AreEqual("strict", (await profiles.ResolveAsync(kind, request.WorkId)).Id, "The profile assigned to the backfilled Work applies to the request before anything is imported.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task RunningAgainOrAfterARestartChangesNothing(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var entry = await AddEntryAsync(db, kind);

        var first = await Backfill(db).RunAsync(CancellationToken.None);
        var workId = await WorkOfAsync(db, entry);
        var afterRestart = await Backfill(db).RunAsync(CancellationToken.None);

        Assert.AreEqual(1, first.Created);
        Assert.AreEqual(0, afterRestart.Bound + afterRestart.Created + afterRestart.Review + afterRestart.Failed);
        Assert.AreEqual(workId, await WorkOfAsync(db, entry));
        Assert.AreEqual(1, await WorkCountAsync(db, kind));
        Assert.AreEqual(1, await db.WorkSourceLinks.CountAsync(link => link.SourceId == entry.Id));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task AnIdThatAWorkAlreadyIdentifiesBindsTheEntryToThatWorkWhateverTheTitle(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var old = await RequestWorkTestSupport.CreateRequestAsync(db, kind, bound: true);
        var entry = await AddEntryAsync(db, kind, title: "A localized or renamed title");

        var result = await Backfill(db).RunAsync(CancellationToken.None);

        Assert.AreEqual(1, result.Bound);
        Assert.AreEqual(old.WorkId, await WorkOfAsync(db, entry));
        Assert.AreEqual(1, await WorkCountAsync(db, kind), "The title differs and still no second Work appears.");
        Assert.AreEqual(evidence.ExternalId, (await db.WorkExternalIdentities.SingleAsync(identity => identity.WorkId == old.WorkId && identity.Provider == evidence.Provider)).ExternalId);
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task AnEntryWithoutAProviderIdIsNeverMatchedByItsTitleAndWaitsForTheOwner(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var evidence = RequestWorkTestSupport.Evidence(kind);
        await RequestWorkTestSupport.CreateRequestAsync(db, kind, bound: true);
        var entry = await AddEntryAsync(db, kind, title: evidence.Title, provider: null, externalId: null);

        var result = await Backfill(db).RunAsync(CancellationToken.None);
        var review = await Backfill(db).ListReviewAsync(50, CancellationToken.None);

        Assert.AreEqual(0, result.Bound + result.Created);
        Assert.IsNull(await WorkOfAsync(db, entry), "The same title as an existing Work is not evidence.");
        Assert.AreEqual(1, await WorkCountAsync(db, kind));
        var item = Assert.ContainsSingle(review);
        Assert.AreEqual(entry.Id, item.EntryId);
        Assert.AreEqual(LibraryWorkReviewReason.NoProviderIdentity, item.Reason);
    }

    [TestMethod]
    public async Task AnIdOfAnotherMediaTypeIsNoIdentityEitherAnAniListIdOnABookOrACatalogIdOnALightNovel()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var book = await AddEntryAsync(db, MediaAcquisitionKind.Book, provider: "anilist", externalId: "101");
        var novel = await AddEntryAsync(db, MediaAcquisitionKind.LightNovel, provider: BookProvider, externalId: BookId);

        var result = await Backfill(db).RunAsync(CancellationToken.None);
        var review = await Backfill(db).ListReviewAsync(50, CancellationToken.None);

        Assert.AreEqual(0, result.Bound + result.Created);
        Assert.AreEqual(2, review.Count);
        Assert.IsTrue(review.All(item => item.Reason == LibraryWorkReviewReason.NoProviderIdentity));
        Assert.AreEqual(0, await db.Set<Work>().CountAsync());
        Assert.AreEqual(WorkMediaType.Book, LibraryWorkBackfill.MediaTypeOf(book));
        Assert.AreEqual(WorkMediaType.LightNovel, LibraryWorkBackfill.MediaTypeOf(novel));
    }

    [TestMethod]
    public async Task IdsThatPointAtTwoWorksAreNotMergedOrChosenAndStayUnresolvedUntilTheOwnerDecides()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var first = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.LightNovel, "anilist", "101", "Re:Zero", null, CancellationToken.None);
        var second = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.LightNovel, "syosetu", "n1234ab", "Re:Zero (web)", null, CancellationToken.None);
        var entry = await AddEntryAsync(db, MediaAcquisitionKind.LightNovel, sourceKey: "n1234ab");
        entry.SourceProvider = "syosetu";
        await db.SaveChangesAsync();

        var result = await Backfill(db).RunAsync(CancellationToken.None);
        var item = Assert.ContainsSingle(await Backfill(db).ListReviewAsync(50, CancellationToken.None));

        Assert.AreEqual(1, result.Review);
        Assert.IsNull(await WorkOfAsync(db, entry));
        Assert.AreEqual(LibraryWorkReviewReason.ConflictingIdentities, item.Reason);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, item.Candidates.Select(candidate => candidate.WorkId).ToArray());
        Assert.AreEqual(2, await db.Set<Work>().CountAsync(), "Nothing was merged or created.");
        Assert.AreEqual(first.Id, (await db.WorkExternalIdentities.SingleAsync(identity => identity.ExternalId == "101")).WorkId, "No identity moved.");
    }

    [TestMethod]
    public async Task AWorkThatAlreadyStandsForAnotherEntryIsNotSharedAutomaticallyAndABoundEntryIsNeverMoved()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var kind = MediaAcquisitionKind.LightNovel;
        var bound = await AddEntryAsync(db, kind, provider: null, externalId: null);
        var boundWork = await works.CreateWorkAsync(WorkMediaType.LightNovel, "Hand-made", null, CancellationToken.None);
        await works.LinkSourceAsync(boundWork.Id, WorkSourceKind.NovelWork, bound.Id, CancellationToken.None);
        await works.LinkExternalIdentityAsync(boundWork.Id, WorkMediaType.LightNovel, "anilist", "101", 1.0, "test", true, false, MappingReviewState.Confirmed, CancellationToken.None);
        var other = await AddEntryAsync(db, kind, provider: "anilist", externalId: "101", sourceKey: "other-copy");

        var result = await Backfill(db).RunAsync(CancellationToken.None);

        Assert.AreEqual(1, result.Review);
        Assert.IsNull(await WorkOfAsync(db, other), "One Work, two entries: the owner decides.");
        Assert.AreEqual(boundWork.Id, await WorkOfAsync(db, bound), "An entry that has a Work is never touched by the pass.");
        Assert.AreEqual(LibraryWorkResolution.AlreadyBound, await Backfill(db).LinkToWorkAsync(bound.Id, (await works.CreateWorkAsync(WorkMediaType.LightNovel, "Other", null, CancellationToken.None)).Id, CancellationToken.None));
        Assert.AreEqual(boundWork.Id, await WorkOfAsync(db, bound), "Linking never reassigns an existing source link.");
    }

    [TestMethod]
    public async Task TheOwnerResolvesAReviewItemExplicitlyAndARetryAfterwardsChangesNothing()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var kind = MediaAcquisitionKind.LightNovel;
        var works = new WorkService(db);
        var target = await works.CreateWorkAsync(WorkMediaType.LightNovel, "Chosen", null, CancellationToken.None);
        var entry = await AddEntryAsync(db, kind, provider: null, externalId: null);
        var backfill = Backfill(db);
        Assert.AreEqual(LibraryWorkReviewReason.NoProviderIdentity, Assert.ContainsSingle(await backfill.ListReviewAsync(50, CancellationToken.None)).Reason);

        var linked = await backfill.LinkToWorkAsync(entry.Id, target.Id, CancellationToken.None);
        var again = await backfill.LinkToWorkAsync(entry.Id, target.Id, CancellationToken.None);
        var pass = await Backfill(db).RunAsync(CancellationToken.None);

        Assert.AreEqual(LibraryWorkResolution.Linked, linked);
        Assert.AreEqual(LibraryWorkResolution.AlreadyBound, again);
        Assert.AreEqual(target.Id, await WorkOfAsync(db, entry));
        Assert.AreEqual(0, pass.Bound + pass.Created + pass.Review);
        Assert.AreEqual(0, (await backfill.ListReviewAsync(50, CancellationToken.None)).Count, "A resolved entry leaves the review by itself.");
        Assert.AreEqual(1, await WorkCountAsync(db, kind));
    }

    [TestMethod]
    public async Task TheOwnerCanNotLinkAnEntryToAWorkWhenOneOfItsIdsBelongsToAnotherWorkOrOfAnotherMediaTypeOrThatAlreadyHasAnEntry()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var works = new WorkService(db);
        var holder = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.LightNovel, "anilist", "101", "Re:Zero", null, CancellationToken.None);
        var unrelated = await works.CreateWorkAsync(WorkMediaType.LightNovel, "Unrelated", null, CancellationToken.None);
        var book = await works.CreateWorkAsync(WorkMediaType.Book, "A book", null, CancellationToken.None);
        var entry = await AddEntryAsync(db, MediaAcquisitionKind.LightNovel);
        var taken = await AddEntryAsync(db, MediaAcquisitionKind.LightNovel, provider: null, externalId: null);
        await works.LinkSourceAsync(unrelated.Id, WorkSourceKind.NovelWork, taken.Id, CancellationToken.None);
        var backfill = Backfill(db);

        var heldElsewhere = await backfill.LinkToWorkAsync(entry.Id, unrelated.Id, CancellationToken.None);
        var wrongType = await backfill.LinkToWorkAsync(entry.Id, book.Id, CancellationToken.None);
        var representsAnother = await backfill.LinkToWorkAsync(entry.Id, unrelated.Id, CancellationToken.None);
        var create = await backfill.CreateWorkAsync(entry.Id, CancellationToken.None);

        Assert.AreEqual(LibraryWorkResolution.IdentityHeldByAnotherWork, heldElsewhere);
        Assert.AreEqual(LibraryWorkResolution.WrongMediaType, wrongType);
        Assert.AreEqual(LibraryWorkResolution.IdentityHeldByAnotherWork, representsAnother);
        Assert.AreEqual(LibraryWorkResolution.IdentityHeldByAnotherWork, create.Resolution, "A Work of its own is refused when the id already names one.");
        Assert.IsNull(await WorkOfAsync(db, entry));
        Assert.AreEqual(holder.Id, (await db.WorkExternalIdentities.SingleAsync(identity => identity.ExternalId == "101")).WorkId);
        Assert.AreEqual(LibraryWorkResolution.NotFound, await backfill.LinkToWorkAsync(Guid.NewGuid(), unrelated.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheOwnerCanGiveAnEntryWithoutAnIdAWorkOfItsOwnAndItThenBindsALaterRequestLazily()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var kind = MediaAcquisitionKind.LightNovel;
        var entry = await AddEntryAsync(db, kind, provider: null, externalId: null);

        var created = await Backfill(db).CreateWorkAsync(entry.Id, CancellationToken.None);
        var again = await Backfill(db).CreateWorkAsync(entry.Id, CancellationToken.None);

        Assert.AreEqual(LibraryWorkResolution.Created, created.Resolution);
        Assert.AreEqual(created.WorkId, await WorkOfAsync(db, entry));
        Assert.AreEqual(LibraryWorkResolution.AlreadyBound, again.Resolution);
        Assert.AreEqual(1, await WorkCountAsync(db, kind));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task AnOldUnboundRequestBindsLazilyToTheWorkOfItsLibraryEntryAndNothingElseOfTheRequestChanges(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var old = await RequestWorkTestSupport.CreateRequestAsync(db, kind, bound: false);
        var entry = await AddEntryAsync(db, kind);
        await Backfill(db).RunAsync(CancellationToken.None);

        var bound = await RequestWorkTestSupport.Binder(db).EnsureBoundAsync(old, CancellationToken.None);

        Assert.AreEqual(await WorkOfAsync(db, entry), bound.WorkId);
        Assert.AreEqual(old.ExternalId, bound.ExternalId);
        Assert.AreEqual(old.Title, bound.Title);
        Assert.AreEqual(1, await WorkCountAsync(db, kind));
    }

    [TestMethod]
    public async Task AVolumeRequestForAnExistingSeriesUsesTheSeriesWorkAndVolumesNeverBecomeWorksOfTheirOwn()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var kind = MediaAcquisitionKind.LightNovel;
        var entry = await AddEntryAsync(db, kind);
        await Backfill(db).RunAsync(CancellationToken.None);
        var seriesWork = await WorkOfAsync(db, entry);
        var volumeThree = new ReadingRequestPayload("Re:Zero", [], null, RequestedVolume: 3);

        var workId = await RequestWorkTestSupport.Binder(db).ResolveAsync(kind, "anilist", "101", "Re:Zero", CancellationToken.None);
        var request = await new AcquisitionAccessStore(db).CreateAsync(
            new AcquisitionRequestDraft(kind, "anilist", "101", "Re:Zero", null, null, JsonSerializer.Serialize(volumeThree, JsonSerializerOptions.Web)) { WorkId = workId },
            "owner",
            AcquisitionRequestStatus.Approved,
            "owner",
            CancellationToken.None);

        Assert.AreEqual(seriesWork, request.WorkId);
        Assert.AreEqual(1, await db.Set<Work>().CountAsync(), "The volume is structural intent of the request, not a Work.");
        StringAssert.Contains(request.PayloadJson, "\"requestedVolume\":3");
    }
}
