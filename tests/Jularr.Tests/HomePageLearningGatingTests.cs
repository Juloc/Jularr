using Jularr.Web.Features.Discovery;
using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Vocabulary;
using Jularr.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Verifies #230/#232 on Home: no coverage percentages and no learning queries unless the resolved
/// Anime scope opts in, and — per docs/mockups/home/SPEC.md ("no stat tiles") — no Learning widgets,
/// prompts or links on Home at all; Learning is its own navigation destination.
/// </summary>
[TestClass]
public sealed class HomePageLearningGatingTests
{
    private const string Profile = "home-user";

    [TestMethod]
    public async Task OffShowsNoCoverage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedEpisodeWithDueVocabularyAsync();

        var home = fixture.Home();
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsFalse(home.ShowContentMetrics);
        Assert.AreEqual(1, home.RecentTitles.Count);
        Assert.AreEqual(0, home.RecentTitles[0].TotalOccurrences, "Coverage must not be loaded.");
        Assert.AreEqual(0, home.RecentTitles[0].PreparationPercent);
    }

    [TestMethod]
    public async Task StudyKeepsHomeQuietUntilMetricsAreOptedIn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedEpisodeWithDueVocabularyAsync();
        await fixture.SetModeAsync(LearningMode.Study);

        var home = fixture.Home();
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsFalse(home.ShowContentMetrics, "ContentMetrics is opt-in even in Study.");
        Assert.AreEqual(0, home.RecentTitles[0].TotalOccurrences);
    }

    [TestMethod]
    public async Task OptedInMetricsResolveThroughTheHierarchy()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedEpisodeWithDueVocabularyAsync();
        await fixture.SetModeAsync(LearningMode.Study);
        var store = new LearningConfigurationStore(fixture.Db);
        // An opted-in HomeWidget no longer adds anything to Home (no stat tiles); only the
        // ContentMetrics capability changes what Home shows.
        await store.SetCapabilityOverrideAsync(
            Profile,
            LearningScopeRef.Profile,
            LearningCapability.HomeWidget,
            true,
            CancellationToken.None);
        await store.SetCapabilityOverrideAsync(
            Profile,
            LearningScopeRef.ForMedia(LearningMediaType.Anime),
            LearningCapability.ContentMetrics,
            true,
            CancellationToken.None);

        var home = fixture.Home();
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsTrue(home.ShowContentMetrics);
        Assert.AreEqual(3, home.RecentTitles[0].TotalOccurrences);
        Assert.AreEqual(2, home.RecentTitles[0].PreparedOccurrences);
        Assert.AreEqual(66, home.RecentTitles[0].PreparationPercent);
    }

    [TestMethod]
    public async Task LanguageToolsShowsNoCoverage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedEpisodeWithDueVocabularyAsync();
        await fixture.SetModeAsync(LearningMode.LanguageTools);

        var home = fixture.Home();
        await home.LoadHomeAsync(DiscoveryCategory.All, CancellationToken.None);

        Assert.IsFalse(home.ShowContentMetrics);
        Assert.AreEqual(0, home.RecentTitles[0].TotalOccurrences);
    }

    [TestMethod]
    public void HomeLeadsWithTheHeroAndContinueRowAndHasNoLearningWidgets()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Index.cshtml"));

        // Order (SPEC content hierarchy): hero → Continue; the discovery rows follow from the Body handler.
        var hero = view.IndexOf("data-home-hero", StringComparison.Ordinal);
        var continueRow = view.IndexOf("data-home-continue", StringComparison.Ordinal);
        Assert.IsTrue(hero > 0);
        Assert.IsTrue(hero < continueRow, "The hero leads Home.");

        // SPEC "Explicit exclusions": no stat tiles; Home carries no Learning links, due-review
        // prompts or widgets at all.
        Assert.AreEqual(0, CountOccurrences(view, "href=\"/Learn"), "Home has no Learning links.");
        Assert.IsFalse(view.Contains("metric-grid", StringComparison.Ordinal), "Home shows no stat grid.");
        Assert.IsFalse(view.Contains("_MetricCard", StringComparison.Ordinal), "Home shows no count cards.");
        Assert.IsFalse(view.Contains("data-home-learning-widget", StringComparison.Ordinal));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        private readonly string path;

        private Fixture(string path, AppDbContext db)
        {
            this.path = path;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"jularr-home-gating-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True")
                .Options;

            var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(path, db);
        }

        public IndexModel Home() => EpisodeFlowFixture.Home(Db, Account());

        public Task SetModeAsync(LearningMode mode) =>
            new LearningConfigurationStore(Db).SetModeAsync(
                Profile,
                LearningScopeRef.Profile,
                mode,
                CancellationToken.None);

        /// <summary>
        /// One episode with three term occurrences; two of them belong to a
        /// due Learning word so coverage resolves to 66% when metrics are on.
        /// </summary>
        public async Task SeedEpisodeWithDueVocabularyAsync()
        {
            var seeded = await new LibraryCanonicalSeed(Db).AddAnimeAsync("Test", [(1, 1, true)]);
            var episode = seeded.Episodes[0].Legacy;
            var known = new Term { Canonical = "見る", Reading = "みる", Meaning = "see" };
            var unknown = new Term { Canonical = "走る", Reading = "はしる", Meaning = "run" };

            Db.AddRange(
                known,
                unknown,
                new EpisodeTerm { EpisodeId = episode.Id, TermId = known.Id, Occurrences = 2 },
                new EpisodeTerm { EpisodeId = episode.Id, TermId = unknown.Id, Occurrences = 1 });
            await LearningTestData.SeedTermCardAsync(
                Db,
                Profile,
                known,
                UserTermState.Learning,
                nextReviewAt: DateTime.UtcNow.AddHours(-1),
                learningStartedAt: DateTime.UtcNow.AddDays(-1));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(path);
        }

        private static CurrentAccountContext Account()
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, Profile)],
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
}
