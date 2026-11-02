using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover's filter row on a catalog read whole: a filter turns the shelves into one grid under the highlights, More holds the rest.
    [TestClass]
    public sealed class DiscoverFilterTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task FilterRow_GenreAndMore_ShowAGridThatLoadsOn_AndClearingBringsTheShelvesBack()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'anilist';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "ImageUrl", "Popularity", "TrendingRank", "GenreIds",
                    "CatalogFormatId", "CountryOfOrigin")
                SELECT 9800 + rank, 'anilist', 2, 1, 'Robot ' || rank, 2020, 'https://127.0.0.1/' || rank || '.jpg', 1000 - rank, rank, '{10}', CASE WHEN rank % 5 = 0 THEN 5 ELSE 1 END,
                    CASE WHEN rank = 7 THEN 'KR' ELSE 'JP' END
                FROM generate_series(1, 25) AS rank;
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ImageUrl", "Popularity", "TrendingRank", "GenreIds", "CatalogFormatId")
                VALUES (9899, 'anilist', 2, 1, 'Quiet Days', 'https://127.0.0.1/99.jpg', 5000, 26, '{16}', 1);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'anilist', "Id"::text FROM "Works" WHERE "Id" BETWEEN 9801 AND 9899;
                """);

            // The type alone keeps its shelves; the row offers anime's filters.
            await page.GotoAsync("/?type=anime");
            var row = page.Locator("form[data-discover-filters]");
            await Assertions.Expect(row).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#discover-recommendations .media-shelf").First).ToBeVisibleAsync();
            await Evidence.SaveAsync(page, "discover-filter-row");

            // Choosing a genre in its popup applies it when the popup closes: one grid, popularity first, under the highlights.
            await row.Locator(".discover-filter__button").First.ClickAsync();
            var popup = page.Locator("#discover-filter-genre");
            await Assertions.Expect(popup).ToBeVisibleAsync();
            await popup.GetByText("Mecha", new() { Exact = true }).ClickAsync();
            await Evidence.SaveAsync(page, "discover-filter-genre-popup");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"type=anime&genre=Mecha$"));
            var cards = page.Locator("#discover-results .media-grid .media-card");
            await Assertions.Expect(cards).ToHaveCountAsync(20);
            await Assertions.Expect(cards.First).ToContainTextAsync("Robot 1");
            await Assertions.Expect(cards.First).ToContainTextAsync("TV");
            await Assertions.Expect(page.Locator(".media-grid").GetByText("Quiet Days")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".hero-carousel")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#discover-recommendations")).ToHaveCountAsync(0);
            await Evidence.SaveAsync(page, "discover-filter-grid");

            // The grid loads on while scrolling and says where it ends.
            for (var attempt = 0; attempt < 20 && await cards.CountAsync() < 25; attempt++)
            {
                await page.EvaluateAsync("window.scrollTo(0, document.documentElement.scrollHeight)");
                await page.WaitForTimeoutAsync(250);
            }
            await Assertions.Expect(cards).ToHaveCountAsync(25);

            // More holds the other filters; the format sits in the row.
            await page.GotoAsync("/?type=anime&genre=Mecha");
            await row.Locator(".discover-filter__more").ClickAsync();
            var drawer = page.Locator("#discover-panel");
            await drawer.GetByText("South Korea (Manhwa)").ClickAsync();
            await Evidence.SaveAsync(page, "discover-filter-more");
            await drawer.Locator("button[type=submit]").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("genre=Mecha&country=KR"));
            await Assertions.Expect(cards).ToHaveCountAsync(1);
            await Assertions.Expect(cards.First).ToContainTextAsync("Robot 7");

            // Clearing keeps the type and brings the shelves back.
            await row.Locator(".discover-filter__clear").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/\?type=anime$"));
            await Assertions.Expect(page.Locator("#discover-recommendations .media-shelf").First).ToBeVisibleAsync();
        }
    }
}
