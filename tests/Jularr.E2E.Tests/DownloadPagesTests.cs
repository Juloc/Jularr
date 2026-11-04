using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The indexers as a compact list whose rows open an edit dialog (added in a dialog with Cancel too), tested in place; the download clients as their list with the opened one below it; one row of navigation.
    [TestClass]
    public sealed class DownloadPagesTests
    {
        private static ILocator Row(IPage page, string name) => page.Locator(".connections-list .list-row", new() { HasText = name });

        private static ILocator Editor(IPage page) => page.Locator("dialog#connection-editor[open]");

        [TestMethod]
        [Timeout(300_000)]
        public async Task Indexers_EditedInTheirDialog_ShowTodaysQueriesAgainstTheLimit()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Acquisition?Type=Newznab");
                var added = page.Locator("dialog#connection-editor form").First;
                await added.Locator("[name=Name]").FillAsync("Limited indexer");
                await added.Locator("[name=BaseUrl]").FillAsync(new Uri(fakes.Address, "newznab/").ToString());
                await added.Locator("[name=ApiKey]").FillAsync(FakeServices.IndexerKey);
                await Steps.SubmitAsync(page, added);

                // The row names the kind and what it serves; without a daily limit today's column says so instead of a bar.
                var row = Row(page, "Limited indexer");
                await Assertions.Expect(row.Locator(".tag")).ToHaveTextAsync("Usenet");
                await Assertions.Expect(row).ToContainTextAsync("All types");
                await Assertions.Expect(row.Locator(".connection-usage")).ToContainTextAsync("No limit");
                await Assertions.Expect(row.Locator("progress")).ToHaveCountAsync(0);

                // The dialog holds every setting: the limits and the media types are saved by its Save and the row shows the bar against the limit.
                await Steps.FollowAsync(page, row.GetByRole(AriaRole.Link, new() { Name = "Limited indexer" }));
                var editor = Editor(page).Locator("form", new() { Has = page.Locator("[name=Name]") });
                await editor.Locator("[name=QueryLimit]").FillAsync("40");
                await editor.Locator("[name=GrabLimit]").FillAsync("5");
                await editor.GetByLabel("Movie", new() { Exact = true }).CheckAsync();
                await Steps.SubmitAsync(page, editor);
                await Assertions.Expect(Editor(page)).ToHaveCountAsync(0);
                await Assertions.Expect(row.Locator("progress.connection-usage__bar")).ToHaveAttributeAsync("max", "40");
                await Assertions.Expect(row.Locator(".connection-usage__text")).ToContainTextAsync("/ 40 queries");
                await Assertions.Expect(row.Locator(".connection-usage__text")).ToContainTextAsync("/ 5 grabs");
                await Assertions.Expect(row.Locator(".list-cell--data").First).ToHaveTextAsync("Movie");

                // A test from the dialog leaves it open with the new state.
                await Steps.FollowAsync(page, row.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Steps.FollowAsync(page, Editor(page).GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true }));
                await Assertions.Expect(Editor(page).Locator(".connection-editor__facts")).ToContainTextAsync("Reachable");
                await Assertions.Expect(page.Locator("[data-action-toast]")).ToContainTextAsync("Reachable");

                // The test search of the dialog counts towards today's queries and the bar fills.
                var before = int.Parse((await row.Locator("progress").GetAttributeAsync("value"))!, System.Globalization.CultureInfo.InvariantCulture);
                await Steps.FollowAsync(page, Editor(page).GetByRole(AriaRole.Link, new() { Name = "Search", Exact = true }));
                var search = page.Locator("dialog#connection-search form");
                await search.Locator("[name=Query]").FillAsync("film");
                await Steps.SubmitAsync(page, search);
                await page.GotoAsync("/Admin/Acquisition");
                await Assertions.Expect(row.Locator("progress")).ToHaveAttributeAsync("value", (before + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Assertions.Expect(row.Locator(".connection-usage__text")).ToContainTextAsync((before + 1) + " / 40 queries");

                // Desktop and phone hold still while loading; on a phone each row is a card with its facts and nothing reaches past the viewport.
                var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                foreach (var (width, height) in new[] { (1440, 900), (390, 844) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync("/Admin/Acquisition");
                    var shift = await page.EvaluateAsync<System.Text.Json.JsonElement>(LayoutShiftTests.Measure);
                    Assert.IsTrue(shift.GetProperty("score").GetDouble() < 0.01, $"Layout shift {shift.GetProperty("score").GetDouble():0.0000} at {width}px ({shift.GetProperty("sources").GetString()})");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow at " + width + "px");
                    await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "indexers-" + width + ".png"), FullPage = true });
                }

                await Assertions.Expect(page.Locator(".connections-list .list-head")).ToBeHiddenAsync();
                await Assertions.Expect(row.Locator(".list-row__caption")).ToContainTextAsync("40 queries");
                await Steps.FollowAsync(page, row.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Assertions.Expect(Editor(page)).ToBeVisibleAsync();
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "indexers-edit-390.png") });
                await page.SetViewportSizeAsync(1440, 900);
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "indexers-edit-1440.png") });

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join(Environment.NewLine, browser.Problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(Indexers_EditedInTheirDialog_ShowTodaysQueriesAgainstTheLimit));
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task IndexersAndClients_AreAddedAndEditedInDialogs_TestedWithoutReload()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // An empty list says so. Without the Subtitles module the indexers are the only page of their entry, so no navigation row stands above them.
                await page.GotoAsync("/Admin/Acquisition");
                await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".list-empty")).ToBeVisibleAsync();

                // The indexer is added in a dialog and shows as not tested.
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add indexer", Exact = true }).And(page.Locator("[href*='Type=Newznab']")));
                Assert.AreEqual(1, await page.Locator("dialog#connection-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).CountAsync());
                var form = page.Locator("dialog#connection-editor form").First;
                await form.Locator("[name=Name]").FillAsync("Fake indexer");
                await form.Locator("[name=BaseUrl]").FillAsync(new Uri(fakes.Address, "newznab/").ToString());
                await form.Locator("[name=ApiKey]").FillAsync(FakeServices.IndexerKey);
                await form.Locator("[name=Categories]").FillAsync("2000,2040");
                await Steps.SubmitAsync(page, form);
                await Assertions.Expect(page.Locator("dialog#connection-editor[open]")).ToHaveCountAsync(0);
                await Assertions.Expect(Row(page, "Fake indexer")).ToContainTextAsync("Not tested");

                // The test swaps the list and says the result in the hint; the page is not loaded again.
                await page.EvaluateAsync("window.__downloadsProbe = 7");
                await Steps.FollowAsync(page, Row(page, "Fake indexer").GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true }));
                await Assertions.Expect(Row(page, "Fake indexer")).ToContainTextAsync("Reachable");
                await Assertions.Expect(page.Locator("[data-action-toast]")).ToContainTextAsync("Reachable");
                Assert.AreEqual(7, await page.EvaluateAsync<int>("window.__downloadsProbe"));

                // The row holds no inputs: editing is a dialog. The stored key is not echoed; a new key, the priority and the state are saved by the dialog's Save,
                // the list and the figures follow without loading the page again.
                Assert.AreEqual(0, await Row(page, "Fake indexer").Locator("input:not([type=hidden])").CountAsync(), "The row holds inputs.");
                await Steps.FollowAsync(page, Row(page, "Fake indexer").GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                var editor = Editor(page).Locator("form", new() { Has = page.Locator("[name=Name]") });
                await Assertions.Expect(editor.Locator("[name=Name]")).ToHaveValueAsync("Fake indexer");
                await Assertions.Expect(editor.Locator("[name=ApiKey]")).ToHaveValueAsync("");
                await Assertions.Expect(editor.Locator("[name=ApiKey]")).ToHaveAttributeAsync("type", "password");
                Assert.IsFalse((await page.ContentAsync()).Contains(FakeServices.IndexerKey, StringComparison.Ordinal), "The page shows the stored key.");
                await editor.Locator("[name=ApiKey]").FillAsync("another-key");
                await editor.Locator("[name=Priority]").FillAsync("7");
                await Steps.ChooseSegmentAsync(editor, "IsEnabled", "False");
                await Steps.SubmitAsync(page, editor);
                await Assertions.Expect(Editor(page)).ToHaveCountAsync(0);
                await Assertions.Expect(Row(page, "Fake indexer")).ToContainTextAsync("Disabled");
                await Assertions.Expect(Row(page, "Fake indexer").Locator(".list-cell--data").Last).ToHaveTextAsync("7");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "Disabled" }).Locator(".kpi-tile__value")).ToHaveTextAsync("1");
                Assert.IsFalse((await page.ContentAsync()).Contains("another-key", StringComparison.Ordinal), "The page shows the new key.");
                Assert.AreEqual(7, await page.EvaluateAsync<int>("window.__downloadsProbe"));

                // Deleting is a tool of the dialog and asks once more with Cancel; Cancel keeps the indexer.
                await Steps.FollowAsync(page, Row(page, "Fake indexer").GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Steps.FollowAsync(page, Editor(page).GetByRole(AriaRole.Link, new() { Name = "Delete", Exact = true }));
                await page.Locator("dialog#connection-delete").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(Row(page, "Fake indexer")).ToHaveCountAsync(1);

                // An external download client has its own page: a list, the opened client below it and a dialog of its own fields.
                await page.GotoAsync("/Admin/Downloads/Clients");
                await page.GetByRole(AriaRole.Button, new() { Name = "Add client", Exact = true }).ClickAsync();
                await Steps.FollowAsync(page, page.Locator("dialog#downloaders-add").GetByRole(AriaRole.Link, new() { Name = "SABnzbd", Exact = true }));
                form = page.Locator("dialog#connection-editor form").First;
                await form.Locator("[name=Name]").FillAsync("Fake SABnzbd");
                await form.Locator("[name=BaseUrl]").FillAsync(new Uri(fakes.Address, "sabnzbd/").ToString());
                await form.Locator("[name=ApiKey]").FillAsync(FakeServices.ClientKey);
                await form.Locator("[name=LocalPath]").FillAsync(fakes.Downloads);
                await Steps.SubmitAsync(page, form);
                await Steps.FollowAsync(page, page.Locator(".downloaders-detail", new() { HasText = "Fake SABnzbd" }).GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true }));
                await Assertions.Expect(page.Locator(".downloaders-detail", new() { HasText = "Fake SABnzbd" })).ToContainTextAsync("Reachable");
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);

                // The pages fit a phone: nothing in the panels reaches past the viewport.
                await page.SetViewportSizeAsync(375, 812);
                foreach (var path in new[] { "/Admin/Acquisition", "/Admin/Downloads", "/Admin/Downloads/Clients", "/Admin/Downloads/Servers", "/Admin/Downloads/Speed" })
                {
                    await page.GotoAsync(path);
                    Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow on " + path);
                    var overflowing = await page.EvaluateAsync<string[]>(
                        "() => [...document.querySelectorAll('.download-speed, .downloads-figures, .downloaders-detail, .downloads-head')].flatMap(root => [root, ...root.querySelectorAll('*')]).filter(element => element.getBoundingClientRect().right > innerWidth + 1).map(element => element.tagName + '.' + element.getAttribute('class'))");
                    CollectionAssert.AreEqual(Array.Empty<string>(), overflowing, "Content past the viewport on " + path + ": " + string.Join(", ", overflowing));
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join(Environment.NewLine, browser.Problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(IndexersAndClients_AreAddedAndEditedInDialogs_TestedWithoutReload));
                throw;
            }
        }
    }
}
