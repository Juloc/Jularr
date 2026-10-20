using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Jobs page: every background job of the running instance, paused and resumed by the Owner, run on request and told to wait while Jularr is used.
    [TestClass]
    public sealed class JobsTests
    {
        private static readonly string[] Jobs =
        [
            "Scans and backups", "Download tracking", "Wanted search", "Plex sync", "Usenet downloads", "AniList matching", "Provider covers", "Jellyfin and Emby sync", "Storage check", "Scan scheduling",
            "Automatic backup", "Housekeeping", "Seek thumbnails", "Subtitles", "Text recognition", "Catalog sync", "Resource sampling"
        ];

        [TestMethod]
        [Timeout(300_000)]
        public async Task OwnerSeesEveryJob_PausesResumesAndRunsOne_AndChangesWhetherItWaits()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Jobs");
                foreach (var job in Jobs)
                {
                    await Assertions.Expect(page.Locator("tbody tr", new() { HasText = job })).ToHaveCountAsync(1);
                }

                // Pausing shows at once and keeps the job from running; resuming undoes it.
                var trickplay = page.Locator("tbody tr", new() { HasText = "Seek thumbnails" });
                await Steps.FollowAsync(page, trickplay.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }));
                await Assertions.Expect(trickplay).ToContainTextAsync("Paused");
                await Assertions.Expect(trickplay.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true })).ToBeDisabledAsync();
                await Steps.FollowAsync(page, trickplay.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }));
                await Assertions.Expect(trickplay).Not.ToContainTextAsync("Paused");

                // A heavy job waits while Jularr is used until the Owner says otherwise.
                await Assertions.Expect(trickplay.GetByRole(AriaRole.Button, new() { Name = "Waits", Exact = true })).ToBeVisibleAsync();
                await Steps.FollowAsync(page, trickplay.GetByRole(AriaRole.Button, new() { Name = "Waits", Exact = true }));
                await Assertions.Expect(trickplay.GetByRole(AriaRole.Button, new() { Name = "Runs", Exact = true })).ToBeVisibleAsync();
                Assert.AreEqual(0, await page.Locator("tbody tr", new() { HasText = "Download tracking" }).GetByRole(AriaRole.Button, new() { Name = "Waits", Exact = true }).CountAsync());

                // Run now wakes the worker; the catalog's module is off, so its pass ends as Off.
                var catalog = page.Locator("tbody tr", new() { HasText = "Catalog sync" });
                await Steps.FollowAsync(page, catalog.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true }));
                for (var attempt = 0; attempt < 30 && !(await catalog.InnerTextAsync()).Contains("Off", StringComparison.Ordinal); attempt++)
                {
                    await Task.Delay(500);
                    await page.ReloadAsync();
                }

                await Assertions.Expect(catalog).ToContainTextAsync("Off");
                await Evidence.SaveAsync(page, "jobs");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "jobs");
                throw;
            }
        }
    }
}
