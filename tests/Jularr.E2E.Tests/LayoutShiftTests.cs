using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Pages hold still while they load: what script shows or hides is decided before the first paint, a shelf keeps room for its arrows,
    // and an endless list adds its pages without moving what is above.
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

            // Enough films that the shelves of Discover outgrow their width and the Library loads further pages by itself.
            var works = string.Join(", ", Enumerable.Range(1, 60).Select(i => $"({9700 + i}, 'tmdb_movie', 1, 'Film {i}', '{2000 + i}-03-31', 'https://127.0.0.1/{i}.jpg', {100 - i}, 7.5, 1000, {i})"));
            var identities = string.Join(", ", Enumerable.Range(1, 60).Select(i => $"({9700 + i}, 'tmdb_movie', '{9700 + i}')"));
            await jularr.RunSqlAsync($"""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES {works};
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES {identities};
                """);
            await jularr.SeedLibraryFilesAsync();

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

                    // Scrolling down the Library loads its further pages; the cards above hold still and the row of the link moves unseen.
                    await page.GotoAsync("/Library");
                    for (var step = 0; step < 6; step++)
                    {
                        await page.Mouse.WheelAsync(0, 2500);
                        await page.WaitForTimeoutAsync(500);
                    }

                    await Assertions.Expect(page.Locator(".library-grid .media-card")).ToHaveCountAsync(60);
                    await Assertions.Expect(page.Locator("[data-infinite-status]")).ToHaveTextAsync("No more titles.");
                    var scrolled = await page.EvaluateAsync<JsonElement>(Measure);
                    if (scrolled.GetProperty("score").GetDouble() >= 0.01) shifts.Add($"{width}px /Library scrolled: {scrolled.GetProperty("score").GetDouble():0.0000} ({scrolled.GetProperty("sources").GetString()})");
                }

                // The device's choice of rows comes back with the page itself: it arrives as rows and holds still; an unknown skin is the default.
                await page.Locator("[data-layout-toggle]").ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-list-layout", "list");
                await browser.Context.AddCookiesAsync([new() { Name = "jularr-ui-skin", Value = "neon", Url = jularr.Address.ToString() }]);
                var response = await page.GotoAsync("/Library");
                var document = await response!.TextAsync();
                StringAssert.Contains(document, "data-list-layout=\"list\"");
                StringAssert.Contains(document, "data-skin=\"clean\"");
                var rows = await page.EvaluateAsync<JsonElement>(Measure);
                if (rows.GetProperty("score").GetDouble() >= 0.01) shifts.Add($"/Library as rows: {rows.GetProperty("score").GetDouble():0.0000} ({rows.GetProperty("sources").GetString()})");

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
