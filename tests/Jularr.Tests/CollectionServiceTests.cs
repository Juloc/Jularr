using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaFacts;
using Jularr.Web.Features.Shell;
using Jularr.Web.Ui;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// Covers the database-backed collection surface (#427): manual vs smart membership, smart-collection
/// materialization and its invalidation on a rule change, and capability filtering of the rendered shelf.
/// </summary>
[TestClass]
public sealed class CollectionServiceTests
{
    private const string Profile = "reader-collections";

    [TestMethod]
    public async Task ManualCollectionHoldsExactlyTheWorksAddedByHand()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddWorkAsync(WorkMediaType.Anime, "Manual anime", 2020);
        var book = await fixture.AddWorkAsync(WorkMediaType.Book, "Manual book", 2019);

        var collection = await fixture.Service.CreateAsync(Profile, CollectionKind.Manual, "Favourites", null, null, default);
        Assert.IsTrue(await fixture.Service.AddManualItemAsync(Profile, collection.Id, anime, default));
        Assert.IsTrue(await fixture.Service.AddManualItemAsync(Profile, collection.Id, book, default));
        Assert.IsFalse(await fixture.Service.AddManualItemAsync(Profile, collection.Id, book, default), "A duplicate add is a no-op.");

        var view = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.IsNotNull(view);
        Assert.AreEqual(CollectionKind.Manual, view.Kind);
        Assert.AreEqual(2, view.Shelf.Cards.Count);

        await fixture.Service.RemoveItemAsync(Profile, collection.Id, book, default);
        var afterRemove = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.AreEqual(1, afterRemove!.Shelf.Cards.Count);
    }

    [TestMethod]
    public async Task SmartCollectionMaterializesRuleMembershipWithReasons()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddWorkAsync(WorkMediaType.Anime, "Anime one", 2022);
        await fixture.AddWorkAsync(WorkMediaType.Anime, "Anime two", 2018);
        await fixture.AddWorkAsync(WorkMediaType.Book, "A book", 2021);

        var rule = new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime");
        var collection = await fixture.Service.CreateAsync(Profile, CollectionKind.Smart, "All anime", null, rule, default);

        // First view auto-materializes (LastMaterializedAt was null).
        var view = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.IsNotNull(view);
        Assert.AreEqual(CollectionKind.Smart, view.Kind);
        Assert.AreEqual(2, view.Shelf.Cards.Count, "Only the two anime works match the rule.");
        Assert.IsNotNull(view.LastMaterializedAt);
        // Explainability: every matched item states why.
        Assert.IsTrue(view.Items.All(i => i.Reasons.Count > 0));
        Assert.IsTrue(view.Items.All(i => i.Reasons.Any(r => r.Contains("Media type"))));
    }

    [TestMethod]
    public async Task ChangingTheRuleInvalidatesAndReMaterializesMembership()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddWorkAsync(WorkMediaType.Anime, "An anime", 2022);
        await fixture.AddWorkAsync(WorkMediaType.Manga, "A manga", 2021);

        var collection = await fixture.Service.CreateAsync(Profile, CollectionKind.Smart, "Rule set",
            null, new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime"), default);

        var first = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.AreEqual(1, first!.Shelf.Cards.Count);
        Assert.AreEqual("An anime", first.Shelf.Cards[0].Title);

        // Repoint the rule at manga; the changed rule must invalidate the old materialization.
        await fixture.Service.UpdateAsync(Profile, collection.Id, "Rule set", null,
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "manga"), default);

        var second = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.AreEqual(1, second!.Shelf.Cards.Count);
        Assert.AreEqual("A manga", second.Shelf.Cards[0].Title, "The re-materialized membership reflects the new rule.");
    }

    [TestMethod]
    public async Task RenderedShelfIsFilteredToVisibleMediaTypes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anime = await fixture.AddWorkAsync(WorkMediaType.Anime, "Visible anime", 2020);
        var book = await fixture.AddWorkAsync(WorkMediaType.Book, "Hidden book", 2019);

        var collection = await fixture.Service.CreateAsync(Profile, CollectionKind.Manual, "Mixed", null, null, default);
        await fixture.Service.AddManualItemAsync(Profile, collection.Id, anime, default);
        await fixture.Service.AddManualItemAsync(Profile, collection.Id, book, default);

        // A profile that may browse only anime must not see the book card.
        fixture.SetVisible(WorkMediaType.Anime);
        var view = await fixture.Service.GetDetailAsync(fixture.Owner(), Profile, collection.Id, UiTextBundle.English, default);
        Assert.AreEqual(1, view!.Shelf.Cards.Count);
        Assert.AreEqual("Visible anime", view.Shelf.Cards[0].Title);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly ConfigurableShell shell = new();

        private Fixture(string directory, AppDbContext db)
        {
            this.directory = directory;
            Db = db;
            var facts = new CollectionFactsProvider(db, new MediaFactsService(db), new FranchiseStore(db));
            Service = new CollectionService(new CollectionStore(db), facts, db, shell);
        }

        public AppDbContext Db { get; }

        public CollectionService Service { get; }

        public ClaimsPrincipal Owner() => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Profile)], "test"));

        public void SetVisible(params WorkMediaType[] visible) => shell.SetVisible(visible);

        public async Task<long> AddWorkAsync(WorkMediaType mediaType, string title, int year)
        {
            var work = new Work { MediaType = mediaType, CanonicalTitle = title, Year = year };
            Db.Works.Add(work);
            await Db.SaveChangesAsync();
            return work.Id;
        }

        public static async Task<Fixture> CreateAsync()
        {
            CollectionService.InvalidateCache();
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-collections-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(directory, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private sealed class ConfigurableShell : IAppShellService
        {
            private WorkMediaType[] visible = [.. WorkMediaTypes.All];

            public void SetVisible(WorkMediaType[] types) => visible = types;

            public Task<ShellMediaAccess> GetMediaAccessAsync(ClaimsPrincipal? user, CancellationToken cancellationToken = default)
            {
                var capabilities = WorkMediaTypes.All.ToDictionary(
                    type => type,
                    type => visible.Contains(type) ? MediaCapability.Browse : MediaCapability.Hidden);
                return Task.FromResult(new ShellMediaAccess(new MediaCapabilityView(false, capabilities)));
            }
        }
    }
}
