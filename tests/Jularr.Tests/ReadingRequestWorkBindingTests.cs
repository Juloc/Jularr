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
/// Book, Light Novel and Manga requests are bound to the canonical Work (#396, #432): one contract over the three kinds for how the Work is resolved,
/// that it is never duplicated, guessed or lost, and that the Work's profile is what every search resolves. Where the importer puts the files is
/// covered next to each importer's own tests.
/// </summary>
[TestClass]
public sealed class ReadingRequestWorkBindingTests
{
    public static IEnumerable<object[]> Kinds() =>
    [
        [MediaAcquisitionKind.Book],
        [MediaAcquisitionKind.LightNovel],
        [MediaAcquisitionKind.Manga]
    ];

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task TheSameEvidenceAlwaysResolvesTheSameWorkAndNeverCreatesASecondOne(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var binder = RequestWorkTestSupport.Binder(fixture.Db);

        var first = await binder.ResolveAsync(kind, evidence.Provider, evidence.ExternalId, evidence.Title, CancellationToken.None);
        var again = await RequestWorkTestSupport.Binder(fixture.Db).ResolveAsync(kind, evidence.Provider, evidence.ExternalId, "A renamed or localized title", CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.AreEqual(first, again, "A title is never the identity: the same provider id is the same Work whatever the title.");
        Assert.AreEqual(1, await fixture.Db.Set<Work>().CountAsync(work => work.MediaType == RequestWorkBinder.MediaTypeOf(kind)));
        Assert.AreEqual(1, await fixture.Db.Set<WorkExternalIdentity>().CountAsync(identity => identity.WorkId == first));
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task ARequestBindsOnceTheBindingSurvivesAReloadAndARequestFromBeforeTheBindingBindsLazily(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, kind, bound: false);
        Assert.IsNull(old.WorkId, "A request made before the binding has none.");

        var bound = await RequestWorkTestSupport.Binder(fixture.Db).EnsureBoundAsync(old, CancellationToken.None);
        var boundAgain = await RequestWorkTestSupport.Binder(fixture.Db).EnsureBoundAsync(old, CancellationToken.None);
        var reloaded = await new AcquisitionAccessStore(fixture.Db).GetAsync(old.Id, CancellationToken.None);

        Assert.IsNotNull(bound.WorkId);
        Assert.AreEqual(bound.WorkId, boundAgain.WorkId, "Binding twice (a retry, a restart) lands on the same Work.");
        Assert.AreEqual(bound.WorkId, reloaded!.WorkId, "The binding is stored with the request, not derived from its payload or title.");
        Assert.AreEqual(1, await fixture.Db.Set<Work>().CountAsync());
        Assert.IsFalse(await new AcquisitionAccessStore(fixture.Db).BindWorkAsync(old.Id, Guid.NewGuid(), CancellationToken.None), "A bound request keeps its Work.");
        Assert.AreEqual(bound.WorkId, (await new AcquisitionAccessStore(fixture.Db).GetAsync(old.Id, CancellationToken.None))!.WorkId);
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task EvidenceThatIsNotAStableIdentityBindsNothingAndTheRequestStaysUsable(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var transient = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, kind, bound: true, provider: "search-result", externalId: "page-3-row-2", title: "Dune");
        var malformed = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, kind, bound: true, provider: kind == MediaAcquisitionKind.Book ? "books-catalog" : "anilist", externalId: kind == MediaAcquisitionKind.Book ? " " : "not-a-number", title: "Other");

        var binder = RequestWorkTestSupport.Binder(fixture.Db);
        var retried = await binder.EnsureBoundAsync(transient, CancellationToken.None);

        Assert.IsNull(transient.WorkId);
        Assert.IsNull(malformed.WorkId);
        Assert.IsNull(retried.WorkId);
        Assert.AreEqual(0, await fixture.Db.Set<Work>().CountAsync(), "No Work is invented from a title or a transient id.");
        Assert.IsTrue(retried.IsOpen, "An unbound request keeps its lifecycle and falls back to the kind default profile.");
    }

    [TestMethod]
    [DataRow(MediaAcquisitionKind.Book)]
    [DataRow(MediaAcquisitionKind.LightNovel)]
    public async Task ALibraryEntryThatAlreadyHoldsTheIdBringsItsWorkAndEvidenceForTwoWorksIsNotMerged(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var works = new WorkService(fixture.Db);
        var holder = new NovelWork
        {
            SourceProvider = kind == MediaAcquisitionKind.Book ? BookCatalogService.ImportedBookProvider : "epub",
            SourceKey = "holder",
            Title = evidence.Title,
            MetadataProvider = evidence.Provider,
            MetadataExternalId = evidence.ExternalId
        };
        fixture.Db.NovelWorks.Add(holder);
        await fixture.Db.SaveChangesAsync();
        var holdersWork = await works.CreateWorkAsync(RequestWorkBinder.MediaTypeOf(kind), evidence.Title, null, CancellationToken.None);
        await works.LinkSourceAsync(holdersWork.Id, WorkSourceKind.NovelWork, holder.Id, CancellationToken.None);
        var binder = RequestWorkTestSupport.Binder(fixture.Db);

        var resolved = await binder.ResolveAsync(kind, evidence.Provider, evidence.ExternalId, evidence.Title, CancellationToken.None);

        Assert.AreEqual(holdersWork.Id, resolved, "The Work the library entry already belongs to is the request's Work; no second Work is created for the same book or series.");
        Assert.AreEqual(1, await fixture.Db.Set<Work>().CountAsync());
        Assert.IsTrue(await fixture.Db.Set<WorkExternalIdentity>().AnyAsync(identity => identity.WorkId == holdersWork.Id && identity.ExternalId == evidence.ExternalId), "The provider id now identifies that Work.");

        var other = await works.CreateWorkAsync(RequestWorkBinder.MediaTypeOf(kind), "Another work", null, CancellationToken.None);
        await fixture.Db.Set<WorkExternalIdentity>().Where(identity => identity.WorkId == holdersWork.Id).ExecuteDeleteAsync();
        await works.LinkExternalIdentityAsync(other.Id, RequestWorkBinder.MediaTypeOf(kind), evidence.Provider, evidence.ExternalId, 1.0, "test", true, false, MappingReviewState.Confirmed, CancellationToken.None);

        var ambiguous = await binder.ResolveAsync(kind, evidence.Provider, evidence.ExternalId, evidence.Title, CancellationToken.None);

        Assert.IsNull(ambiguous, "The id names one Work and the library entry that holds it belongs to another: the request is left unbound for review instead of guessing.");
        Assert.AreEqual(2, await fixture.Db.Set<Work>().CountAsync(), "Nothing was merged or created.");
    }

    [TestMethod]
    [DynamicData(nameof(Kinds))]
    public async Task TheWorksProfileOverrideWinsThenTheKindDefaultAndAReassignmentOrDefaultChangeAppliesToTheNextSearch(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-request-work-profile-");
        try
        {
            var registry = new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration(), new BookAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration()]);
            var profiles = new QualityProfileStore(directory, registry);
            var baseline = registry.DefaultProfileFor(kind);
            await profiles.UpsertAsync(baseline with { Id = "strict", Name = "Strict" });
            await profiles.UpsertAsync(baseline with { Id = "relaxed", Name = "Relaxed" });
            var request = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, kind, bound: true);

            var byKind = await profiles.ResolveAsync(kind, request.WorkId);
            await profiles.AssignWorkAsync(request.WorkId!.Value, "strict");
            var byOverride = await profiles.ResolveAsync(kind, request.WorkId);
            await profiles.AssignWorkAsync(request.WorkId.Value, "relaxed");
            var reassigned = await profiles.ResolveAsync(kind, request.WorkId);
            await profiles.SetKindDefaultAsync(kind, "strict");
            var stillOverridden = await profiles.ResolveAsync(kind, request.WorkId);
            await profiles.AssignWorkAsync(request.WorkId.Value, null);
            var afterRemoval = await profiles.ResolveAsync(kind, request.WorkId);
            var otherWork = await profiles.ResolveAsync(kind, Guid.NewGuid());

            Assert.AreEqual(baseline.Id, byKind.Id, "Without an override the media-kind default applies.");
            Assert.AreEqual("strict", byOverride.Id, "The Work's own profile wins.");
            Assert.AreEqual("relaxed", reassigned.Id, "A reassignment while the request is Wanted applies to the next evaluation.");
            Assert.AreEqual("relaxed", stillOverridden.Id, "A changed kind default does not override the Work's own choice.");
            Assert.AreEqual("strict", afterRemoval.Id, "Removing the override falls back to the current kind default.");
            Assert.AreEqual("strict", otherWork.Id, "Another Work follows the kind default.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ManualSearchAndAutomaticSearchResolveTheProfileOfTheSameWorkEvenForARequestFromBeforeTheBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("jularr-request-work-manual-");
        try
        {
            var registry = new MediaAcquisitionRegistry([new AnimeAcquisitionRegistration(), new BookAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration()]);
            var profiles = new QualityProfileStore(directory, registry);
            await profiles.UpsertAsync(registry.DefaultProfileFor(MediaAcquisitionKind.Manga) with { Id = "volumes", Name = "Volumes only" });
            var binder = RequestWorkTestSupport.Binder(fixture.Db);
            var old = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, MediaAcquisitionKind.Manga, bound: false);
            var workId = (await binder.EnsureBoundAsync(old, CancellationToken.None)).WorkId!.Value;
            await profiles.AssignWorkAsync(workId, "volumes");
            var manual = new ReadingManualSearchService(new AcquisitionAccessStore(fixture.Db), null!, null!, null!, profiles, TimeProvider.System, binder);
            var unboundOld = await RequestWorkTestSupport.CreateRequestAsync(fixture.Db, MediaAcquisitionKind.LightNovel, bound: false);

            var target = await manual.GetTargetAsync(old.Id, CancellationToken.None);
            var lazily = await manual.GetTargetAsync(unboundOld.Id, CancellationToken.None);

            Assert.AreEqual("Volumes only", target!.ProfileName, "Manual Search shows the profile of the request's Work, the one the automatic search resolves.");
            Assert.IsNotNull((await new AcquisitionAccessStore(fixture.Db).GetAsync(unboundOld.Id, CancellationToken.None))!.WorkId, "Opening Manual Search on an old request binds it.");
            Assert.IsNotNull(lazily);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void TheVolumeOrChapterAWorkRequestAsksForIsStructuralIntentSeparateFromTheWork()
    {
        var workId = Guid.NewGuid();
        var volume = new ReadingRequestPayload("Frieren", [], null, RequestedVolume: 8);
        var chapter = new ReadingRequestPayload("Frieren", [], null, RequestedChapterStart: 75, RequestedChapterEnd: 75);
        var request = MangaRequest(JsonSerializer.Serialize(volume, JsonSerializerOptions.Web), workId);

        var read = ReadingAcquisitionEngine.ReadPayload(request, ReadingAcquisitionEngine.FallbackTarget(request));
        var chapterRead = ReadingAcquisitionEngine.ReadPayload(request with { PayloadJson = JsonSerializer.Serialize(chapter, JsonSerializerOptions.Web) }, ReadingAcquisitionEngine.FallbackTarget(request));

        Assert.AreEqual(8, read.RequestedVolume);
        Assert.AreEqual(75, chapterRead.RequestedChapterStart);
        Assert.AreEqual(workId, request.WorkId, "The volume or chapter is in the payload; the Work is the series, so the profile of the series applies to every volume and chapter.");
    }

    [TestMethod]
    public void NoBookOrReadingSearchResolvesItsProfileWithoutTheRequestsWork()
    {
        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        var offenders = new[] { "Features/Books", "Features/ReadingAcquisition" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(web, folder), "*.cs", SearchOption.AllDirectories))
            // The Books add dialog searches a catalog before any request exists, so it has no Work yet.
            .Where(file => !file.EndsWith("BookSearchCoordinator.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("workId: null", StringComparison.Ordinal) && File.ReadAllText(file).Contains("ResolveAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        var guarded = new[] { "Features/Books/BookAcquisitionExecutor.cs", "Features/ReadingAcquisition/ReadingAcquisitionExecutors.cs", "Features/ReadingAcquisition/ReadingManualSearchService.cs", "Features/Books/BookManualSearchService.cs" };

        Assert.AreEqual(0, offenders.Length, "A reading search that resolves the profile with no Work ignores the Work override: " + string.Join(", ", offenders));
        foreach (var file in guarded)
        {
            StringAssert.Contains(File.ReadAllText(Path.Combine(web, file)), "RequestWorkBinder", $"{file} binds an old request to its Work before it uses it.");
        }
    }

    [TestMethod]
    [DataRow(MediaAcquisitionKind.Book)]
    [DataRow(MediaAcquisitionKind.LightNovel)]
    [DataRow(MediaAcquisitionKind.Manga)]
    public async Task ARequestTheServiceCreatesIsBoundOnceAndARepeatedRequestDoesNotCreateAnotherWork(MediaAcquisitionKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var empty = Path.Combine(Path.GetTempPath(), $"jularr-no-settings-{Guid.NewGuid():N}");
        var service = new AcquisitionRequestService(
            new AcquisitionAccessStore(fixture.Db),
            [],
            AcquisitionAccessFixture.Account("owner", Jularr.Web.Features.Auth.AccountRole.Owner),
            new Jularr.Web.Features.Auth.MediaCapabilityService(new Jularr.Web.Features.Auth.MediaCapabilityStore(empty)),
            new AcquisitionRequestSettingsStore(empty),
            new RecordingEventPublisher(),
            NullLogger<AcquisitionRequestService>.Instance,
            workBinder: RequestWorkTestSupport.Binder(fixture.Db));
        var evidence = RequestWorkTestSupport.Evidence(kind);
        var draft = new AcquisitionRequestDraft(kind, evidence.Provider, evidence.ExternalId, evidence.Title, null, null);

        var first = await service.SubmitWithOutcomeAsync(draft, CancellationToken.None);
        var second = await service.SubmitWithOutcomeAsync(draft with { Title = "A different spelling" }, CancellationToken.None);

        Assert.IsNotNull(first.Request.WorkId);
        Assert.IsTrue(second.AlreadyRequested);
        Assert.AreEqual(first.Request.Id, second.Request.Id);
        Assert.AreEqual(1, await fixture.Db.Set<Work>().CountAsync(), "Requesting the same title again never creates a second Work.");
    }

    [TestMethod]
    public async Task ALightNovelRequestsVolumesJoinTheSeriesOfItsWorkAndAnImportRetryChangesNothing()
    {
        await using var host = await LightNovelHost.CreateAsync();
        var request = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.LightNovel, bound: true, provider: "syosetu", externalId: "n1234ab", title: "Test Book");
        var download = host.Download("volume-1");

        var first = await host.ImportAsync(request, download);
        var retry = await host.ImportAsync(request, download);
        var next = await host.ImportAsync(request, host.Download("volume-2"));

        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, first.Disposition, first.Message);
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, retry.Disposition, retry.Message);
        Assert.AreEqual(CompletedDownloadImportDisposition.Completed, next.Disposition, next.Message);
        var series = await host.Db.NovelWorks.SingleAsync();
        Assert.AreEqual(request.WorkId, await RequestWorkTestSupport.WorkOfLegacyAsync(host.Db, WorkSourceKind.NovelWork, series.Id), "The series that received the volumes is the request's Work.");
        Assert.AreEqual(1, await host.Db.Set<Work>().CountAsync(), "A volume is structure under the one series Work, never a Work of its own.");
        StringAssert.EndsWith(first.ResultUrl, series.Id.ToString());
    }

    [TestMethod]
    public async Task ALightNovelImportIntoASeriesOfAnotherWorkIsAReviewNotASilentSwitch()
    {
        await using var host = await LightNovelHost.CreateAsync();
        var unbound = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.LightNovel, bound: false, provider: "manual", externalId: "x", title: "Test Book");
        await host.ImportAsync(unbound, host.Download("volume-1"));
        var existing = await host.Db.NovelWorks.SingleAsync();
        var works = new WorkService(host.Db);
        var other = await works.CreateWorkAsync(WorkMediaType.LightNovel, "Another series", null, CancellationToken.None);
        await works.LinkSourceAsync(other.Id, WorkSourceKind.NovelWork, existing.Id, CancellationToken.None);
        var request = await RequestWorkTestSupport.CreateRequestAsync(host.Db, MediaAcquisitionKind.LightNovel, bound: true, provider: "syosetu", externalId: "n1234ab", title: "Test Book");

        var result = await host.ImportAsync(request, host.Download("volume-2"));

        Assert.AreEqual(CompletedDownloadImportDisposition.NeedsReview, result.Disposition, result.Message);
        Assert.AreEqual(other.Id, await RequestWorkTestSupport.WorkOfLegacyAsync(host.Db, WorkSourceKind.NovelWork, existing.Id));
    }

    private static AcquisitionRequest MangaRequest(string payload, Guid workId)
    {
        var now = DateTime.UtcNow;
        return new AcquisitionRequest(Guid.NewGuid(), MediaAcquisitionKind.Manga, "anilist", "202", "Frieren", null, null, payload, "owner", AcquisitionRequestStatus.Approved, null, null, null, now, now, "owner", now, workId);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Jularr.sln was not found above the test binaries.");
    }

    private sealed class LightNovelHost : IAsyncDisposable
    {
        private readonly string root;
        private readonly LightNovelCompletedDownloadImportAdapter adapter;

        private LightNovelHost(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            var importer = new NovelEpubImportService(db, new NovelVolumeAssetStore(new DirectoryInfo(Path.Combine(root, "volumes"))), NullLogger<NovelEpubImportService>.Instance);
            adapter = new LightNovelCompletedDownloadImportAdapter(
                importer,
                null!,
                new AnimeImportSettingsStore(root),
                new FileSystemHardLinkCreator(),
                NullLogger<LightNovelCompletedDownloadImportAdapter>.Instance,
                routing: new Jularr.Web.Features.Storage.LibraryRootRoutingService(db),
                binder: RequestWorkTestSupport.Binder(db));
        }

        public AppDbContext Db { get; }

        public static async Task<LightNovelHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"jularr-ln-request-work-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(root, "app.db")};Foreign Keys=True").Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            await ReadingTestRoots.AssignAsync(db, MediaAcquisitionKind.LightNovel, Path.Combine(root, "library"), ImportMode.Copy);
            return new LightNovelHost(root, db);
        }

        public string Download(string name)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "downloads", name)).FullName;
            using var file = File.Create(Path.Combine(folder, name + ".epub"));
            BookCompletedDownloadImportTests.BuildTestEpub().CopyTo(file);
            return folder;
        }

        public Task<CompletedDownloadImportResult> ImportAsync(AcquisitionRequest request, string download) =>
            adapter.ImportAsync(new CompletedDownloadImportRequest(request, null, download), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, AppDbContext db)
        {
            this.directory = directory;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-request-work-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True").Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(directory, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
