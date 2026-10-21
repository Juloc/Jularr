using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A fresh installation in a real browser: the Owner is created in setup, signs in, and every page of the menu opens cleanly.
    [TestClass]
    public sealed class FirstRunTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task FreshInstance_OwnerSetupSignInAndMenuPages_OpenWithoutErrors()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var page = await context.NewPageAsync();
            var problems = new ConcurrentQueue<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error" && !IsProviderCover(message.Location)) problems.Enqueue(page.Url + ": " + message.Text);
            };
            page.PageError += (_, error) => problems.Enqueue(page.Url + ": " + error);
            page.Response += (_, response) =>
            {
                if (response.Status >= 500 && !IsProviderCover(response.Url)) problems.Enqueue(response.Url + ": HTTP " + response.Status);
            };

            try
            {
                // The first visit leads to setup, where the Owner is created, then signs in again.
                await Owner.SetUpAndSignInAsync(page);

                // Every menu entry opens with its content and without server or script errors.
                var links = await page.Locator("nav a[href^='/']").EvaluateAllAsync<string[]>("links => [...new Set(links.map(link => link.getAttribute('href')))]");
                Assert.IsTrue(links.Length > 5, "The menu shows too few pages: " + string.Join(", ", links));
                foreach (var link in links.Where(link => !link.StartsWith("/Account/Logout", StringComparison.Ordinal)))
                {
                    // The page itself is what is checked: covers of the real catalogs may load slowly or never in a test run.
                    var response = await page.GotoAsync(link, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    Assert.IsNotNull(response, link);
                    Assert.IsTrue(response.Status < 400, link + " answered " + response.Status);
                    Assert.AreEqual(1, await page.Locator("main").CountAsync(), link + " has no page content");
                }

                // The calendar shows its month, switches to the week and the day in place, and opens its filter as a dialog.
                await page.GotoAsync("/Calendar");
                await Assertions.Expect(page.Locator(".calendar__grid")).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = "Week", Exact = true }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("view=week"));
                await Assertions.Expect(page.Locator(".calendar__week")).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = "Day", Exact = true }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("view=day"));
                await Assertions.Expect(page.Locator(".calendar__strip-dates")).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Filter", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("dialog.filter-dialog[open]")).ToBeVisibleAsync();

                CollectionAssert.AreEqual(Array.Empty<string>(), problems.ToArray(), string.Join(Environment.NewLine, problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, "first-run");
                throw;
            }
        }

        // Covers of the real catalogs (Open Library, TMDB) are fetched through the server from sites this test does not control.
        private static bool IsProviderCover(string? address) => address?.Contains("/api/v1/covers/external", StringComparison.Ordinal) == true;
    }
}
