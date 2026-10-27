using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class WorkSearchRunTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task AllFilteredAndSelected_SearchInTheBackground_AndCanBeCancelled()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Hold the existing queue through its supported control so cancellation is deterministic.
            await SetQueuePausedAsync(page, true);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                SELECT id, 1, 'Search target ' || id, 2020 FROM generate_series(601, 665) AS id;
                INSERT INTO "WantedItems" ("Id", "WorkId", "IsPaused", "NextSearchAt")
                SELECT id, id, id = 665, now() + interval '3 days' FROM generate_series(601, 665) AS id;
                """);
            await page.GotoAsync("/Admin/Works?type=1&q=Search%20target%2061");
            await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Search filtered", Exact = true }));
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToContainTextAsync("0 of 10 targets checked");
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToContainTextAsync("0 of 10 targets checked");
            await Steps.FollowAsync(page, page.Locator("[data-search-progress]").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }));
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToContainTextAsync("Cancelled");

            // Search All includes every page, even while the visible list is filtered.
            await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Search all", Exact = true }));
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToContainTextAsync("0 of 64 targets checked");
            var evidence = Path.Combine(JularrInstance.Repository, "TestResults");
            Directory.CreateDirectory(evidence);
            await page.SetViewportSizeAsync(1280, 900);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "search-all-desktop.png"), FullPage = false });
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.querySelector('.app-sidebar')?.getBoundingClientRect().right <= 1");
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"), "The search controls must fit a phone.");
            await Assertions.Expect(page.Locator("[data-search-progress]").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "search-all-mobile.png"), FullPage = false });
            await Steps.FollowAsync(page, page.Locator("[data-search-progress]").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }));

            await page.SetViewportSizeAsync(1280, 900);
            await SetQueuePausedAsync(page, false);
            await page.GotoAsync("/Admin/Works?type=1&q=Search%20target%2061");
            await page.Locator("input[name=workIds]").Nth(0).CheckAsync();
            await page.Locator("input[name=workIds]").Nth(1).CheckAsync();
            await Steps.FollowAsync(page, page.Locator("button[form=works-bulk][value=search]"));
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToContainTextAsync("2 of 2 targets checked", new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator("[data-search-progress]")).ToHaveAttributeAsync("data-running", "false");
            await page.GetByText("Search results", new() { Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page.Locator("[data-search-result-rows]")).ToContainTextAsync("Search target 610");
            await Assertions.Expect(page.Locator("[data-search-result-rows]")).ToContainTextAsync("Search target 611");
            await Assertions.Expect(page.Locator("[data-search-result-rows]")).Not.ToContainTextAsync("Waiting");
            CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
        }

        private static async Task SetQueuePausedAsync(IPage page, bool paused)
        {
            await page.GotoAsync("/Admin/Jobs");
            var row = page.Locator(".list-body .list-row").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Library search queue", Exact = true }) });
            await Steps.FollowAsync(page, row.GetByRole(AriaRole.Button, new() { Name = paused ? "Pause" : "Resume", Exact = true }));
        }
    }
}
