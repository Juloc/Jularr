using Jularr.Web.Data;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Home;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>The Home/Discover layout (#877): one owner, instance defaults with per-profile overrides, and the order the Home planner and the media-type bar follow.</summary>
[TestClass]
public sealed class HomeLayoutTests
{
    private static readonly IReadOnlySet<WorkMediaType> AllTypes = HomeLayoutPolicy.DefaultOrder.ToHashSet();

    private static HomeLayoutPreference Preference(string order, string hidden = "", HomeLanding landing = HomeLanding.Home, bool prioritizeContinue = true) =>
        new(order.Split(',', StringSplitOptions.RemoveEmptyEntries), hidden.Split(',', StringSplitOptions.RemoveEmptyEntries), landing, prioritizeContinue);

    [TestMethod]
    public void TheBuiltInLayoutIsTheOrderHomeHadBeforeItWasConfigurable()
    {
        var layout = HomeLayoutPolicy.Resolve(HomeLayoutPreference.BuiltIn, AllTypes, false);

        CollectionAssert.AreEqual(new[] { WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie, WorkMediaType.LightNovel, WorkMediaType.Book, WorkMediaType.Manga }, layout.Order.ToArray());
        Assert.AreEqual(0, layout.Hidden.Count);
        Assert.IsTrue(layout.PrioritizeContinue);
        Assert.AreEqual(HomeLanding.Home, layout.Landing);
    }

    [TestMethod]
    public void AStoredOrderWinsAndAMediaTypeTheStoredListDoesNotKnowIsAppendedAtTheEnd()
    {
        var layout = HomeLayoutPolicy.Resolve(Preference("manga,anime"), AllTypes, true);

        CollectionAssert.AreEqual(
            new[] { WorkMediaType.Manga, WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie, WorkMediaType.LightNovel, WorkMediaType.Book },
            layout.Order.ToArray(),
            "The stored order is kept exactly; what it does not know follows in the built-in order.");
    }

    [TestMethod]
    public void AnExplicitlyHiddenTypeStaysHiddenAndAStaleIdentifierIsIgnoredWithoutAFailure()
    {
        var layout = HomeLayoutPolicy.Resolve(Preference("anime,gone,manga,anime", "manga,vanished"), AllTypes, true);

        Assert.IsTrue(layout.Hidden.SetEquals([WorkMediaType.Manga]));
        CollectionAssert.DoesNotContain(layout.Shown.ToArray(), WorkMediaType.Manga);
        Assert.AreEqual(1, layout.Order.Count(type => type == WorkMediaType.Anime), "A repeated identifier is one card.");
    }

    [TestMethod]
    public void ASwitchedOffMediaTypeIsNotInTheLayoutAndComesBackInItsOldPlaceWhenTheModuleReturns()
    {
        var stored = Preference("manga,anime,series", "series");
        var withoutManga = AllTypes.Where(type => type != WorkMediaType.Manga).ToHashSet();

        var off = HomeLayoutPolicy.Resolve(stored, withoutManga, true);
        var edited = HomeLayoutPolicy.FromEditor(stored, off.Order.Reverse().Select(WorkMediaTypes.ToStorage), off.Order.Where(type => type != WorkMediaType.Anime).Select(WorkMediaTypes.ToStorage), "home", true, withoutManga);
        var back = HomeLayoutPolicy.Resolve(edited, AllTypes, true);

        CollectionAssert.DoesNotContain(off.Order.ToArray(), WorkMediaType.Manga);
        Assert.AreEqual(WorkMediaType.Manga, back.Order[0], "The type the module switch removed keeps its slot, so editing without it never loses it.");
        Assert.IsTrue(edited.Hidden.Contains("anime"), "Editing hides what was left unchecked among the available types.");
        Assert.IsFalse(back.Hidden.Contains(WorkMediaType.Manga));
    }

    [TestMethod]
    public void AHiddenFlagOfAnUnavailableTypeSurvivesAnEditAndAPresetOnlyFillsTheEditor()
    {
        var stored = Preference("manga,anime,series,movie", "manga");
        var available = new HashSet<WorkMediaType> { WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie };

        var edited = HomeLayoutPolicy.FromEditor(stored, ["movie", "series", "anime"], ["movie", "series", "anime"], "library", false, available);
        var (order, hidden) = HomeLayoutPolicy.ApplyPreset([WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Book], available);

        CollectionAssert.AreEqual(new[] { "manga", "movie", "series", "anime" }, edited.MediaOrder.ToArray());
        CollectionAssert.AreEqual(new[] { "manga" }, edited.Hidden.ToArray());
        Assert.AreEqual(HomeLanding.Library, edited.Landing);
        Assert.IsFalse(edited.PrioritizeContinue);
        CollectionAssert.AreEqual(new[] { WorkMediaType.Movie, WorkMediaType.Series, WorkMediaType.Anime }, order.ToArray());
        Assert.IsTrue(hidden.SetEquals([WorkMediaType.Anime]), "A preset names the types it shows; every other available type is hidden, and a type that is not available is not offered.");
    }

    [TestMethod]
    public void OnlyPresetsThatNameAnAvailableMediaTypeAreOffered()
    {
        var booksOnly = new HashSet<WorkMediaType> { WorkMediaType.Book };

        var presets = HomeEditorModel.PresetsFor(booksOnly).Select(preset => preset.Id).ToArray();

        CollectionAssert.AreEquivalent(new[] { "balanced", "booksNovels", "everything" }, presets);
    }

    [TestMethod]
    public void TheHomePlannerGroupsTheMediaTypesInTheViewersOrderAndLeavesAHiddenTypeOut()
    {
        var plans = DiscoveryShelfComposer.Plan([.. AllTypes], [WorkMediaType.Manga, WorkMediaType.Anime]);
        var defaults = DiscoveryShelfComposer.Plan([.. AllTypes]);

        CollectionAssert.AreEqual(new[] { "trending-manga", "top-manga", "new-manga", "upcoming-manga", "trending-anime", "top-anime", "new-anime", "upcoming-anime" }, plans.Select(plan => plan.Id).ToArray());
        Assert.AreEqual("trending-anime", defaults[0].Id, "Without an order the built-in one applies.");
        Assert.IsFalse(DiscoveryShelfComposer.Plan([WorkMediaType.Anime], [WorkMediaType.Manga, WorkMediaType.Anime]).Any(plan => plan.MediaType == WorkMediaType.Manga), "Access still limits the types.");
    }

    [TestMethod]
    public void TheMediaTypeBarFollowsTheViewersOrderAndKeepsTheBrowsedTypeEvenWhenItIsHidden()
    {
        var bar = DiscoverScopes.TabsFor([WorkMediaType.Manga, WorkMediaType.Anime], DiscoveryCategory.Book).Select(tab => tab.Category).ToArray();
        var plain = DiscoverScopes.TabsFor([WorkMediaType.Manga, WorkMediaType.Anime], DiscoveryCategory.All).Select(tab => tab.Category).ToArray();

        CollectionAssert.AreEqual(new[] { DiscoveryCategory.All, DiscoveryCategory.Manga, DiscoveryCategory.Anime, DiscoveryCategory.Book }, bar);
        CollectionAssert.AreEqual(new[] { DiscoveryCategory.All, DiscoveryCategory.Manga, DiscoveryCategory.Anime }, plain);
    }

    [TestMethod]
    public async Task ANewProfileInheritsTheInstanceDefaultAndAnOverrideWinsWithoutTouchingTheDefault()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new HomeLayoutStore(fixture.Db);
        await store.SaveInstanceDefaultAsync(Preference("book,manga", "series", HomeLanding.Library, false), CancellationToken.None);

        var inherited = await store.ResolveAsync("alice", AllTypes, CancellationToken.None);
        await store.SaveProfileAsync("alice", Preference("movie,anime"), CancellationToken.None);
        var own = await store.ResolveAsync("alice", AllTypes, CancellationToken.None);
        var other = await store.ResolveAsync("bob", AllTypes, CancellationToken.None);
        var instance = await store.GetInstanceDefaultAsync(CancellationToken.None);

        Assert.AreEqual(WorkMediaType.Book, inherited.Order[0]);
        Assert.AreEqual(HomeLanding.Library, inherited.Landing);
        Assert.IsFalse(inherited.PrioritizeContinue);
        Assert.IsFalse(inherited.IsCustomized);
        Assert.AreEqual(HomeOnboardingState.Pending, inherited.Onboarding);
        Assert.AreEqual(WorkMediaType.Movie, own.Order[0]);
        Assert.IsTrue(own.IsCustomized);
        Assert.AreEqual(HomeOnboardingState.Completed, own.Onboarding);
        Assert.AreEqual(WorkMediaType.Book, other.Order[0], "Another profile still follows the instance default.");
        CollectionAssert.AreEqual(new[] { "book", "manga" }, instance.MediaOrder.ToArray(), "A profile's own layout never writes the instance default.");
    }

    [TestMethod]
    public async Task ChangingTheInstanceDefaultMovesProfilesThatNeverArrangedAnythingButNotThoseThatDid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new HomeLayoutStore(fixture.Db);
        await store.SkipOnboardingAsync("skipper", CancellationToken.None);
        await store.SaveProfileAsync("arranger", Preference("manga"), CancellationToken.None);

        await store.SaveInstanceDefaultAsync(Preference("book"), CancellationToken.None);

        Assert.AreEqual(WorkMediaType.Book, (await store.ResolveAsync("skipper", AllTypes, CancellationToken.None)).Order[0]);
        Assert.AreEqual(WorkMediaType.Manga, (await store.ResolveAsync("arranger", AllTypes, CancellationToken.None)).Order[0]);
    }

    [TestMethod]
    public async Task TheOnboardingIsOfferedOnceAndSkippingNeverOverwritesAnArrangement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new HomeLayoutStore(fixture.Db);

        Assert.AreEqual(HomeOnboardingState.Pending, (await store.GetProfileAsync("alice", CancellationToken.None)).Onboarding);
        await store.SkipOnboardingAsync("alice", CancellationToken.None);
        var skipped = await store.GetProfileAsync("alice", CancellationToken.None);
        await store.SaveProfileAsync("bob", Preference("manga"), CancellationToken.None);
        await store.SkipOnboardingAsync("bob", CancellationToken.None);
        var arranged = await store.GetProfileAsync("bob", CancellationToken.None);

        Assert.AreEqual(HomeOnboardingState.Skipped, skipped.Onboarding);
        Assert.IsNull(skipped.Override, "Skipping leaves the instance default in effect.");
        Assert.AreEqual(HomeOnboardingState.Completed, arranged.Onboarding);
        Assert.IsNotNull(arranged.Override);
    }

    [TestMethod]
    public async Task ResettingRemovesTheOverrideSoTheCurrentInstanceDefaultAppliesAgainAndOnboardingStaysDone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new HomeLayoutStore(fixture.Db);
        await store.SaveProfileAsync("alice", Preference("manga", "anime"), CancellationToken.None);
        await store.SaveInstanceDefaultAsync(Preference("book"), CancellationToken.None);

        await store.ResetProfileAsync("alice", CancellationToken.None);
        var layout = await store.ResolveAsync("alice", AllTypes, CancellationToken.None);

        Assert.IsFalse(layout.IsCustomized);
        Assert.AreEqual(WorkMediaType.Book, layout.Order[0]);
        Assert.AreEqual(0, layout.Hidden.Count);
        Assert.AreEqual(HomeOnboardingState.Completed, layout.Onboarding);
        Assert.AreEqual(HomeLanding.Home, await store.GetLandingAsync("alice", CancellationToken.None));
    }

    [TestMethod]
    public async Task TheDatabaseRefusesAHalfStoredOverrideAndAnUnknownLandingPage()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(() => fixture.Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ProfileHomeLayouts\" (\"ProfileId\", \"OnboardingState\", \"MediaOrder\", \"UpdatedAt\") VALUES ('x', 'completed', 'anime', now())"));
        await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(() => fixture.Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"InstanceHomeDefaults\" (\"Id\", \"MediaOrder\", \"LandingPage\", \"UpdatedAt\") VALUES (1, 'anime', 'elsewhere', now())"));
    }

    [TestMethod]
    public void OnlyTheHomeLayoutStoreTouchesTheLayoutTables()
    {
        var web = Path.Combine(RepositoryRoot(), "src", "Jularr.Web");
        var offenders = Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.Combine("Data", "Migrations")) && !file.Contains(Path.Combine("Features", "Home") + Path.DirectorySeparatorChar + "HomeLayoutStore"))
            .Where(file => File.ReadAllText(file).Contains("ProfileHomeLayouts", StringComparison.Ordinal) || File.ReadAllText(file).Contains("InstanceHomeDefaults", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.AreEqual(0, offenders.Length, "The Home layout has one owner: " + string.Join(", ", offenders));
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
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-home-layout-{Guid.NewGuid():N}");
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
