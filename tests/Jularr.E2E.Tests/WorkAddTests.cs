using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Adding a title from the library list: it is monitored, and "Search now" decides whether the first search starts at once or waits.
    [TestClass]
    public sealed class WorkAddTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task AddFromSearch_MonitorsTheTitle_AndSearchNowDecidesTheFirstSearch()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await page.GotoAsync("/Admin/Works");
                await page.GetByRole(AriaRole.Button, new() { Name = "Add title", Exact = true }).ClickAsync();
                var dialog = page.Locator("dialog[open], .modal[open]").First;
                await dialog.GetByLabel("Title").First.FillAsync("Bunny");
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Search metadata", Exact = true }));

                // The result carries the toggle, on by default.
                var toggle = dialog.GetByRole(AriaRole.Switch, new() { Name = "Search now", Exact = true });
                await Assertions.Expect(toggle).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "work-add-desktop");
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "work-add-phone");
                await page.SetViewportSizeAsync(1280, 900);

                // Without the toggle the title is monitored and its first search waits.
                await dialog.Locator("label.toggle").First.ClickAsync();
                await Assertions.Expect(toggle).Not.ToBeCheckedAsync();
                await Steps.FollowAsync(page, dialog.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }));
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Admin/Works/\\d+"));
                Assert.AreEqual(1L, await jularr.ScalarAsync("""SELECT count(*) FROM "WantedItems" WHERE NOT "IsPaused" """));
                Assert.AreEqual(true, await jularr.ScalarAsync("""SELECT "NextSearchAt" > now() + interval '23 hours' FROM "WantedItems" """));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(AddFromSearch_MonitorsTheTitle_AndSearchNowDecidesTheFirstSearch));
                throw;
            }
        }
    }
}
