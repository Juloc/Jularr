using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover's genre chips and rows, and the vertical Explore feed that loads its titles in parts.
    [TestClass]
    public sealed class DiscoverFeedTests
    {
        [TestMethod]
        [Timeout(900_000)]
        public async Task Genres_AndTheExploreFeed_Show()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'anilist');
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute", "CompletedAt") VALUES ('openlibrary', 20, now());
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleasedOn", "ReleaseYear", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank", "CountryOfOrigin", "GenreIds")
                SELECT 20000 + kind.first + rank, kind.namespace, kind.type, kind.subtype, kind.name || ' ' || rank, current_date - (rank * 9), 2025 - rank / 8, 'https://127.0.0.1/' || rank || '.jpg',
                    1000 - rank, 7.5, 1000, rank, CASE WHEN rank % 2 = 0 THEN 'KR' ELSE 'JP' END,
                    ARRAY[((rank % 4) * 3 + 1)::smallint, ((rank % 3) * 3 + 3)::smallint, (14 + rank % 2)::smallint, 4::smallint]
                FROM (VALUES (0, 'tmdb_movie', 1, NULL::smallint, 'Film'), (100, 'anilist', 2, 1::smallint, 'Anime'), (300, 'openlibrary', 3, NULL::smallint, 'Book')) AS kind (first, namespace, type, subtype, name)
                    CROSS JOIN generate_series(1, 40) AS rank;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", "CatalogNamespace", "Id"::text FROM "Works" WHERE "Id" BETWEEN 20001 AND 20440;
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") SELECT "Id", 'Trailer', 'Way9Dexny3w', TRUE FROM "Works" WHERE "Id" BETWEEN 20001 AND 20010;
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") SELECT "Id", 'Overview', 'A long summary of this title that goes on for a while so the layout of the lower half can be judged: people meet, things happen, and the end is not what anyone expected at the start of it all.', TRUE FROM "Works" WHERE "Id" BETWEEN 20001 AND 20010;
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") SELECT "Id", 'Genres', 'Action, Drama, Thriller', TRUE FROM "Works" WHERE "Id" BETWEEN 20001 AND 20010;
                """);

            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync("/?mode=watch");
            await Assertions.Expect(page.Locator(".discover-genres__chip").First).ToHaveTextAsync("Action");
            await Assertions.Expect(page.Locator(".discover-genres__chip")).ToHaveCountAsync(8);
            await Assertions.Expect(page.Locator(".media-shelf__heading h2", new() { HasText = "Comedy" })).ToHaveCountAsync(1, new() { Timeout = 15000 });
            var all = await page.Locator(".media-shelf__heading a", new() { HasText = "See all" }).EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('href'))");
            Assert.IsTrue(all.Any(href => href.Contains("genre=Comedy", StringComparison.Ordinal)), string.Join(" ", all));

            await page.GotoAsync("/Discover/Feed?mode=watch");
            await Assertions.Expect(page.Locator("[data-feed-item]").First).ToBeVisibleAsync(new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator(".feed__item--skeleton")).ToHaveCountAsync(0);
            Assert.IsTrue(await page.Locator("[data-feed-item]").CountAsync() >= 2);
            await Assertions.Expect(page.Locator("[data-feed-item]").First.Locator(".feed__actions .button--primary")).ToBeVisibleAsync();
        }
    }
}
