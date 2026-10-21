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
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Series", "Series"));
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);

                // Adding the title stored its poster and backdrop; the page shows them from Jularr's own address and never from the provider.
                Assert.IsTrue(fakes.ImageRequests >= 2);

                // The wanted page of a series leads to the title's page; the breadcrumb names the list it belongs to.
                await Steps.OpenWantedAsync(page, FakeServices.SeriesTitle);
                StringAssert.Contains(new Uri(page.Url).AbsolutePath, "/Admin/Works/");
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = FakeServices.SeriesTitle, Level = 1 })).ToBeVisibleAsync();
                var cover = page.Locator("img.media-image[src^='/api/v1/covers/']");
                await Assertions.Expect(cover.First).ToBeVisibleAsync();
                Assert.IsTrue(await cover.First.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"), "The stored cover did not load.");
                Assert.AreEqual(0, await page.Locator("img[src*='image.tmdb.org']").CountAsync());
                Assert.IsTrue(fakes.ImageRequests >= 2);
                await Assertions.Expect(page.Locator(".app-header-location a.breadcrumb__link", new() { HasText = "Works" })).ToBeVisibleAsync();
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
                await page.Locator("th [data-columns-open]").First.ClickAsync();
                var picker = page.Locator("[data-column-picker]");
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await picker.GetByRole(AriaRole.Checkbox, new() { Name = "Status", Exact = true }).UncheckAsync();
                await Assertions.Expect(page.Locator("th[data-col=status]").First).ToBeHiddenAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("th[data-col=status]").First).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("th[data-col=aired]").First).ToBeVisibleAsync();

                // The hero shows the title and its description from the metadata.
                var hero = page.Locator(".work-hero");
                await Assertions.Expect(hero.GetByRole(AriaRole.Heading, new() { Name = FakeServices.SeriesTitle, Level = 1 })).ToBeVisibleAsync();
                await Assertions.Expect(hero.Locator(".work-overview")).ToContainTextAsync("Two test episodes.");

                // One control collapses all sections and, once none is open, expands them again.
                var collapseAll = page.Locator("[data-collapse-all]");
                await Assertions.Expect(collapseAll).ToContainTextAsync("Collapse all");
                await collapseAll.ClickAsync();
                await Assertions.Expect(collapseAll).ToContainTextAsync("Expand all");
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeHiddenAsync();

                // Monitoring is the switch of the hero. The action swaps only the page's region: the page content stays, the collapsed season stays collapsed,
                // the hero does not change its size and the hint says what happened.
                await page.EvaluateAsync("() => { window.keptContent = document.getElementById('app-content'); window.heroHeight = document.querySelector('.work-hero').getBoundingClientRect().height; }");
                var monitor = page.Locator(".work-actions .work-switch").First;
                await Assertions.Expect(monitor).ToHaveAttributeAsync("aria-label", "Stop monitoring");
                await Steps.SubmitAsync(page, page.Locator(".work-actions form").Filter(new() { Has = page.GetByRole(AriaRole.Switch) }));
                await Assertions.Expect(page.Locator(".work-actions .work-switch").First).ToHaveAttributeAsync("aria-label", "Start monitoring");
                await Assertions.Expect(page.Locator("[data-action-toast]")).ToContainTextAsync("Monitoring off");
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => window.keptContent === document.getElementById('app-content')"), "The action swapped the whole page content.");
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => window.heroHeight === document.querySelector('.work-hero').getBoundingClientRect().height"), "The hero changed its size.");
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeHiddenAsync();
                await Assertions.Expect(collapseAll).ToContainTextAsync("Expand all");
                await collapseAll.ClickAsync();
                await Assertions.Expect(collapseAll).ToContainTextAsync("Collapse all");
                await Assertions.Expect(season.Locator("tr.work-row").First).ToBeVisibleAsync();

                // A result such as "nothing found" is the hint, never a message inside the hero.
                Assert.AreEqual(0, await hero.Locator("[role=status]").CountAsync());

                // The edit dialog: title row with the X, the fields, and Cancel and the save button in its footer, at the bottom.
                var editor = page.Locator("#work-edit");
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(editor).ToBeVisibleAsync();
                await Assertions.Expect(editor.Locator(".modal__head [data-dialog-close]")).ToBeVisibleAsync();
                await Assertions.Expect(editor.GetByText("Close folder browser")).ToHaveCountAsync(0);
                var save = editor.GetByRole(AriaRole.Button, new() { Name = "Save changes" });
                await Assertions.Expect(save).ToBeVisibleAsync();
                var lastField = (await editor.Locator(".ui-field").Last.BoundingBoxAsync())!;
                var saveBox = (await save.BoundingBoxAsync())!;
                Assert.IsTrue(saveBox.Y > lastField.Y + lastField.Height, "The save button is not below the fields of the dialog.");
                await editor.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
                await Assertions.Expect(editor).ToBeHiddenAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await editor.Locator(".modal__head [data-dialog-close]").ClickAsync();
                await Assertions.Expect(editor).ToBeHiddenAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(editor).ToBeHiddenAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Steps.SubmitAsync(page, editor.Locator("form"));
                await Assertions.Expect(editor).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("[data-action-toast]")).ToContainTextAsync("Saved");
                await Evidence.SaveAsync(page, "work-page-series");

                // A narrow screen keeps the page itself in its width.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "work-page-series-mobile");
                await page.SetViewportSizeAsync(1280, 900);

                // Deleting the title with an exclusion: it leaves the list and the exclusion is listed until it is removed.
                await page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
                var deletion = page.Locator("#work-delete");
                await Assertions.Expect(deletion).ToBeVisibleAsync();
                await deletion.Locator("label.toggle", new() { HasText = "Add an exclusion" }).ClickAsync();
                Assert.IsTrue(await deletion.Locator("[name=AddsExclusion][type=checkbox]").IsCheckedAsync());
                await Evidence.SaveAsync(page, "work-delete-dialog");
                await Steps.SubmitAsync(page, deletion.Locator("form"));
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Admin/Works$"));
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Excluded titles" })).ToBeVisibleAsync();
                await Steps.SubmitAsync(page, page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Remove exclusion" }) }));
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Excluded titles" })).ToBeHiddenAsync();
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
