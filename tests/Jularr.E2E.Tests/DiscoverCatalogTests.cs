using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover from a catalog read whole: its shelves need no provider, typed words suggest its titles.
    [TestClass]
    public sealed class DiscoverCatalogTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task WholeCatalog_FillsTheShelvesAndSuggestsWhileTyping()
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

            // Typed words suggest the kept titles under the field; Escape closes them.
            var search = page.Locator(".app-header .search-box input[type=search]");
            await search.FillAsync("matr");
            var suggestions = page.Locator(".discover-suggestion");
            await Assertions.Expect(suggestions).ToHaveCountAsync(2);
            await Assertions.Expect(suggestions.First).ToContainTextAsync("The Matrix");
            await Assertions.Expect(suggestions.First).ToHaveAttributeAsync("href", "/Details/9603");
            await Evidence.SaveAsync(page, "discover-suggestions");
            await search.PressAsync("Escape");
            await Assertions.Expect(page.Locator(".discover-suggestions")).ToBeHiddenAsync();
        }
    }
}
