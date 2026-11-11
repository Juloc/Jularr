using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover's recommendation shelves arrive a few at a time: the first ones with the page, the next ones while scrolling toward them, without moving what is shown.
    [TestClass]
    public sealed class DiscoverRecommendationsTests
    {
        // The layout shift the browser counted since the page began to load, without shifts right after an input.
        private const string Shift = """
            async () => {
                let score = 0; const sources = [];
                await new Promise(resolve => {
                    new PerformanceObserver(list => {
                        for (const entry of list.getEntries()) {
                            if (entry.hadRecentInput) continue;
                            score += entry.value;
                            for (const source of entry.sources) sources.push((source.node?.className || source.node?.nodeName || '?') + ' ' + Math.round(source.previousRect.y) + '>' + Math.round(source.currentRect.y)
                                + ' @' + Math.round(entry.startTime));
                        }
                    }).observe({ type: 'layout-shift', buffered: true });
                    setTimeout(resolve, 300);
                });
                return { score, sources: sources.slice(0, 6).join(' | ') };
            }
            """;

        [TestMethod]
        [Timeout(600_000)]
        public async Task Recommendations_LoadAFewShelvesAtATime_WhileScrolling_WithoutShifting()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            // The Anime and Manga mode: AniList's anime, manga and light novels read whole, forty titles each, of which the anime seasons stay empty; the series' provider
            // does not answer, which the shelves say once.
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'anilist');
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute", "CompletedAt") VALUES ('openlibrary', 20, now());
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank", "CountryOfOrigin")
                SELECT 20000 + kind.first + rank, kind.namespace, kind.type, kind.subtype, kind.name || ' ' || rank, current_date + (rank - 20), 'https://127.0.0.1/' || rank || '.jpg',
                    1000 - rank, 7.5, 1000, rank, CASE WHEN rank % 2 = 0 THEN 'KR' ELSE 'JP' END
                FROM (VALUES (0, 'tmdb_movie', 1, NULL::smallint, 'Film'), (100, 'anilist', 2, 1::smallint, 'Anime'), (200, 'anilist', 3, 3::smallint, 'Novel'),
                    (300, 'openlibrary', 3, NULL::smallint, 'Book'), (400, 'anilist', 3, 2::smallint, 'Manga')) AS kind (first, namespace, type, subtype, name)
                    CROSS JOIN generate_series(1, 40) AS rank;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", "CatalogNamespace", "Id"::text FROM "Works" WHERE "Id" BETWEEN 20001 AND 20440;
                """);

            try
            {
                // The page itself (asked once before, so the host has started up) and its first shelves: shelves only, never a whole document, and a link to the next ones.
                await page.APIRequest.GetAsync("/?mode=animanga");
                var watch = Stopwatch.StartNew();
                var document = await page.APIRequest.GetAsync("/?mode=animanga");
                var documentTime = watch.ElapsedMilliseconds;
                var fragmentHeaders = new Dictionary<string, string> { ["X-Jularr-Fragment"] = "recommendations" };
                await page.APIRequest.GetAsync("/?handler=Recommendations", new() { Headers = fragmentHeaders });
                watch.Restart();
                var fragment = await page.APIRequest.GetAsync("/?handler=Recommendations", new() { Headers = fragmentHeaders });
                var fragmentTime = watch.ElapsedMilliseconds;
                var shelves = await fragment.TextAsync();
                Evidence.Log($"[test] / {(await document.BodyAsync()).Length} bytes in {documentTime} ms ({document.Headers.GetValueOrDefault("server-timing")}); first recommendations "
                    + $"{shelves.Length} bytes, {Regex.Count(shelves, "class=\"media-card\"")} cards in {fragmentTime} ms ({fragment.Headers.GetValueOrDefault("server-timing")})");
                Assert.AreEqual(200, fragment.Status, shelves);
                StringAssert.DoesNotMatch(shelves, new Regex("<!DOCTYPE|<html|<head", RegexOptions.IgnoreCase));
                Assert.AreEqual(2, Regex.Count(shelves, "<section class=\"media-shelf\""), shelves);
                StringAssert.Contains(shelves, "/?p=2&amp;handler=Recommendations");

                // On the desktop the third part fails once and its link becomes the retry.
                var failing = true;
                await page.RouteAsync(url => url.Contains("p=3&handler=Recommendations", StringComparison.Ordinal), route => failing ? route.AbortAsync() : route.ContinueAsync());
                var retry = page.Locator("#discover-recommendations [data-discover-recommendations]", new() { HasText = "Try again" });
                foreach (var (width, height, name) in new[] { (1440, 900, "desktop"), (390, 844, "phone") })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync("/?mode=animanga");
                    var shown = page.Locator("#discover-recommendations .media-shelf");
                    await Assertions.Expect(shown).ToHaveCountAsync(2);
                    await Assertions.Expect(page.Locator("#discover-recommendations .discover-media-heading")).ToHaveTextAsync(["Anime"]);
                    await Evidence.SaveAsync(page, $"discover-recommendations-{name}-first");

                    // Scrolling down brings the next shelves until the last type's last list; each type is named once, above its first shelf.
                    for (var step = 0; step < 30 && await page.Locator("#discover-recommendations [data-discover-recommendations]").CountAsync() > 0; step++)
                    {
                        if (failing && await retry.IsVisibleAsync())
                        {
                            await Assertions.Expect(page.Locator(".discover-feedback")).ToHaveTextAsync("Results could not be loaded. Try again or open the next page.");
                            await Evidence.SaveAsync(page, "discover-recommendations-retry");
                            failing = false;
                            await retry.ClickAsync();
                            await Assertions.Expect(page.Locator(".discover-feedback")).ToHaveTextAsync("");
                            await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-discover-layout", "grid");
                        }

                        await page.Mouse.WheelAsync(0, 1200);
                        await page.WaitForTimeoutAsync(300);
                    }

                    Assert.IsFalse(failing, "The failed part offered its retry.");

                    await Assertions.Expect(page.Locator("#discover-recommendations [data-discover-recommendations]")).ToHaveCountAsync(0);
                    await Assertions.Expect(page.Locator("#discover-recommendations .discover-media-heading"))
                        .ToHaveTextAsync(["Anime", "Light novels", "Manga"]);
                    await Assertions.Expect(page.Locator("#discover-recommendations > [role=status]")).ToHaveCountAsync(0);
                    await Assertions.Expect(shown.Last.Locator("h2")).ToHaveTextAsync("Top 100");
                    await Assertions.Expect(shown.Last).ToContainTextAsync("Manga 1");

                    var shift = await page.EvaluateAsync<JsonElement>(Shift);
                    var score = shift.GetProperty("score").GetDouble();
                    Evidence.Log($"[test] {name}: {await shown.CountAsync()} shelves after scrolling, layout shift {score:0.0000} ({shift.GetProperty("sources").GetString()})");
                    Assert.IsTrue(score < 0.01, $"{name} shifted {score:0.0000} ({shift.GetProperty("sources").GetString()})");
                    await Evidence.SaveAsync(page, $"discover-recommendations-{name}-scrolled");
                }
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-recommendations-failed");
                throw;
            }
        }
    }
}
