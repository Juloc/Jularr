using Jularr.Web.Features.Discovery;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Jularr.Web.Features.Ai;
using Jularr.Web.Infrastructure.Ai;
using AiAdminModel = Jularr.Web.Pages.Admin.AiModel;
using AiSettingsModel = Jularr.Web.Pages.Settings.AiModel;
using DiscoverIndexModel = Jularr.Web.Pages.IndexModel;
using DiscoverMangaImportModel = Jularr.Web.Pages.Discover.MangaImportModel;
using LibraryIndexModel = Jularr.Web.Pages.Library.IndexModel;
using MangaIndexModel = Jularr.Web.Pages.Manga.IndexModel;
using MangaReadModel = Jularr.Web.Pages.Manga.ReadModel;
using MangaSeriesModel = Jularr.Web.Pages.Manga.SeriesModel;

namespace Jularr.Tests;

/// <summary>
/// Guards the #186 rule that ordinary page GETs render from local state.
/// Every external client handed to a page throws if it is used, so a page GET
/// that starts contacting AniList (or any other remote service) fails here.
/// </summary>
[TestClass]
public sealed class LocalFirstPageGetTests
{
    [TestMethod]
    public async Task MangaReaderGetRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await fixture.ConnectAniListAsync();
        var (seriesId, chapterIds) = await fixture.AddMangaAsync(
            "Local Manga",
            aniListId: "321",
            chapters: 3);
        await fixture.SaveMangaProgressAsync(seriesId, chapterIds[1], pageIndex: 4);
        var page = fixture.Attach(new MangaReadModel(fixture.Db, fixture.OwnerAccount, new WorkQueryService(fixture.Db), fixture.WorkBridge()));

        var result = await page.OnGetAsync(chapterIds[1], null, CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.AreEqual(4, page.InitialPage);
        Assert.AreEqual(chapterIds[0], page.PreviousChapterId);
        Assert.AreEqual(chapterIds[2], page.NextChapterId);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public void MangaReaderHasNoRemoteProgressDependency()
    {
        // The reader used to preview AniList progress on every GET. Remote
        // state now lives only on the series page's lazy card.
        var dependencies = typeof(MangaReadModel)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        CollectionAssert.DoesNotContain(dependencies, typeof(AniListAccountService));
        CollectionAssert.DoesNotContain(dependencies, typeof(IHttpClientFactory));
        CollectionAssert.DoesNotContain(dependencies, typeof(HttpClient));
    }

    [TestMethod]
    public async Task MangaSeriesGetWithoutQueryRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await fixture.ConnectAniListAsync();
        var (seriesId, chapterIds) = await fixture.AddMangaAsync(
            "Local Manga",
            aniListId: "321",
            chapters: 2);
        await fixture.SaveMangaProgressAsync(seriesId, chapterIds[1], pageIndex: 9);
        var page = fixture.Attach(fixture.MangaSeriesPage());

        var result = await page.OnGetAsync(seriesId, null, CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.IsNotNull(page.ExternalProgress);
        Assert.AreEqual(0, page.SearchResults.Count);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task MangaSeriesSearchFailureStillRendersLocalPageAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        var (seriesId, _) = await fixture.AddMangaAsync(
            "Local Manga",
            aniListId: null,
            chapters: 1);
        var page = fixture.Attach(fixture.MangaSeriesPage());

        // An explicit search submit may contact AniList, but its failure must
        // not turn the local series page into an HTTP 500.
        var result = await page.OnGetAsync(seriesId, "Local Manga", CancellationToken.None);

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.AreEqual(1, fixture.Guard.Requests.Count);
        Assert.AreEqual(0, page.SearchResults.Count);
        Assert.IsNotNull(page.SearchError);
        Assert.IsNotNull(page.ExternalProgress);
    }

    [TestMethod]
    public async Task MangaLibraryGetRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await fixture.ConnectAniListAsync();
        var (seriesId, chapterIds) = await fixture.AddMangaAsync(
            "Local Manga",
            aniListId: "321",
            chapters: 2);
        await fixture.SaveMangaProgressAsync(seriesId, chapterIds[0], pageIndex: 1);
        var page = fixture.Attach(new MangaIndexModel(
            fixture.Db,
            fixture.OwnerAccount,
            fixture.Operations,
            fixture.HttpClientFactory,
            fixture.ReviewStore,
            NullLogger<MangaIndexModel>.Instance));

        await page.OnGetAsync(CancellationToken.None);

        Assert.AreEqual(1, page.Series.Count);
        Assert.AreEqual(1, page.ContinueReading.Count);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task DiscoverGetWithoutQueryRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await fixture.ConnectAniListAsync();
        var page = fixture.Attach(fixture.DiscoverPage());

        await page.LoadDiscoverAsync(CancellationToken.None);

        // Browse/search results come from the explicit, no-store Results
        // handler after first paint; the page GET itself stays local.
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task HomeGetWithRecommendationsRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await fixture.ConnectAniListAsync();
        await new LibraryCanonicalSeed(fixture.Db).AddAnimeAsync("Local Anime", [(1, 1, true), (1, 2, true)]);
        var page = fixture.Attach(fixture.HomePage());

        await page.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        // Hero, Continue, For you and recently discovered come from local state only.
        Assert.AreEqual("Local Anime", Assert.ContainsSingle(page.RecentTitles).Title.Title);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task HomeOfAManagerOnlyInstanceLinksNothingIntoThePlayer()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        var modules = new Jularr.Web.Features.Instance.InstanceModuleStore(fixture.Root);
        var anime = await new LibraryCanonicalSeed(fixture.Db).AddAnimeAsync("Local Anime", [(1, 1, true), (1, 2, true)]);
        await new VideoProgressService(fixture.Db).UpdateAsync("owner", MediaProgressTarget.Episode(anime.Work.Id, anime.Episodes[0].Canonical.Id), new MediaProgressUpdate(600_000, 1_400_000, false));
        var playing = fixture.Attach(fixture.HomePage(modules));
        await playing.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);
        Assert.AreEqual($"/Library/Episode/{anime.Episodes[0].Legacy.Id}", Assert.ContainsSingle(playing.ContinueWatching).PlayHref);

        await modules.SetAsync(Jularr.Web.Features.Instance.InstanceModule.Playback, false);
        var managerOnly = fixture.Attach(fixture.HomePage(modules));
        await managerOnly.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.AreEqual($"/Library/Anime/{anime.Anime.Id}", Assert.ContainsSingle(managerOnly.RecentTitles).Title.DetailHref);
        Assert.IsEmpty(managerOnly.ContinueWatching);
        Assert.IsEmpty(managerOnly.ContinueTiles);
        Assert.IsEmpty(managerOnly.PlaybackHistory);
        Assert.IsEmpty(managerOnly.Hero);
    }

    [TestMethod]
    public async Task DiscoverMangaImportGetRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        var page = fixture.Attach(new DiscoverMangaImportModel(
            fixture.Db,
            fixture.OwnerAccount,
            fixture.HttpClientFactory,
            fixture.Operations,
            NullLogger<DiscoverMangaImportModel>.Instance));

        var result = await page.OnGetAsync("321", "Remote Manga");

        Assert.IsInstanceOfType<PageResult>(result);
        Assert.AreEqual("321", page.AniListId);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task AnimeLibraryGetRendersWithoutExternalCallsAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        await new LibraryCanonicalSeed(fixture.Db).AddAnimeAsync("Local Anime", [(1, 1, true), (1, 2, true), (1, 3, true)]);
        var page = fixture.Attach(fixture.LibraryPage());
        page.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, Jularr.Web.Features.Auth.AccountRoles.Owner)], "test"));

        await page.OnGetAsync(CancellationToken.None);

        var card = Assert.ContainsSingle(page.Cards);
        Assert.AreEqual("Local Anime", card.Title);
        fixture.Guard.AssertNotCalled();
    }

    [TestMethod]
    public async Task AiSettingsAndAdminGetsRenderFromCachedStateAsync()
    {
        await using var fixture = await LocalFirstFixture.CreateAsync();
        var launcher = new GuardedCodexLauncher();
        var settingsStore = new AiProfileSettingsStore(
            new EphemeralDataProtectionProvider(),
            NullLogger<AiProfileSettingsStore>.Instance,
            new DirectoryInfo(fixture.Root));
        await settingsStore.SaveAsync(
            "owner",
            new AiProfileSettings(AiProviderIds.OpenAiCompatible, "https://api.example.invalid/v1", "model", "test-key", AiTranslationMode.Efficient),
            CancellationToken.None);
        var codex = new CodexCliProvider(new CodexAppServerGateway(
            new CodexAppServerClient(launcher, TimeProvider.System, NullLogger<CodexAppServerClient>.Instance),
            TimeProvider.System));
        var usage = new AiUsageTracker();
        var tracker = new AiActivityTracker(TimeProvider.System);
        var catalogs = new AiModelCatalogService(new AiModelCatalogStore(fixture.Db), TimeProvider.System);
        var router = new ProfileAiProviderRouter(
            fixture.OwnerAccount,
            settingsStore,
            usage,
            codex,
            fixture.HttpClientFactory,
            new AiActivityRunner(tracker, usage, TimeProvider.System),
            catalogs,
            new AiUsageStore(fixture.Db),
            TimeProvider.System);

        var settings = fixture.Attach(new AiSettingsModel(
            fixture.Db, fixture.OwnerAccount, settingsStore, router, new AiUsageStore(fixture.Db), tracker, usage, codex, TimeProvider.System));
        await settings.OnGetAsync(CancellationToken.None);
        var admin = fixture.Attach(new AiAdminModel(
            fixture.Db, codex, catalogs, new AiUsageStore(fixture.Db), tracker, TimeProvider.System));
        await admin.OnGetAsync(CancellationToken.None);

        Assert.AreEqual(AiProviderIds.OpenAiCompatible, settings.ProviderId);
        Assert.IsNull(admin.Diagnostics.Quota, "Quota is only read by the explicit refresh.");
        Assert.AreEqual(0, launcher.Starts, "A page GET must not start the Codex app-server.");
        fixture.Guard.AssertNotCalled();
    }

    private sealed class GuardedCodexLauncher : ICodexAppServerLauncher
    {
        public int Starts { get; private set; }

        public Task<ICodexAppServerTransport> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            throw new InvalidOperationException("Codex app-server started during a local-first page GET.");
        }
    }

    /// <summary>
    /// Stands in for every outbound HTTP dependency. Any request fails the
    /// calling code immediately and is recorded for the assertion.
    /// </summary>
    internal sealed class ExternalCallGuard : HttpMessageHandler
    {
        private readonly List<string> requests = [];

        public IReadOnlyList<string> Requests => requests;

        public HttpClient CreateClient() =>
            new(this, disposeHandler: false)
            {
                BaseAddress = new Uri("https://external.invalid/")
            };

        public void AssertNotCalled() =>
            Assert.AreEqual(
                0,
                requests.Count,
                "An ordinary page GET contacted an external service: " +
                string.Join(", ", requests));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            throw new InvalidOperationException(
                $"External call during a local-first page GET: {request.RequestUri}");
        }
    }

    private sealed class GuardedHttpClientFactory(ExternalCallGuard guard) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => guard.CreateClient();
    }

    private sealed class LocalFirstFixture : IAsyncDisposable
    {
        private const string OwnerProfileId = "owner";

        private readonly string root;
        private readonly AniListAccountStore accountStore;
        private readonly ReadingSegmentMappingStore segmentStore;

        private LocalFirstFixture(string root, AppDbContext db)
        {
            this.root = root;
            Db = db;
            Guard = new ExternalCallGuard();
            HttpClientFactory = new GuardedHttpClientFactory(Guard);
            OwnerAccount = CreateOwnerAccount();
            Operations = new OperationRunner(db, new ServiceCollection().BuildServiceProvider());
            accountStore = new AniListAccountStore(
                new EphemeralDataProtectionProvider(),
                NullLogger<AniListAccountStore>.Instance,
                new DirectoryInfo(root));
            var mappingDirectory = new DirectoryInfo(Path.Combine(root, "anilist"));
            ReviewStore = new MediaMappingReviewStore(
                NullLogger<MediaMappingReviewStore>.Instance,
                mappingDirectory);
            segmentStore = new ReadingSegmentMappingStore(
                NullLogger<ReadingSegmentMappingStore>.Instance,
                mappingDirectory);
        }

        public AppDbContext Db { get; }
        public string Root => root;
        public ExternalCallGuard Guard { get; }
        public IHttpClientFactory HttpClientFactory { get; }
        public CurrentAccountContext OwnerAccount { get; }
        public OperationRunner Operations { get; }
        public MediaMappingReviewStore ReviewStore { get; }

        public static async Task<LocalFirstFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "jularr-tests",
                $"local-first-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "anilist"));
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "jularr.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new LocalFirstFixture(root, db);
        }

        /// <summary>
        /// A connected account means every AniList-aware code path would try
        /// to reach AniList if it were asked for remote state.
        /// </summary>
        public Task ConnectAniListAsync() =>
            accountStore.SaveAsync(
                OwnerProfileId,
                new StoredAniListAccount(
                    12345,
                    42,
                    "viewer-42",
                    null,
                    "owner-token",
                    DateTimeOffset.UtcNow,
                    null),
                CancellationToken.None);

        public AniListAccountService AniListAccount() =>
            new(
                Guard.CreateClient(),
                accountStore,
                Db,
                new AnimeMetadataService(Db, [], accountStore, ReviewStore),
                segmentStore,
                ReviewStore,
                OwnerAccount,
                NullLogger<AniListAccountService>.Instance);

        public LegacyWorkBridge WorkBridge() =>
            new(Db, new WorkService(Db), new WorkStructureService(Db));

        public MangaSeriesModel MangaSeriesPage() =>
            new(
                Db,
                OwnerAccount,
                HttpClientFactory,
                Operations,
                ReviewStore,
                AniListAccount(),
                new FranchiseStore(Db),
                CreateFranchiseService());

        // This page only reads existing franchise membership (#425); nothing here exercises the
        // AniList-backed refresh, so the source and limiter are unused placeholders.
        private FranchiseService CreateFranchiseService() =>
            new(
                new FranchiseStore(Db),
                new MediaRelationStore(Db),
                new NoopFranchiseRelationSource(),
                new AniListRequestLimiter(new AniListRateLimitGate(), TimeProvider.System),
                new FranchiseRefreshSignal(),
                NullLogger<FranchiseService>.Instance);

        /// <summary>Home with the real local recommendation service; its relation source is guarded.</summary>
        public Jularr.Web.Pages.IndexModel HomePage(Jularr.Web.Features.Instance.IInstanceModuleService? modules = null)
        {
            var animeProvider = new AniListMetadataProvider(
                Guard.CreateClient(),
                NullLogger<AniListMetadataProvider>.Instance);
            var readingProvider = new NovelAniListProvider(
                Guard.CreateClient(),
                NullLogger<NovelAniListProvider>.Instance);
            var watchlistStore = new WatchlistStore(Db);
            var franchiseService = new FranchiseService(
                new FranchiseStore(Db),
                new MediaRelationStore(Db),
                new AniListFranchiseRelationSource(animeProvider, readingProvider),
                new AniListRequestLimiter(new AniListRateLimitGate(), TimeProvider.System),
                new FranchiseRefreshSignal(),
                NullLogger<FranchiseService>.Instance);
            var recommendations = new Jularr.Web.Features.Recommendations.MediaRecommendationService(
                Db,
                watchlistStore,
                new WatchlistLibraryResolver(Db),
                franchiseService,
                new Jularr.Web.Features.Shell.AppShellService(
                    new MediaCapabilityService(new MediaCapabilityStore(root))));

            return EpisodeFlowFixture.Home(Db, OwnerAccount, recommendations, modules, new MediaCapabilityStore(root));
        }

        public LibraryIndexModel LibraryPage() => new(
            Db,
            OwnerAccount,
            new Jularr.Web.Features.Collections.CollectionService(
                new Jularr.Web.Features.Collections.CollectionStore(Db),
                new Jularr.Web.Features.Collections.CollectionFactsProvider(
                    Db,
                    new Jularr.Web.Features.MediaFacts.MediaFactsService(Db),
                    new FranchiseStore(Db)),
                Db,
                new Jularr.Web.Features.Shell.AppShellService(
                    new MediaCapabilityService(new MediaCapabilityStore(root)))),
            new Jularr.Web.Features.Shell.AppShellService(
                new MediaCapabilityService(new MediaCapabilityStore(root))),
            NullLogger<LibraryIndexModel>.Instance,
            new Jularr.Web.Features.Instance.InstanceModuleStore(root));

        public DiscoverIndexModel DiscoverPage()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:Translation:MemoryPath"] = Path.Combine(root, "translation-memory")
                })
                .Build();
            var readingProvider = new NovelAniListProvider(
                Guard.CreateClient(),
                NullLogger<NovelAniListProvider>.Instance);
            var novelMetadata = new NovelMetadataService(
                Db,
                [readingProvider],
                ReviewStore,
                segmentStore);

            var animeProvider = new AniListMetadataProvider(
                Guard.CreateClient(),
                NullLogger<AniListMetadataProvider>.Instance);
            var franchiseStore = new FranchiseStore(Db);
            var relationStore = new MediaRelationStore(Db);
            var watchlistStore = new WatchlistStore(Db);
            var franchiseService = new FranchiseService(
                franchiseStore,
                relationStore,
                new AniListFranchiseRelationSource(animeProvider, readingProvider),
                new Jularr.Web.Features.Calendar.AniListRequestLimiter(new AniListRateLimitGate(), TimeProvider.System),
                new FranchiseRefreshSignal(),
                NullLogger<FranchiseService>.Instance);

            var books = new BookCatalogService(
                Guard.CreateClient(),
                Db,
                new ThrowingBookTranslator(),
                configuration);
            var works = new Jularr.Web.Features.MediaCore.WorkService(Db);
            var structure = new Jularr.Web.Features.MediaCore.WorkStructureService(Db);
            var tmdbCredentials = TmdbTestSupport.Credentials(apiKey: null);
            var tmdbHealth = new Jularr.Web.Features.Providers.ProviderHealthTracker(TimeProvider.System);
            var tmdb = new Jularr.Web.Features.Discovery.TmdbDiscoveryProvider(
                Guard.CreateClient(),
                tmdbCredentials,
                new Jularr.Web.Features.Providers.ProviderExecutor(
                    new Jularr.Web.Features.Providers.ProviderRateLimiter(),
                    tmdbHealth,
                    TimeProvider.System,
                    NullLogger<Jularr.Web.Features.Providers.ProviderExecutor>.Instance),
                tmdbHealth,
                new Jularr.Web.Features.Providers.ProviderResponseCache(TimeProvider.System),
                works,
                structure,
                Db,
                new Jularr.Web.Features.MediaCore.LegacyWorkBridge(Db, works, structure),
                new Jularr.Web.Features.Metadata.WorkMetadataRefreshQueue(new Jularr.Web.Features.MediaCore.WorkMetadataStore(Db), new Jularr.Web.Features.Metadata.WorkMetadataRefreshSignal(), TimeProvider.System));
            var coordinator = new Jularr.Web.Features.Discovery.DiscoveryCoordinator(
                tmdbCredentials,
                AniListAccount(),
                Db,
                DiscoveryTestSupport.Flights(providers: [animeProvider, readingProvider, books, tmdb]),
                TimeProvider.System,
                NullLogger<Jularr.Web.Features.Discovery.DiscoveryCoordinator>.Instance);
            var shellService = new Jularr.Web.Features.Shell.AppShellService(
                new MediaCapabilityService(new MediaCapabilityStore(root)));
            var shelves = new Jularr.Web.Features.Discovery.DiscoveryShelfService(
                coordinator,
                shellService);

            var recommendations = new Jularr.Web.Features.Recommendations.MediaRecommendationService(
                Db,
                watchlistStore,
                new Jularr.Web.Features.Watchlist.WatchlistLibraryResolver(Db),
                franchiseService,
                shellService);

            return new DiscoverIndexModel(
                coordinator,
                shelves,
                tmdb,
                Db,
                new NovelImportService(Db, [], novelMetadata),
                novelMetadata,
                OwnerAccount,
                Operations,
                AcquisitionAccessFixture.DefaultsService(
                    new AcquisitionAccessStore(Db),
                    OwnerAccount,
                    new RecordingEventPublisher()),
                new AcquisitionAccessStore(Db),
                new VideoRequestScopeResolver(Db),
                watchlistStore,
                franchiseService,
                recommendations,
                new Jularr.Web.Features.Instance.InstanceModuleStore(root),
                null!, NullLogger<DiscoverIndexModel>.Instance);
        }

        public async Task<(Guid SeriesId, IReadOnlyList<Guid> ChapterIds)> AddMangaAsync(
            string title,
            string? aniListId,
            int chapters)
        {
            var repository = new MangaRepository(Db);
            var seriesId = Guid.NewGuid();
            var sourcePath = Path.Combine(root, "manga", seriesId.ToString("N"));
            await repository.UpsertSeriesAsync(
                seriesId,
                title,
                sourcePath,
                CancellationToken.None);

            var chapterIds = new List<Guid>();
            for (var number = 1; number <= chapters; number++)
            {
                var chapterId = Guid.NewGuid();
                chapterIds.Add(chapterId);
                await repository.UpsertChapterAsync(
                    new MangaChapterItem(
                        chapterId,
                        seriesId,
                        number,
                        null,
                        $"Chapter {number}",
                        10,
                        "folder",
                        DateTime.UtcNow),
                    Path.Combine(sourcePath, number.ToString()),
                    CancellationToken.None);
            }

            if (aniListId is not null)
            {
                await repository.UpdateMetadataAsync(
                    seriesId,
                    new MangaAniListCandidate(aniListId, title, null, null, null, null, null),
                    CancellationToken.None);
            }

            return (seriesId, chapterIds);
        }

        public async Task SaveMangaProgressAsync(Guid seriesId, Guid chapterId, int pageIndex)
        {
            var repository = new MangaRepository(Db);
            var chapter = await repository.GetChapterAsync(chapterId, CancellationToken.None);
            Assert.IsNotNull(chapter);
            Assert.AreEqual(seriesId, chapter.SeriesId);
            await repository.SaveProgressAsync(
                OwnerProfileId,
                chapter,
                pageIndex,
                CancellationToken.None);
        }

        public async Task AddAnimeAsync(string title, int episodes)
        {
            var anime = new Anime { Key = Guid.NewGuid().ToString("N"), Title = title };
            Db.Add(anime);
            for (var number = 1; number <= episodes; number++)
            {
                Db.Add(new Episode
                {
                    AnimeId = anime.Id,
                    SeasonNumber = 1,
                    Number = number,
                    Title = $"Episode {number}"
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public TPage Attach<TPage>(TPage page)
            where TPage : PageModel
        {
            var services = new ServiceCollection()
                .AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>()
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            page.PageContext = new PageContext
            {
                HttpContext = httpContext,
                ViewData = new ViewDataDictionary<TPage>(
                    new EmptyModelMetadataProvider(),
                    new ModelStateDictionary())
            };
            page.TempData = new TempDataDictionary(httpContext, new NoTempDataProvider());
            return page;
        }

        private static CurrentAccountContext CreateOwnerAccount()
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, OwnerProfileId),
                            new Claim(ClaimTypes.Role, AccountRoles.Owner)
                        ],
                        "test"))
            };

            return new CurrentAccountContext(
                new FixedHttpContextAccessor { HttpContext = httpContext });
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class ThrowingBookTranslator : IBookTranslator
    {
        public string Id => "local-first-guard";

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Translation must not run during a local-first page GET.");
    }

    private sealed class NoopFranchiseRelationSource : IFranchiseRelationSource
    {
        public Task<AniListRelatedMedia> GetRelatedAsync(WatchlistIdentity work, CancellationToken cancellationToken) =>
            Task.FromResult(new AniListRelatedMedia(null, []));
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
