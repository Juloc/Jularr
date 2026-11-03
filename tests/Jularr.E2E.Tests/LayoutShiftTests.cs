using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Pages hold still while they load: what script shows or hides is decided before the first paint, and a shelf keeps room for its arrows.
    [TestClass]
    public sealed class LayoutShiftTests
    {
        // The layout shift the browser counted since the page began to load (cumulative, without shifts right after an input), with its first sources.
        private const string Measure = """
            async () => {
                await new Promise(resolve => setTimeout(resolve, 2500));
                let score = 0; const sources = [];
                await new Promise(resolve => {
                    new PerformanceObserver(list => {
                        for (const entry of list.getEntries()) {
                            if (entry.hadRecentInput) continue;
                            score += entry.value;
                            for (const source of entry.sources) sources.push((source.node?.className || source.node?.nodeName || '?') + ' ' + Math.round(source.previousRect.height) + '>' + Math.round(source.currentRect.height));
                        }
                    }).observe({ type: 'layout-shift', buffered: true });
                    setTimeout(resolve, 300);
                });
                return { score, sources: sources.slice(0, 4).join(' | ') };
            }
            """;

        [TestMethod]
        [Timeout(600_000)]
        public async Task Pages_WhileLoading_ShiftLessThanAHundredth()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Enough films that the shelves of Discover outgrow their width and show their arrows.
            var works = string.Join(", ", Enumerable.Range(1, 12).Select(i => $"({9700 + i}, 'tmdb_movie', 1, 'Film {i}', '{2000 + i}-03-31', 'https://127.0.0.1/{i}.jpg', {100 - i}, 7.5, 1000, {i})"));
            var identities = string.Join(", ", Enumerable.Range(1, 12).Select(i => $"({9700 + i}, 'tmdb_movie', '{9700 + i}')"));
            await jularr.RunSqlAsync($"""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES {works};
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES {identities};
                """);

            // Shelves, choice selects, layout toggles and drawer buttons, the save pill's forms on cards and in lists.
            string[] paths = ["/", "/Library", "/Favorites", "/Notifications", "/Admin/Providers", "/Admin/Catalog", "/Admin/Downloads", "/Account/Sessions"];
            var shifts = new List<string>();
            try
            {
                foreach (var (width, height) in new[] { (1440, 900), (390, 844) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    foreach (var path in paths)
                    {
                        await page.GotoAsync(path);
                        var result = await page.EvaluateAsync<JsonElement>(Measure);
                        var score = result.GetProperty("score").GetDouble();
                        if (score >= 0.01) shifts.Add($"{width}px {path}: {score:0.0000} ({result.GetProperty("sources").GetString()})");
                    }
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), shifts.ToArray(), string.Join("; ", shifts));
            }
            catch
            {
                await Evidence.SaveAsync(page, "layout-shift-failed");
                throw;
            }
        }
    }
}
