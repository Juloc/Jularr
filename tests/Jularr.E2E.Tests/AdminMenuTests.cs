using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The administration menu as the Owner uses it: six groups of entries with a find field, one row of sibling pages on every page of an entry, three live figures.
    [TestClass]
    public sealed class AdminMenuTests
    {
        private const string Marker = "window.menuMarker = 'same document'";

        [TestMethod]
        [Timeout(300_000)]
        public async Task Menu_MarksTheCurrentEntryAfterEverySwap_ShowsOneRowOfPages_AndEveryAddressStillRenders()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Dashboard");
                await page.EvaluateAsync(Marker);
                var menu = page.Locator(".app-sidebar--admin .menu");
                await Assertions.Expect(menu.Locator(".menu-group")).Not.ToHaveCountAsync(0);
                await Assertions.Expect(menu.Locator(".menu-entry.is-active")).ToHaveCountAsync(1);
                await Assertions.Expect(menu.Locator(".menu-entry.is-active")).ToHaveAttributeAsync("href", "/Admin/Dashboard");
                Assert.AreEqual(0, await menu.Locator(".menu-group__label").EvaluateAllAsync<int>("labels => labels.filter(label => label.textContent.trim() === '').length"), "Every group has its name.");

                // A click on an entry swaps the page in place and the mark follows it; the group of the page is tinted.
                await menu.Locator(".menu-entry", new() { HasText = "Jobs" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/Jobs$"));
                await Assertions.Expect(menu.Locator(".menu-entry.is-active")).ToHaveAttributeAsync("href", "/Admin/Jobs");
                await Assertions.Expect(menu.Locator(".menu-group--current")).ToHaveCountAsync(1);
                Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.menuMarker"));

                // The settings are one entry with five pages in one row; a chip swaps the page and the entry stays marked.
                await menu.Locator(".menu-entry", new() { HasText = "Settings" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/Instance$"));
                var row = page.Locator("main .navigation-tabs");
                await Assertions.Expect(row).ToHaveCountAsync(1);
                await Assertions.Expect(row.Locator(".navigation-tabs__link")).ToHaveCountAsync(5);
                await row.GetByRole(AriaRole.Link, new() { Name = "Modules", Exact = true }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/Instance/Modules$"));
                await Assertions.Expect(row.Locator("[aria-current=page]")).ToHaveTextAsync("Modules");
                await Assertions.Expect(menu.Locator(".menu-entry.is-active")).ToHaveAttributeAsync("href", "/Admin/Instance");
                await Assertions.Expect(page.Locator(".breadcrumb")).ToContainTextAsync("Settings");
                Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.menuMarker"));

                // The quality rules are panes of one address and keep one row with their media type menu.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
                await Assertions.Expect(page.Locator("main .navigation-tabs")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("main .navigation-tabs [aria-current=page]")).ToHaveTextAsync("Custom formats");
                await Assertions.Expect(menu.Locator(".menu-entry.is-active")).ToHaveAttributeAsync("href", "/Admin/Downloads");

                // Every entry opens its first page and every chip of its row a page that renders: the old addresses all work.
                var entries = await menu.Locator(".menu-entry").EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");
                var seen = new HashSet<string>();
                foreach (var entry in entries)
                {
                    await page.GotoAsync(entry);
                    var chips = await page.Locator("main .navigation-tabs .navigation-tabs__link").EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");
                    foreach (var address in chips.Prepend(entry).Where(address => address.StartsWith("/Admin", StringComparison.Ordinal) && seen.Add(address)))
                    {
                        var response = await page.GotoAsync(address, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                        Assert.IsNotNull(response, address);
                        Assert.IsTrue(response.Status < 400, address + " answered " + response.Status);
                        Assert.AreEqual(1, await page.Locator("main").CountAsync(), address + " has no page content");
                    }
                }

                Assert.IsTrue(seen.Count > 15, "The menu reaches too few pages: " + string.Join(", ", seen));
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "admin-menu");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Find_NarrowsTheEntriesAsOneTypes_AndTheFigureOfOpenRequestsFollowsWithoutReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('Acquisition', 'Requests')");
                await page.GotoAsync("/Admin/Dashboard");
                await page.EvaluateAsync(Marker);
                var menu = page.Locator(".app-sidebar--admin .menu");
                var find = menu.Locator("[data-menu-find] input");
                var all = await menu.Locator(".menu-entry:visible").CountAsync();
                Assert.IsTrue(all > 5);

                // A word of a page name keeps the entry that has the page; the groups without a match go.
                await find.FillAsync("modules");
                await Assertions.Expect(menu.Locator(".menu-entry:visible")).ToHaveCountAsync(1);
                await Assertions.Expect(menu.Locator(".menu-entry:visible")).ToContainTextAsync("Settings");
                await Assertions.Expect(menu.Locator(".menu-group:visible")).ToHaveCountAsync(1);

                // A group's name shows its entries; Escape brings every entry back, and the page was not reloaded.
                await find.FillAsync("maintenance");
                await Assertions.Expect(menu.Locator(".menu-group:visible")).ToHaveCountAsync(1);
                await Assertions.Expect(menu.Locator(".menu-entry:visible")).Not.ToHaveCountAsync(0);
                await find.FillAsync("nothing like it");
                await Assertions.Expect(menu.Locator(".menu-entry:visible")).ToHaveCountAsync(0);
                await find.PressAsync("Escape");
                await Assertions.Expect(menu.Locator(".menu-entry:visible")).ToHaveCountAsync(all);
                await find.FillAsync("logs");
                await find.PressAsync("Enter");
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/System$"));
                Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.menuMarker"));

                // The figure of the users entry hides at zero, and an open request shows on it by the live stream, without a reload.
                var badge = page.Locator(".menu-entry .menu-badge[data-live-text=requests]");
                await Assertions.Expect(badge).ToBeHiddenAsync();
                await jularr.RunSqlAsync("""
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Menu film');
                    INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByAccountId", "UiLocaleId")
                    SELECT (SELECT max("Id") FROM "Works"), (SELECT min("Id") FROM "Accounts"), (SELECT min("Id") FROM "UiLocales");
                    """);
                await Assertions.Expect(badge).ToHaveTextAsync("1", new() { Timeout = 30_000 });
                await Assertions.Expect(badge).ToBeVisibleAsync();
                Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.menuMarker"));
                await Evidence.SaveAsync(page, "admin-menu-figure");

                // On a phone the menu is the drawer with its find field.
                await page.SetViewportSizeAsync(390, 844);
                await page.GotoAsync("/Admin/Dashboard");
                await Assertions.Expect(page.Locator(".app-sidebar")).ToBeHiddenAsync();
                await page.Locator("[data-sidebar-toggle]").First.ClickAsync();
                await Assertions.Expect(page.Locator(".app-sidebar .menu-find")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".app-sidebar .menu-entry:visible").First).ToBeVisibleAsync();
                Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow on a phone.");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "admin-menu-find");
                throw;
            }
        }
    }
}
