using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The indexers and the download clients as lists of their own: added in a dialog, tested and switched in place, one row of navigation.
    [TestClass]
    public sealed class DownloadPagesTests
    {
        private static ILocator Row(IPage page, string name) => page.Locator(".list-body .list-row", new() { HasText = name });

        [TestMethod]
        [Timeout(300_000)]
        public async Task IndexersAndClients_AreAddedInDialogs_TestedAndSwitchedWithoutReload()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // An empty list says so; the one navigation row joins the pages.
                await page.GotoAsync("/Admin/Acquisition");
                await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".list-empty")).ToBeVisibleAsync();

                // The indexer is added in a dialog and shows as not tested.
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add indexer", Exact = true }));
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

                // The switch turns the indexer off in place and the figures follow.
                await Steps.FollowAsync(page, Row(page, "Fake indexer").GetByRole(AriaRole.Switch, new() { Name = "Active", Exact = true }));
                await Assertions.Expect(Row(page, "Fake indexer").GetByRole(AriaRole.Switch)).ToHaveAttributeAsync("aria-checked", "false");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "Disabled" }).Locator(".kpi-tile__value")).ToHaveTextAsync("1");
                Assert.AreEqual(7, await page.EvaluateAsync<int>("window.__downloadsProbe"));

                // Editing opens the same dialog; closing it leaves the address of the item.
                await Steps.FollowAsync(page, Row(page, "Fake indexer").GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Assertions.Expect(page.Locator("dialog#connection-editor [name=Name]")).ToHaveValueAsync("Fake indexer");
                await Assertions.Expect(page.Locator("dialog#connection-editor [name=ApiKey]")).ToHaveValueAsync("");
                await page.Locator("dialog#connection-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                Assert.IsFalse(page.Url.Contains("AcquisitionConnectionId", StringComparison.Ordinal), page.Url);

                // The download client has its own page, the same list and a dialog of its own fields.
                await page.GotoAsync("/Admin/Acquisition/Downloads");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add download client", Exact = true }));
                form = page.Locator("dialog#connection-editor form").First;
                await form.Locator("[name=Name]").FillAsync("Fake SABnzbd");
                await form.Locator("[name=BaseUrl]").FillAsync(new Uri(fakes.Address, "sabnzbd/").ToString());
                await form.Locator("[name=ApiKey]").FillAsync(FakeServices.ClientKey);
                await form.Locator("[name=LocalPath]").FillAsync(fakes.Downloads);
                await Steps.SubmitAsync(page, form);
                await Steps.FollowAsync(page, Row(page, "Fake SABnzbd").GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true }));
                await Assertions.Expect(Row(page, "Fake SABnzbd")).ToContainTextAsync("Reachable");
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);

                // The lists fit a phone.
                await page.SetViewportSizeAsync(390, 844);
                foreach (var path in new[] { "/Admin/Acquisition", "/Admin/Acquisition/Downloads" })
                {
                    await page.GotoAsync(path);
                    Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow on " + path);
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join(Environment.NewLine, browser.Problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(IndexersAndClients_AreAddedInDialogs_TestedAndSwitchedWithoutReload));
                throw;
            }
        }
    }
}
