using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.ReadingAcquisition;

namespace Jularr.Tests;

/// <summary>
/// The profile an owner picks while approving a request is the per-Work assignment of the Work the request stands for, so the first search after the approval
/// and every later one resolve it, and nothing is stored on the request itself.
/// </summary>
[TestClass]
public sealed class RequestProfileAssignmentTests
{
    private static MediaAcquisitionRegistry Registry() =>
        new([new AnimeAcquisitionRegistration(), new BookAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration()]);

    private static Task<AcquisitionRequest> PendingAsync(AppDbContext db, MediaAcquisitionKind kind, string provider, string externalId, string title) =>
        new AcquisitionAccessStore(db).CreateAsync(new AcquisitionRequestDraft(kind, provider, externalId, title, null, null), "owner", AcquisitionRequestStatus.Pending, null, CancellationToken.None);

    [TestMethod]
    [DataRow(MediaAcquisitionKind.Book)]
    [DataRow(MediaAcquisitionKind.LightNovel)]
    [DataRow(MediaAcquisitionKind.Manga)]
    public async Task TheChosenProfileBecomesTheProfileOfTheRequestsWorkEvenForARequestFromBeforeTheBinding(MediaAcquisitionKind kind)
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-approve-profile-");
        try
        {
            var registry = Registry();
            var profiles = new QualityProfileStore(directory, registry);
            await profiles.UpsertAsync(registry.DefaultProfileFor(kind) with { Id = "strict", Name = "Strict" });
            var old = await RequestWorkTestSupport.CreateRequestAsync(db, kind, bound: false);
            var assignment = new RequestProfileAssignment(profiles, RequestWorkTestSupport.Binder(db), new VideoRequestWorkResolver(db));

            var result = await assignment.AssignAsync(old, "STRICT", CancellationToken.None);
            var bound = (await new AcquisitionAccessStore(db).GetAsync(old.Id, CancellationToken.None))!;

            Assert.AreEqual(RequestProfileResult.Assigned, result);
            Assert.IsNotNull(bound.WorkId, "The request is bound to its Work so the profile has somewhere to go.");
            Assert.AreEqual("strict", (await profiles.ResolveAsync(kind, bound.WorkId)).Id, "Every search of the Work resolves the chosen profile.");
            Assert.AreNotEqual("strict", (await profiles.ResolveAsync(kind, Guid.NewGuid())).Id, "Another Work keeps the default.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task AMovieRequestUsesItsVideoWorkAndAnUnknownProfileOrAnAnimeRequestAssignsNothing()
    {
        await using var db = await MediaCoreTestSupport.CreateDbAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-approve-profile-");
        try
        {
            var registry = Registry();
            var profiles = new QualityProfileStore(directory, registry);
            await profiles.UpsertAsync(registry.DefaultProfileFor(MediaAcquisitionKind.Book) with { Id = "strict", Name = "Strict" });
            var works = new WorkService(db);
            var movie = await works.EnsureWorkByExternalIdentityAsync(WorkMediaType.Movie, "tmdb", "603", "The Matrix", 1999, CancellationToken.None);
            var request = await PendingAsync(db, MediaAcquisitionKind.Movie, "tmdb", "603", "The Matrix");
            var anime = await PendingAsync(db, MediaAcquisitionKind.Anime, "anilist", "1", "Cowboy Bebop");
            var withoutWork = await PendingAsync(db, MediaAcquisitionKind.Movie, "tmdb", "999", "Not materialized");
            var assignment = new RequestProfileAssignment(profiles, RequestWorkTestSupport.Binder(db), new VideoRequestWorkResolver(db));

            Assert.AreEqual(RequestProfileResult.UnknownProfile, await assignment.AssignAsync(request, "gone", CancellationToken.None));
            Assert.AreEqual(RequestProfileResult.Assigned, await assignment.AssignAsync(request, "strict", CancellationToken.None));
            Assert.AreEqual("strict", (await profiles.LoadAsync()).WorkAssignments[movie.Id.ToString("D")]);
            Assert.AreEqual(RequestProfileResult.NoWork, await assignment.AssignAsync(anime, "strict", CancellationToken.None), "Anime keeps its profile in the request options and its monitoring.");
            Assert.AreEqual(RequestProfileResult.NoWork, await assignment.AssignAsync(withoutWork, "strict", CancellationToken.None), "A title without a Work has nothing to assign to.");
            Assert.AreEqual(1, (await profiles.LoadAsync()).WorkAssignments.Count);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
