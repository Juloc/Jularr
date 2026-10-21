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

                // Monitoring is the switch of the hero; the dialog edits the title.
                var monitor = page.Locator(".work-actions .work-switch").First;
                await Assertions.Expect(monitor).ToHaveAttributeAsync("aria-label", "Stop monitoring");
                await Steps.SubmitAsync(page, page.Locator(".work-actions form").Filter(new() { Has = page.GetByRole(AriaRole.Switch) }));
                await Assertions.Expect(page.Locator(".work-actions .work-switch").First).ToHaveAttributeAsync("aria-label", "Start monitoring");
                await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#work-edit")).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator("#work-edit")).ToBeHiddenAsync();
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

        // The library list: rows with covers, a filter that leads to the fitting titles, the switch and the search of one row, the mass editor with its report, and the columns kept in the browser.
        [TestMethod]
        [Timeout(600_000)]
        public async Task Works_ListTakesFiltersRowActionsAndTheMassEditor()
        {
            using var folders = new Folders("Movies", "Series");
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm")));
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
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5040", searchesAutomatically: true);
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);

                // Both titles are rows with their cover.
                await page.GotoAsync("/Admin/Works");
                var movie = page.Locator("tr.works-row", new() { HasText = FakeServices.MovieTitle });
                var series = page.Locator("tr.works-row", new() { HasText = FakeServices.SeriesTitle });
                await Assertions.Expect(movie).ToBeVisibleAsync();
                await Assertions.Expect(series).ToBeVisibleAsync();
                await Assertions.Expect(series.Locator("td[data-col=cover] img")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^/api/v1/covers/"));
                await Evidence.SaveAsync(page, "works-list");

                // A filter leads to the titles that fit and stays in the address; the header search is the only search.
                await page.Locator("select[name=type]").SelectOptionAsync("2");
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("[?&]type=2"));
                await Assertions.Expect(page.Locator("tr.works-row")).ToHaveCountAsync(1);
                await Assertions.Expect(series).ToBeVisibleAsync();
                Assert.AreEqual(0, await page.Locator("main input[type=search]").CountAsync());
                await page.Locator("a.works-clear").ClickAsync();
                await Assertions.Expect(page.Locator("tr.works-row")).ToHaveCountAsync(2);

                // The switch of one row is the title page's rule: the film stops being monitored and the filter finds it.
                await movie.Locator("td[data-col=monitored] .work-switch").ClickAsync();
                await Assertions.Expect(page.Locator(".works-report")).ToContainTextAsync("1 of 1");
                await Assertions.Expect(movie.Locator("td[data-col=monitored] .work-switch")).ToHaveAttributeAsync("aria-label", "Start monitoring");
                await page.Locator("select[name=monitored]").SelectOptionAsync("false");
                await Assertions.Expect(page.Locator("tr.works-row")).ToHaveCountAsync(1);
                await Assertions.Expect(movie).ToBeVisibleAsync();
                await page.Locator("a.works-clear").ClickAsync();
                await Assertions.Expect(page.Locator("tr.works-row")).ToHaveCountAsync(2);

                // The search of one row grabs the offered release at once, as on the title's page.
                await movie.Locator("td[data-col=monitored] .work-switch").ClickAsync();
                await Assertions.Expect(movie.Locator("td[data-col=monitored] .work-switch")).ToHaveAttributeAsync("aria-label", "Stop monitoring");
                fakes.Offer(film);
                await movie.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".works-report")).ToContainTextAsync("Search:");
                for (var attempt = 0; attempt < 50 && fakes.Grabbed.Count == 0; attempt++) await Task.Delay(100);
                CollectionAssert.AreEqual(new[] { fakes.NzbAddress(film).ToString() }, fakes.Grabbed.ToArray());

                // Select all opens the mass editor in place of the filters; it sets monitoring for both and reports per title.
                await page.Locator("[data-works-select-all]").CheckAsync();
                await Assertions.Expect(page.Locator("[data-works-count]")).ToHaveTextAsync("2 selected");
                await Assertions.Expect(page.Locator(".works-toolbar")).ToBeHiddenAsync();
                await Evidence.SaveAsync(page, "works-mass-editor");
                await page.Locator("select[name=monitoring]").SelectOptionAsync("off");
                await page.Locator("form[data-works-bar] button[value=apply]").ClickAsync();
                await Assertions.Expect(page.Locator(".works-report")).ToContainTextAsync("Changes: 2 of 2");
                await Assertions.Expect(series.Locator("td[data-col=monitored] .work-switch")).ToHaveAttributeAsync("aria-label", "Start monitoring");

                // A column is hidden in the picker and stays hidden after a reload.
                await page.Locator("th [data-columns-open]").First.ClickAsync();
                await page.Locator("[data-column-picker]").GetByRole(AriaRole.Checkbox, new() { Name = "Profile", Exact = true }).UncheckAsync();
                await Assertions.Expect(page.Locator("th[data-col=profile]").First).ToBeHiddenAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("th[data-col=profile]").First).ToBeHiddenAsync();

                // A narrow screen keeps the page in its width.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "works-list-mobile");
                await page.SetViewportSizeAsync(1280, 900);

                // The delete dialog of the chosen titles removes them.
                await page.Locator("[data-works-select-all]").CheckAsync();
                await page.Locator("[data-works-delete]").ClickAsync();
                await Assertions.Expect(page.Locator("#works-delete")).ToBeVisibleAsync();
                await Steps.SubmitAsync(page, page.Locator("#works-delete form"));
                await Assertions.Expect(page.Locator(".works-report")).ToContainTextAsync("Delete");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-works-list");
                throw;
            }
        }
    }
}
