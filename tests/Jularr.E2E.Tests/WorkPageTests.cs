using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class WorkPageTests
    {
        // A requested series on its management page: hero, tabs, seasons as accordions with a centred round button, the chosen columns and their order, sorting, monitoring and the edit dialog.
        [TestMethod]
        [Timeout(600_000)]
        public async Task Series_OnItsManagementPage_ShowsSeasonsColumnsAndTheTitleActions()
        {
            using var folders = new Folders("Series");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Providers:Tmdb"] = fakes.Tmdb3.ToString(), ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Series", "Series"));
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);

                // The wanted page of a series leads to the title's page; the breadcrumb names the list it belongs to.
                await Steps.OpenWantedAsync(page, FakeServices.SeriesTitle);
                StringAssert.Contains(new Uri(page.Url).AbsolutePath, "/Admin/Works/");
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = FakeServices.SeriesTitle, Level = 1 })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".app-header-location a", new() { HasText = "Works" })).ToBeVisibleAsync();
                foreach (var tab in new[] { "Seasons", "History", "Metadata" })
                {
                    await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = tab })).ToBeVisibleAsync();
                }

                // The newest season is open; its round button sits in the middle of the header.
                var season = page.Locator(".work-season").First;
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeVisibleAsync();
                var head = (await season.Locator(".work-season__head").BoundingBoxAsync())!;
                var toggle = (await season.Locator(".work-season__toggle").BoundingBoxAsync())!;
                Assert.IsTrue(Math.Abs(toggle.X + toggle.Width / 2 - (head.X + head.Width / 2)) <= 2, "The season button is not in the middle of its header.");
                await season.Locator(".work-season__toggle").ClickAsync();
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeHiddenAsync();
                await season.Locator(".work-season__toggle").ClickAsync();
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeVisibleAsync();

                // Episodes newest first; a click on the title column sorts, again reverses.
                var numbers = async () => await page.Locator("tr.work-row td[data-col=number]").AllInnerTextsAsync();
                var before = await numbers();
                Assert.IsTrue(before.Count >= 2);
                Assert.AreEqual(before.OrderByDescending(int.Parse).ToArray().First(), before[0]);
                await page.Locator("th[data-col=number] button").First.ClickAsync();
                await page.Locator("th[data-col=number] button").First.ClickAsync();
                Assert.AreEqual(before.Min(int.Parse).ToString(), (await numbers())[0]);

                // A column is hidden in the picker and stays hidden after a reload.
                await page.Locator("[data-columns-open]").First.ClickAsync();
                var picker = page.Locator("[data-column-picker]");
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await picker.GetByRole(AriaRole.Checkbox, new() { Name = "Status", Exact = true }).UncheckAsync();
                await Assertions.Expect(page.Locator("th[data-col=status]").First).ToBeHiddenAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("th[data-col=status]").First).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("th[data-col=aired]").First).ToBeVisibleAsync();

                // Monitoring is the switch of the hero; the dialog edits the title.
                var monitor = page.Locator(".work-actions .work-switch").First;
                await Assertions.Expect(monitor).ToHaveAttributeAsync("aria-label", "Stop monitoring");
                await Steps.SubmitAsync(page, page.Locator(".work-actions form").Filter(new() { Has = page.GetByRole(AriaRole.Switch) }));
                await Assertions.Expect(page.Locator(".work-actions .work-switch").First).ToHaveAttributeAsync("aria-label", "Start monitoring");
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("dialog.work-dialog")).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator("dialog.work-dialog")).ToBeHiddenAsync();
                await Evidence.SaveAsync(page, "work-page-series");

                // A narrow screen keeps the page itself in its width.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "work-page-series-mobile");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-work-page");
                throw;
            }
        }
    }
}
