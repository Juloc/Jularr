using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Jobs page: every background job of the running instance, paused and resumed by the Owner, run on request and told to wait while Jularr is used.
    [TestClass]
    public sealed class JobsTests
    {
        // The row of a job: the one whose title button carries its name.
        private static ILocator Row(IPage page, string job) =>
            page.Locator(".list-body .list-row").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = job, Exact = true }) });

        // Opens the details of a job and returns one of its switches (none for a null name).
        private static async Task<ILocator> OpenDetailsAsync(IPage page, string job, string? switchName)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = job, Exact = true }).ClickAsync();
            var drawer = page.GetByRole(AriaRole.Dialog, new() { Name = job });
            await Assertions.Expect(drawer).ToBeVisibleAsync();
            return drawer.GetByRole(AriaRole.Switch, new() { Name = switchName ?? "Enabled" });
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task OwnerSeesEveryJob_PausesResumesAndRunsOne_AndChangesWhetherItWaits()
        {
            using var folders = new Folders();
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:AniList"] = fakes.AniList.ToString()
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                var jobs = await page.APIRequest.GetAsync("/api/v1/admin/jobs");
                Assert.AreEqual(200, jobs.Status);
                using var definitions = JsonDocument.Parse(await jobs.TextAsync());
                var expectedJobs = definitions.RootElement.EnumerateArray().Select(job => job.GetProperty("key").GetString()).ToArray();
                await page.GotoAsync("/Admin/Jobs");
                var shownJobs = new List<string>();
                while (true)
                {
                    shownJobs.AddRange(await page.Locator(".jobs-list .list-identity__title").EvaluateAllAsync<string[]>(
                        "buttons => buttons.map(button => button.dataset.drawerOpen.slice('job-'.length))"));
                    var next = page.GetByRole(AriaRole.Navigation, new() { Name = "Pages", Exact = true })
                        .GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true });
                    if (await next.CountAsync() == 0) break;
                    await Steps.FollowAsync(page, next);
                }
                CollectionAssert.AreEquivalent(expectedJobs, shownJobs.ToArray());
                await page.GotoAsync("/Admin/Jobs?q=trickplay");

                // Pausing shows at once and keeps the job from running; resuming undoes it.
                var trickplay = Row(page, "Seek thumbnails");
                await Steps.FollowAsync(page, trickplay.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }));
                await page.GotoAsync("/Admin/Jobs?q=trickplay");
                await Assertions.Expect(trickplay).ToContainTextAsync("Paused");
                await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Job states" }).GetByRole(AriaRole.Link, new() { Name = "Paused" })).ToContainTextAsync("1");
                await Assertions.Expect(trickplay.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true })).ToBeDisabledAsync();
                await Steps.FollowAsync(page, trickplay.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }));
                await page.GotoAsync("/Admin/Jobs?q=trickplay");
                await Assertions.Expect(trickplay).Not.ToContainTextAsync("Paused");

                // A heavy job waits while Jularr is used until the Owner says otherwise; the switch sits in the job's details.
                var waits = await OpenDetailsAsync(page, "Seek thumbnails", "Waits while Jularr is used");
                await Assertions.Expect(waits).ToHaveAttributeAsync("aria-checked", "true");
                await Steps.FollowAsync(page, waits);
                await page.GotoAsync("/Admin/Jobs?q=trickplay");
                waits = await OpenDetailsAsync(page, "Seek thumbnails", "Waits while Jularr is used");
                await Assertions.Expect(waits).ToHaveAttributeAsync("aria-checked", "false");
                await page.Keyboard.PressAsync("Escape");
                await page.GotoAsync("/Admin/Jobs?q=downloads");
                await OpenDetailsAsync(page, "Download tracking", null);
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Dialog, new() { Name = "Download tracking" }).GetByRole(AriaRole.Switch, new() { Name = "Waits while Jularr is used" }).CountAsync());
                await page.Keyboard.PressAsync("Escape");

                // The chips count the states and narrow the list; the filter field narrows it by words.
                await page.GotoAsync("/Admin/Jobs");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Navigation, new() { Name = "Job states" }).GetByRole(AriaRole.Link, new() { Name = "Paused" }));
                await Assertions.Expect(page.Locator(".list-body .list-row")).ToHaveCountAsync(0);
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Navigation, new() { Name = "Job states" }).GetByRole(AriaRole.Link, new() { Name = "All" }));
                await page.GetByRole(AriaRole.Searchbox, new() { Name = "Filter jobs" }).FillAsync("plex");
                await Assertions.Expect(page.Locator(".list-body .list-row:not([hidden])")).ToHaveCountAsync(1);
                await page.GetByRole(AriaRole.Searchbox, new() { Name = "Filter jobs" }).FillAsync("");

                // Run now wakes the worker; the catalog's module is off, so its pass ends as Off.
                await page.GotoAsync("/Admin/Jobs?q=catalog");
                var catalog = Row(page, "Catalog sync");
                await Steps.FollowAsync(page, catalog.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true }));
                await page.GotoAsync("/Admin/Jobs?q=catalog");
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
