using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Learning.Courses;
using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Statistics;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Pages.Learn;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using KanaIndexModel = Jularr.Web.Pages.Kana.IndexModel;

namespace Jularr.Tests;

/// <summary>
/// Verifies #230/#232: Learning hub modules and module pages follow the
/// canonical resolver only. Off shows nothing, Language Tools shows no
/// cards/SRS, Study shows the modules, historic cards never re-open a module
/// and Kana needs a Japanese course.
/// </summary>
[TestClass]
public sealed class LearningHubGatingTests
{
    private const string Profile = "hub-user";

    [TestMethod]
    public async Task OffHidesEveryModuleEvenWithDueReviews()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddDueLearningTermAsync();

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);

        Assert.IsFalse(hub.LearningEnabled);
        Assert.AreEqual(LearningMode.Off, hub.Mode);
        Assert.IsFalse(hub.ShowAnyModule);
        Assert.IsFalse(hub.ShowMetrics);
        Assert.IsFalse(hub.ShowKana);
        Assert.IsFalse(hub.ScriptTrainerNeedsCourse);
        Assert.IsFalse(hub.LanguageToolsOnly);
        Assert.AreEqual(0, hub.DueReviews, "Off must not count due reviews.");
        Assert.AreEqual(0, hub.LearningTerms);
    }

    [TestMethod]
    public async Task LanguageToolsShowsNoCardsOrSrsModules()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.LanguageTools);
        await fixture.AddDueLearningTermAsync();

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);

        Assert.IsTrue(hub.LearningEnabled);
        Assert.AreEqual(LearningMode.LanguageTools, hub.Mode);
        Assert.IsFalse(hub.ShowReviews);
        Assert.IsFalse(hub.ShowVocabulary);
        Assert.IsFalse(hub.ShowSentences);
        Assert.IsFalse(hub.ShowKana);
        Assert.IsFalse(hub.ShowProgress);
        Assert.IsFalse(hub.ScriptTrainerNeedsCourse);
        Assert.IsTrue(hub.LanguageToolsOnly);
        Assert.AreEqual(0, hub.DueReviews);
    }

    [TestMethod]
    public async Task LanguageToolsKeepsLookupWithoutCardsOrSrs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.LanguageTools);

        var resolved = await new LearningConfigurationStore(fixture.Db).ResolveProfileAsync(
            Profile,
            CancellationToken.None);
        Assert.IsTrue(resolved.IsEnabled(LearningCapability.LanguageLookup));
        Assert.IsTrue(resolved.IsEnabled(LearningCapability.ReadingAids));
        Assert.IsTrue(resolved.IsEnabled(LearningCapability.Translation));
        Assert.IsFalse(resolved.IsEnabled(LearningCapability.Vocabulary));
        Assert.IsFalse(resolved.IsEnabled(LearningCapability.Reviews));

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);

        Assert.AreEqual(0, await fixture.Db.LearningCourses.CountAsync());
        Assert.AreEqual(0, await fixture.Db.LearningCards.CountAsync());
        Assert.AreEqual(0, await fixture.Db.LearningUnits.CountAsync());
    }

    [TestMethod]
    public async Task StudyShowsEveryModuleAndCountsDueReviews()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.Study);
        await fixture.AddDueLearningTermAsync();

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);

        Assert.IsTrue(hub.ShowReviews);
        Assert.IsTrue(hub.ShowVocabulary);
        Assert.IsTrue(hub.ShowSentences);
        Assert.IsTrue(hub.ShowKana, "The Japanese course unlocks the Kana trainer.");
        Assert.IsTrue(hub.ShowProgress);
        Assert.IsTrue(hub.ShowMetrics);
        Assert.IsFalse(hub.ScriptTrainerNeedsCourse);
        Assert.IsFalse(hub.LanguageToolsOnly);
        Assert.AreEqual(1, hub.DueReviews);
        Assert.AreEqual(1, hub.LearningTerms);
    }

    [TestMethod]
    public async Task KanaNeedsAnEnabledJapaneseCourse()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.Study);

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);
        Assert.IsFalse(hub.ShowKana, "No course: no Kana trainer.");
        Assert.IsTrue(hub.ScriptTrainerNeedsCourse);
        AssertHubRedirect(await fixture.Kana().OnGetAsync(null, 1, CancellationToken.None));

        var courses = new LearningCourseStore(fixture.Db);
        var german = await courses.CreateAsync(
            Profile,
            "de",
            "en",
            null,
            null,
            CancellationToken.None);
        hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);
        Assert.IsFalse(hub.ShowKana, "A course without a script trainer toolkit does not unlock Kana.");
        Assert.IsTrue(hub.ScriptTrainerNeedsCourse);

        var japanese = await courses.CreateAsync(
            Profile,
            "ja",
            "de",
            null,
            null,
            CancellationToken.None);
        hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);
        Assert.IsTrue(hub.ShowKana);
        Assert.IsFalse(hub.ScriptTrainerNeedsCourse);
        Assert.IsInstanceOfType<PageResult>(
            await fixture.Kana().OnGetAsync(null, 1, CancellationToken.None));

        await courses.UpdateAsync(
            Profile,
            japanese.Id,
            null,
            isEnabled: false,
            japanese.Options,
            CancellationToken.None);
        hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);
        Assert.IsFalse(hub.ShowKana, "A disabled Japanese course does not unlock Kana.");
        Assert.AreNotEqual(german.Id, japanese.Id);
    }

    [TestMethod]
    public async Task CustomVocabularyWithoutReviewsKeepsSavingButHidesSrs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.Custom);
        await fixture.SetCapabilityAsync(LearningCapability.Vocabulary, true);
        var (term, card) = await fixture.AddTrackedTermAsync(UserTermState.Known);

        var hub = fixture.Hub();
        await hub.OnGetAsync(CancellationToken.None);
        Assert.IsTrue(hub.ShowVocabulary);
        Assert.IsFalse(hub.ShowReviews);
        Assert.IsTrue(hub.ShowMetrics);

        var vocabulary = fixture.Vocabulary();
        var page = await vocabulary.OnGetAsync(null, null, null, CancellationToken.None);
        Assert.IsInstanceOfType<PageResult>(page);
        Assert.IsFalse(vocabulary.ShowReviewActions);
        Assert.IsFalse(vocabulary.Items.Single().Due);

        Assert.IsInstanceOfType<RedirectToPageResult>(
            await vocabulary.OnPostSaveAsync(card.CourseId, card.UnitId, CancellationToken.None));
        var saved = await fixture.WordCardAsync(term.Id);
        Assert.AreEqual(UserTermState.Saved, saved.State);
        Assert.IsNull(saved.NextReviewAt, "Saving must not schedule a review.");
        Assert.IsNull(saved.QueuePosition, "Saving must not queue the word for reviews.");

        Assert.IsInstanceOfType<ForbidResult>(
            await vocabulary.OnPostLearnAsync(card.CourseId, card.UnitId, CancellationToken.None),
            "Learning transition requires the Reviews capability.");
        Assert.IsInstanceOfType<ForbidResult>(
            await vocabulary.OnPostSuspendAsync(card.CourseId, card.UnitId, CancellationToken.None));
        Assert.AreEqual(UserTermState.Saved, (await fixture.WordCardAsync(term.Id)).State);
        Assert.AreEqual(
            0,
            await LearningQueries
                .DueCards(fixture.Db, Profile, DateTime.UtcNow.AddYears(1))
                .CountAsync());

        Assert.IsInstanceOfType<RedirectToPageResult>(
            await vocabulary.OnPostIgnoreAsync(card.CourseId, card.UnitId, CancellationToken.None));
        Assert.AreEqual(UserTermState.Ignored, (await fixture.WordCardAsync(term.Id)).State);
    }

    [TestMethod]
    public async Task StudyVocabularyLifecycleFollowsCanonicalTransitions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.Study);
        var (term, card) = await fixture.AddTrackedTermAsync(UserTermState.Known);
        var vocabulary = fixture.Vocabulary();

        await vocabulary.OnPostSaveAsync(card.CourseId, card.UnitId, CancellationToken.None);
        var saved = await fixture.WordCardAsync(term.Id);
        Assert.AreEqual(UserTermState.Saved, saved.State);
        Assert.IsNull(saved.NextReviewAt);

        await vocabulary.OnPostLearnAsync(card.CourseId, card.UnitId, CancellationToken.None);
        var learning = await fixture.WordCardAsync(term.Id);
        Assert.AreEqual(UserTermState.Learning, learning.State);
        Assert.IsNull(learning.NextReviewAt, "Queued, not due, until the daily limit admits it.");
        Assert.IsNotNull(learning.QueuePosition);

        await vocabulary.OnPostSuspendAsync(card.CourseId, card.UnitId, CancellationToken.None);
        var suspended = await fixture.WordCardAsync(term.Id);
        Assert.AreEqual(UserTermState.Suspended, suspended.State);
        Assert.IsNull(suspended.NextReviewAt);

        await vocabulary.OnPostLearnAsync(card.CourseId, card.UnitId, CancellationToken.None);
        Assert.AreEqual(
            UserTermState.Learning,
            (await fixture.WordCardAsync(term.Id)).State,
            "Resume moves a suspended word back into reviews.");

        await vocabulary.OnPostIgnoreAsync(card.CourseId, card.UnitId, CancellationToken.None);
        var ignored = await fixture.WordCardAsync(term.Id);
        Assert.AreEqual(UserTermState.Ignored, ignored.State);
        Assert.IsNull(ignored.QueuePosition);

        await vocabulary.OnPostKnownAsync(card.CourseId, card.UnitId, CancellationToken.None);
        Assert.AreEqual(UserTermState.Known, (await fixture.WordCardAsync(term.Id)).State);

        Assert.IsTrue(vocabulary.TempData["Status"] is string status && status.Contains(term.Canonical));
    }

    [TestMethod]
    public async Task ModulePagesRedirectToHubWhenLearningIsOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddDueLearningTermAsync();

        AssertHubRedirect(await fixture.Vocabulary().OnGetAsync(null, null, null, CancellationToken.None));
        AssertHubRedirect(await fixture.Review().OnGetAsync(CancellationToken.None));
        AssertHubRedirect(await fixture.Progress().OnGetAsync(CancellationToken.None));
        AssertHubRedirect(await fixture.Sentences().OnGetAsync(null, CancellationToken.None));
        AssertHubRedirect(await fixture.Kana().OnGetAsync(null, 1, CancellationToken.None));

        Assert.AreEqual(
            0,
            await fixture.Db.LearningUnits.CountAsync(x => x.Kind == LearningUnitKind.Script),
            "Kana must not seed its catalog when the trainer is off.");
    }

    [TestMethod]
    public async Task ModulePagesRedirectToHubInLanguageToolsMode()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.LanguageTools);
        var (_, card) = await fixture.AddDueLearningTermAsync();

        AssertHubRedirect(await fixture.Vocabulary().OnGetAsync(null, null, null, CancellationToken.None));
        AssertHubRedirect(await fixture.Review().OnGetAsync(CancellationToken.None));
        AssertHubRedirect(await fixture.Progress().OnGetAsync(CancellationToken.None));
        AssertHubRedirect(await fixture.Sentences().OnGetAsync(null, CancellationToken.None));
        AssertHubRedirect(await fixture.Kana().OnGetAsync(null, 1, CancellationToken.None));

        Assert.IsInstanceOfType<ForbidResult>(
            await fixture.Review().OnPostReviewAsync(card.Id, ReviewRating.Good, CancellationToken.None));
        Assert.IsInstanceOfType<ForbidResult>(
            await fixture.Vocabulary().OnPostSaveAsync(card.CourseId, card.UnitId, CancellationToken.None));
        Assert.AreEqual(
            UserTermState.Learning,
            (await fixture.Db.LearningCards.AsNoTracking().SingleAsync(x => x.Id == card.Id)).State,
            "Language Tools must not change card state.");
    }

    [TestMethod]
    public async Task StudyModulePagesRender()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(LearningMode.Study);
        await fixture.AddDueLearningTermAsync();

        Assert.IsInstanceOfType<PageResult>(
            await fixture.Vocabulary().OnGetAsync(null, null, null, CancellationToken.None));
        Assert.IsInstanceOfType<PageResult>(await fixture.Review().OnGetAsync(CancellationToken.None));
        Assert.IsInstanceOfType<PageResult>(await fixture.Progress().OnGetAsync(CancellationToken.None));
        Assert.IsInstanceOfType<PageResult>(await fixture.Sentences().OnGetAsync(null, CancellationToken.None));
        Assert.IsInstanceOfType<PageResult>(await fixture.Kana().OnGetAsync(null, 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task EpisodeScopeResolvesNoLearningSurfacesWhenOff()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new LearningConfigurationStore(fixture.Db);
        var context = new LearningScopeContext(
            LearningMediaType.Anime,
            WorkKey: "anime-1",
            ContentKey: "episode-1");

        var off = await store.ResolveAsync(Profile, context, CancellationToken.None);
        Assert.IsFalse(off.IsEnabled(LearningCapability.ContentMetrics));
        Assert.IsFalse(off.IsEnabled(LearningCapability.PreparationSuggestions));
        Assert.IsFalse(off.IsEnabled(LearningCapability.Vocabulary));
        Assert.IsFalse(off.IsEnabled(LearningCapability.PlayerTools));
        Assert.IsFalse(off.HasAnyVisibleLearning);

        await fixture.SetModeAsync(LearningMode.LanguageTools);
        var tools = await store.ResolveAsync(Profile, context, CancellationToken.None);
        Assert.IsTrue(tools.IsEnabled(LearningCapability.LanguageLookup));
        Assert.IsTrue(tools.IsEnabled(LearningCapability.PlayerTools));
        Assert.IsFalse(tools.IsEnabled(LearningCapability.Vocabulary));
        Assert.IsFalse(tools.IsEnabled(LearningCapability.Reviews));
        Assert.IsFalse(tools.IsEnabled(LearningCapability.ContentMetrics));
    }

    [TestMethod]
    public void EpisodePageConsumesResolverForLearningSurfaces()
    {
        // Read-only check of #277's gating: the Episode page resolves its scope
        // through the canonical store and wraps learning UI in those flags.
        var pages = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Library");
        var model = File.ReadAllText(Path.Combine(pages, "Episode.cshtml.cs"));
        var view = File.ReadAllText(Path.Combine(pages, "Episode.cshtml"));

        StringAssert.Contains(model, "new LearningConfigurationStore(db, instanceModules).ResolveAsync(");
        StringAssert.Contains(model, "LearningMediaType.Anime");
        StringAssert.Contains(model, "LearningSettings.IsEnabled(LearningCapability.ContentMetrics)");
        StringAssert.Contains(model, "LearningSettings.IsEnabled(LearningCapability.PreparationSuggestions)");
        StringAssert.Contains(model, "LearningSettings.IsEnabled(LearningCapability.Vocabulary)");
        StringAssert.Contains(model, "LearningSettings.IsEnabled(LearningCapability.PlayerTools)");

        StringAssert.Contains(view, "@if (Model.ShowContentMetrics)");
        StringAssert.Contains(view, "@if (Model.ShowPreparationSuggestions)");
        StringAssert.Contains(view, "@if (Model.ShowVocabularyTools)");
        Assert.IsFalse(
            view.Contains("href=\"/Learn", StringComparison.Ordinal),
            "Episode must not link into the Learning hub directly.");
    }

    [TestMethod]
    public void EpisodePageHostsTheSharedLanguageInspectorForItsScope()
    {
        // #233 player hook: Episode.cshtml renders the shared inspector partial
        // for its own anime/episode scope, using the same host factory as the
        // other surfaces (Books.Read, Learn.Sentences).
        var view = EpisodePlayerSource.Read(RepositoryRoot());

        StringAssert.Contains(view, "@using Jularr.Web.Features.Learning.LanguageAssistance");
        StringAssert.Contains(
            view,
            "<partial name=\"_LanguageInspector\" model='LanguageInspectorHost.ForAnimeEpisode(Model.AnimeId, Model.EpisodeId, \"ja\")' />");

        // It must load before episode-player.js so window.JularrLanguageInspector
        // already exists when the player wires its open/close/statechange handlers.
        var inspectorIndex = view.IndexOf("_LanguageInspector", StringComparison.Ordinal);
        var episodeScriptIndex = view.IndexOf("~/js/episode-player.js", StringComparison.Ordinal);
        Assert.IsTrue(inspectorIndex >= 0 && inspectorIndex < episodeScriptIndex);
    }

    [TestMethod]
    public async Task EpisodeScopeShowsTheInspectorOnlyWhenPlayerToolsResolvesOn()
    {
        // #233: the inspector partial guards itself with
        // LearningModuleResolver.ResolveAssistanceAsync(surface: Anime), which
        // must follow the same PlayerTools capability the Episode page already
        // uses for its own learning sheet (ShowPlayerTools).
        await using var fixture = await Fixture.CreateAsync();
        var context = new LearningScopeContext(
            LearningMediaType.Anime,
            WorkKey: "anime-1",
            ContentKey: "episode-1");
        var resolver = new LearningModuleResolver(fixture.Db);

        var off = await resolver.ResolveAssistanceAsync(
            Profile,
            context,
            LanguageSourceType.Anime,
            CancellationToken.None);
        Assert.IsFalse(off.ShowInspector, "Learning off must not render the inspector.");

        await fixture.SetModeAsync(LearningMode.LanguageTools);
        var tools = await resolver.ResolveAssistanceAsync(
            Profile,
            context,
            LanguageSourceType.Anime,
            CancellationToken.None);
        Assert.IsTrue(tools.Any, "Language Tools enables lookup.");
        Assert.IsTrue(tools.SurfaceTools, "Language Tools enables PlayerTools by default.");
        Assert.IsTrue(tools.ShowInspector);

        await fixture.SetCapabilityAsync(LearningCapability.PlayerTools, false);
        var playerToolsOff = await resolver.ResolveAssistanceAsync(
            Profile,
            context,
            LanguageSourceType.Anime,
            CancellationToken.None);
        Assert.IsTrue(playerToolsOff.Any, "Lookup is still on.");
        Assert.IsFalse(playerToolsOff.SurfaceTools);
        Assert.IsFalse(
            playerToolsOff.ShowInspector,
            "Without PlayerTools the player must not render the inspector, even with lookup on.");
    }

    [TestMethod]
    public void HubLinksOnlyRenderInsideTheirModuleFlags()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Learn", "Index.cshtml"));

        foreach (var (href, flag) in new[]
                 {
                     ("href=\"/Learn/Review\"", "Model.ShowReviews"),
                     ("href=\"/Learn/Vocabulary\"", "Model.ShowVocabulary"),
                     ("href=\"/Learn/Sentences\"", "Model.ShowSentences"),
                     ("href=\"/Learn/Kana\"", "Model.ShowKana"),
                     ("href=\"/Learn/Progress\"", "Model.ShowProgress")
                 })
        {
            var link = view.IndexOf(href, StringComparison.Ordinal);
            Assert.IsTrue(link > 0, $"{href} is part of the hub.");
            var guard = view.LastIndexOf("@if (", link, StringComparison.Ordinal);
            StringAssert.StartsWith(
                view[guard..].Replace("@if (", ""),
                flag,
                $"{href} must be wrapped in {flag}.");
            Assert.AreEqual(
                1,
                view.Split(href).Length - 1,
                $"{href} appears exactly once.");
        }
    }

    private static void AssertHubRedirect(IActionResult result)
    {
        var redirect = Assert.IsInstanceOfType<RedirectToPageResult>(result);
        Assert.AreEqual("/Learn/Index", redirect.PageName);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, AppDbContext db)
        {
            this.directory = directory;
            Db = db;
            Account = CreateAccount(Profile);
            Learning = new LearningService(db, new FsrsReviewScheduler(), Account);
        }

        public AppDbContext Db { get; }
        public CurrentAccountContext Account { get; }
        public LearningService Learning { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"jularr-learning-hub-gating-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(
                    $"Data Source={Path.Combine(directory, "jularr.db")};Foreign Keys=True")
                .Options;

            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(directory, db);
        }

        public IndexModel Hub() => new(Db, Account);

        public VocabularyModel Vocabulary() => WithTempData(new VocabularyModel(Db, Learning, Account));

        public ReviewModel Review() =>
            WithTempData(new ReviewModel(
                Db,
                Learning,
                new AiSentenceExplanationService(Db, new StubExplainer()),
                new NovelCatalogQueries(Db),
                Account));

        public ProgressModel Progress() =>
            new(Db, new LearningStatisticsService(Db), Account);

        public SentencesModel Sentences() =>
            new(
                Db,
                Account,
                new Jularr.Web.Features.Learning.LanguageAssistance.LanguageTextAnalyzer(
                    new EmptyMorphology(),
                    new JapaneseDictionary(directory)),
                new AiSentenceExplanationService(Db, new StubExplainer()));

        public KanaIndexModel Kana() => WithTempData(new KanaIndexModel(Db, Learning));

        public Task SetModeAsync(LearningMode mode) =>
            new LearningConfigurationStore(Db).SetModeAsync(
                Profile,
                LearningScopeRef.Profile,
                mode,
                CancellationToken.None);

        public Task SetCapabilityAsync(LearningCapability capability, bool? enabled) =>
            new LearningConfigurationStore(Db).SetCapabilityOverrideAsync(
                Profile,
                LearningScopeRef.Profile,
                capability,
                enabled,
                CancellationToken.None);

        public async Task<Term> AddTermAsync()
        {
            var term = new Term
            {
                Language = "ja",
                Canonical = "学ぶ",
                Reading = "まなぶ",
                Meaning = "learn"
            };
            Db.Terms.Add(term);
            await Db.SaveChangesAsync();
            return term;
        }

        public async Task<(Term Term, LearningCard Card)> AddTrackedTermAsync(
            UserTermState state,
            DateTime? nextReviewAt = null,
            DateTime? learningStartedAt = null)
        {
            var term = await AddTermAsync();
            var card = await LearningTestData.SeedTermCardAsync(
                Db,
                Profile,
                term,
                state,
                nextReviewAt,
                learningStartedAt);
            return (term, card);
        }

        public Task<(Term Term, LearningCard Card)> AddDueLearningTermAsync() =>
            AddTrackedTermAsync(
                UserTermState.Learning,
                nextReviewAt: DateTime.UtcNow.AddHours(-1),
                learningStartedAt: DateTime.UtcNow.AddDays(-2));

        public Task<LearningCard> WordCardAsync(Guid termId) =>
            (from card in Db.LearningCards.AsNoTracking()
             join unit in Db.LearningUnits.AsNoTracking() on card.UnitId equals unit.Id
             where card.ProfileId == Profile
                 && card.Mode == LearningCardMode.Recognition
                 && unit.TermId == termId
             select card)
            .SingleAsync();

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static T WithTempData<T>(T page) where T : PageModel
        {
            page.TempData = new TempDataDictionary(
                new DefaultHttpContext(),
                new StubTempDataProvider());
            return page;
        }

        private static CurrentAccountContext CreateAccount(string profileId)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, profileId)],
                        "test"))
            };

            return new CurrentAccountContext(
                new FixedHttpContextAccessor { HttpContext = httpContext });
        }
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class StubTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class EmptyMorphology : IJapaneseMorphology
    {
        public IReadOnlyList<JapaneseMorphToken> Analyze(string text) => [];
    }

    private sealed class StubExplainer : IAiSentenceExplainer
    {
        public string Id => "stub";

        public Task<AiSentenceExplanation> ExplainSentenceAsync(
            AiSentenceExplainRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiSentenceExplanation(request.Sentence, [], [], false));
    }
}
