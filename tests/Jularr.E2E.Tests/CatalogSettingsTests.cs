using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Discover catalog page: the catalogs are rows with their mode and counts; a sync window is added as a draft in the catalog's dialog, typed in the desktop time fields and saved there.
    [TestClass]
    public sealed class CatalogSettingsTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task AniListWindow_AddedTypedAndSaved_StaysAfterReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Catalog");
            await Assertions.Expect(page.GetByText("Full local discovery is off", new() { Exact = false })).ToBeVisibleAsync();

            // Every catalog is a row: how it is kept (here only what Discover loaded), the titles and what is still to read.
            var rows = page.Locator(".list-body .list-row");
            await Assertions.Expect(rows).ToHaveCountAsync(3);
            var aniListRow = rows.Filter(new() { HasText = "AniList" });
            await Assertions.Expect(aniListRow).ToContainTextAsync("Kept");
            await Assertions.Expect(aniListRow).ToContainTextAsync("20 requests/min");
            Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "Sync now", Exact = true }).IsDisabledAsync(), "Sync now needs full local discovery.");

            // The catalog's settings open in a dialog of their own.
            var aniList = page.GetByRole(AriaRole.Dialog, new() { Name = "AniList", Exact = true });
            await aniListRow.GetByRole(AriaRole.Button, new() { Name = "Configure", Exact = true }).ClickAsync();
            await Assertions.Expect(aniList).ToBeVisibleAsync();
            await Steps.FollowAsync(page, aniList.GetByRole(AriaRole.Button, new() { Name = "Add window", Exact = true }));
            await Assertions.Expect(aniList).ToBeVisibleAsync();

            // The desktop shows hour and minute fields that fill the native time field.
            var parts = aniList.Locator(".ui-time__part");
            await Assertions.Expect(parts).ToHaveCountAsync(4);
            await parts.Nth(0).FillAsync("22");
            await parts.Nth(1).FillAsync("30");
            await parts.Nth(2).FillAsync("01");
            await parts.Nth(3).FillAsync("00");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Evidence.SaveAsync(page, "catalog-settings");
            await Steps.SubmitAsync(page, aniList.Locator("form"));

            await Assertions.Expect(rows.Filter(new() { HasText = "AniList" })).ToContainTextAsync("22:30–01:00");
            await page.ReloadAsync();
            await page.Locator(".list-body .list-row").Filter(new() { HasText = "AniList" }).GetByRole(AriaRole.Button, new() { Name = "Configure", Exact = true }).ClickAsync();
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].EndsAt']")).ToHaveValueAsync("01:00");
            await page.Keyboard.PressAsync("Escape");

            // The cache of images of other titles is sized here and saved through the page's pill.
            var covers = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Images of other titles", Exact = true }) }).Locator("form[data-save]");
            await Assertions.Expect(covers.GetByLabel("Keep unused images for")).ToHaveValueAsync("14");
            await covers.GetByLabel("Largest size, 0 keeps none").FillAsync("120");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(covers.GetByLabel("Largest size, 0 keeps none")).ToHaveValueAsync("120");
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task EveryCatalog_ShowsItsNameAndIcon_NeverItsKey()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute") VALUES ('tvdb', 30), ('openlibrary', 30), ('musicbrainz', 30), ('igdb', 30);
                """);
            await page.GotoAsync("/Admin/Catalog");

            // Seven catalogs, each a row with an icon tile and its display name; no row is named by a raw key.
            var catalogRows = page.Locator(".list-body .list-row");
            await Assertions.Expect(catalogRows).ToHaveCountAsync(7);
            foreach (var name in new[] { "TMDB films", "TMDB series", "AniList", "TVDB", "Open Library", "MusicBrainz", "IGDB" })
            {
                await Assertions.Expect(catalogRows.Filter(new() { HasText = name })).ToHaveCountAsync(1);
            }

            await Assertions.Expect(catalogRows.Locator(".icon-tile .ui-icon")).ToHaveCountAsync(7);
            var text = await page.Locator("#app-content").InnerTextAsync();
            foreach (var key in new[] { "tmdb_movie", "tmdb_tv", "tvdb", "openlibrary", "musicbrainz", "igdb", "anilist", "admin.catalog." })
            {
                Assert.IsFalse(text.Contains(key, StringComparison.Ordinal), key);
            }
        }
    }
}
