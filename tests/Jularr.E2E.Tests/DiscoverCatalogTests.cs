using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover from a catalog read whole: its shelves need no provider, typed words suggest its titles.
    [TestClass]
    public sealed class DiscoverCatalogTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task WholeCatalog_FillsTheShelvesAndSuggestsByTypeWhileTyping()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES
                    (9603, 'tmdb_movie', 1, 'The Matrix', '1999-03-31', 'https://127.0.0.1/603.jpg', 80, 8.7, 25000, 1),
                    (9604, 'tmdb_movie', 1, 'The Matrix Reloaded', '2003-05-15', 'https://127.0.0.1/604.jpg', 60, 7.0, 10000, 2);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (9603, 'tmdb_movie', '603'), (9604, 'tmdb_movie', '604');
                """);

            // Without a TMDB key the film shelves still come from the catalog.
            await page.GotoAsync("/?type=movies");
            var trending = page.Locator(".media-shelf", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Trending", Exact = true }) });
            await Assertions.Expect(trending.GetByText("The Matrix Reloaded")).ToBeVisibleAsync();

            // On any page, typed words suggest the kept titles under the field, grouped by type, then all results; Escape closes them.
            await page.GotoAsync("/Library");
            var search = page.Locator(".app-header .search-box input[type=search]");
            await search.FillAsync("matr");
            var popup = page.Locator(".app-search__popup");
            var suggestions = popup.Locator(".app-search__hit");
            await Assertions.Expect(suggestions).ToHaveCountAsync(2);
            await Assertions.Expect(popup.Locator(".app-search__type")).ToHaveTextAsync(["Movies"]);
            await Assertions.Expect(suggestions.First).ToContainTextAsync("The Matrix");
            await Assertions.Expect(suggestions.First).ToHaveAttributeAsync("href", "/Details/9603");
            await Assertions.Expect(popup.Locator(".app-search__all")).ToHaveAttributeAsync("href", "/?q=matr");
            await Assertions.Expect(page.Locator(".app-header kbd")).ToHaveCountAsync(0);
            await Evidence.SaveAsync(page, "discover-suggestions");
            await search.PressAsync("Escape");
            await Assertions.Expect(popup).ToBeHiddenAsync();

            // On a phone the icon opens the field over the header, the suggestions fill the screen and the back button closes them.
            await page.SetViewportSizeAsync(390, 844);
            await page.Locator(".app-search__icon").ClickAsync();
            await search.FillAsync("matr");
            await Assertions.Expect(suggestions).ToHaveCountAsync(2);
            var box = (await popup.BoundingBoxAsync())!;
            Assert.AreEqual(390, box.Width, 1);
            Assert.IsTrue(box.Y + box.Height >= 843, "the suggestions reach the bottom of the screen");
            await Evidence.SaveAsync(page, "discover-suggestions-phone");
            await page.Locator("[data-search-close]").ClickAsync();
            await Assertions.Expect(popup).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("[data-search-close]")).ToBeHiddenAsync();
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task WholeCatalog_RankingGridAndShelfLoadMoreWhileScrolling_UntilTheyEnd()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ImageUrl", "TrendingRank")
                SELECT 9700 + rank, 'tmdb_movie', 1, 'Ranked ' || rank, 'https://127.0.0.1/' || rank || '.jpg', rank FROM generate_series(1, 45) AS rank;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT 9700 + rank, 'tmdb_movie', (9700 + rank)::text FROM generate_series(1, 45) AS rank;
                """);

            // The grid of a ranking adds its next page near its end, then says where the ranking ends.
            await page.GotoAsync("/?type=movies&view=Trending");
            var cards = page.Locator("#discover-results .media-grid .media-card");
            await Assertions.Expect(cards).ToHaveCountAsync(20);
            await Assertions.Expect(page.Locator("[data-discover-next]")).ToBeHiddenAsync();
            await ScrollUntilAsync(cards, () => page.EvaluateAsync("window.scrollTo(0, document.documentElement.scrollHeight)"));
            await Assertions.Expect(page.Locator(".discover-skeletons")).ToHaveCountAsync(0);
            await Assertions.Expect(cards).ToHaveCountAsync(45);
            await Assertions.Expect(page.Locator(".discover-paging .discover-end")).ToHaveTextAsync("No more titles.");
            await Assertions.Expect(page.Locator("[data-discover-next]")).ToHaveCountAsync(0);

            // Its shelf does the same as it is scrolled to its end.
            await page.GotoAsync("/?type=movies");
            var shelf = page.Locator(".media-shelf", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Trending", Exact = true }) });
            var shelfCards = shelf.Locator(".media-card");
            await Assertions.Expect(shelfCards).ToHaveCountAsync(20);
            await ScrollUntilAsync(shelfCards, () => shelf.Locator(".media-shelf__items").EvaluateAsync("items => items.scrollLeft = items.scrollWidth"));
            await Assertions.Expect(shelfCards).ToHaveCountAsync(45);
            await Assertions.Expect(shelf.Locator(".discover-end")).ToHaveTextAsync("No more titles.");
            await Evidence.SaveAsync(page, "discover-shelf-end");
        }

        // Scrolls to the end until no further card arrives.
        private static async Task ScrollUntilAsync(ILocator cards, Func<Task> scroll)
        {
            for (int count = -1, now = await cards.CountAsync(), round = 0; now != count && round < 10; round++)
            {
                count = now;
                await scroll();
                await Task.Delay(1500);
                now = await cards.CountAsync();
            }
        }
    }
}
