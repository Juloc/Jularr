using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Discover catalog page: the catalogs are cards with their tier, mode, counts and inputs; a sync window is added as a draft in the card, typed in the desktop time fields and saved by the page's pill.
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

            // Every catalog is a card: how it is kept (here only what Discover loaded), the titles and what is still to read.
            var cards = page.Locator(".card-grid .feature-card");
            await Assertions.Expect(cards).ToHaveCountAsync(3);
            var aniList = cards.Filter(new() { HasText = "AniList" });
            await Assertions.Expect(aniList).ToContainTextAsync("Kept");
            Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "Sync now", Exact = true }).IsDisabledAsync(), "Sync now needs full local discovery.");

            // The catalog's settings are inputs of the card: no Configure button, no dialog.
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Configure", Exact = true }).CountAsync());
            await Assertions.Expect(aniList.Locator("[name=RequestsPerMinute]")).ToHaveValueAsync("20");
            await Steps.FollowAsync(page, aniList.GetByRole(AriaRole.Button, new() { Name = "Add window", Exact = true }));
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("02:00");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeVisibleAsync();

            // The desktop shows hour and minute fields that fill the native time field.
            var parts = aniList.Locator(".ui-time__part");
            await Assertions.Expect(parts).ToHaveCountAsync(4);
            await parts.Nth(0).FillAsync("22");
            await parts.Nth(1).FillAsync("30");
            await parts.Nth(2).FillAsync("01");
            await parts.Nth(3).FillAsync("00");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Evidence.SaveAsync(page, "catalog-settings");
            await Steps.SaveAsync(page);

            await page.ReloadAsync();
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].EndsAt']")).ToHaveValueAsync("01:00");

            // The card's tier replaces its old switch: kept whole before the update, the lean tier asks for Discover's lists each time.
            await Assertions.Expect(aniList.GetByRole(AriaRole.Radio, new() { Name = "3 Full", Exact = true })).ToBeCheckedAsync();
            Assert.AreEqual(0, await aniList.GetByText("Keep whole", new() { Exact = true }).CountAsync());
            await aniList.GetByRole(AriaRole.Radio, new() { Name = "1 Lean", Exact = true }).CheckAsync(new() { Force = true });
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(aniList.GetByRole(AriaRole.Radio, new() { Name = "1 Lean", Exact = true })).ToBeCheckedAsync();
            await Assertions.Expect(aniList).ToContainTextAsync("Live");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");

            // One section keeps the instance's choices beside the images of other titles, each saved through the page's pill.
            var section = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Cache", Exact = true }) });
            var details = section.Locator("form[action='/Admin/Catalog?handler=Cache']");
            var covers = section.Locator("form[action='/Admin/Catalog?handler=Covers']");
            await Assertions.Expect(details.GetByLabel("Keep details for")).ToHaveValueAsync("14");
            await details.GetByLabel("Keep details for").FillAsync("30");
            await details.GetByRole(AriaRole.Switch, new() { Name = "Detail Discover in advance", Exact = true }).CheckAsync(new() { Force = true });
            await Assertions.Expect(covers.GetByLabel("Keep unused images for")).ToHaveValueAsync("14");
            await covers.GetByLabel("Largest size, 0 keeps none").FillAsync("120");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(details.GetByLabel("Keep details for")).ToHaveValueAsync("30");
            await Assertions.Expect(details.GetByRole(AriaRole.Switch, new() { Name = "Detail Discover in advance", Exact = true })).ToBeCheckedAsync();
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

            // Seven catalogs, each a card with an icon tile and its display name; no card is named by a raw key.
            var catalogRows = page.Locator(".card-grid .feature-card");
            await Assertions.Expect(catalogRows).ToHaveCountAsync(7);
            foreach (var name in new[] { "TMDB films", "TMDB series", "AniList", "TVDB", "Open Library", "MusicBrainz", "IGDB" })
            {
                await Assertions.Expect(catalogRows.Filter(new() { HasText = name })).ToHaveCountAsync(1);
            }

            await Assertions.Expect(catalogRows.Locator(".icon-tile .ui-icon")).ToHaveCountAsync(7);

            // Only a catalog with a full sync may keep the whole catalog.
            await Assertions.Expect(catalogRows.Filter(new() { HasText = "TVDB" }).GetByRole(AriaRole.Radio, new() { Name = "3 Full", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(catalogRows.Filter(new() { HasText = "TMDB films" }).GetByRole(AriaRole.Radio, new() { Name = "3 Full", Exact = true })).ToBeEnabledAsync();
            var text = await page.Locator("#app-content").InnerTextAsync();
            foreach (var key in new[] { "tmdb_movie", "tmdb_tv", "tvdb", "openlibrary", "musicbrainz", "igdb", "anilist", "admin.catalog." })
            {
                Assert.IsFalse(text.Contains(key, StringComparison.Ordinal), key);
            }
        }
    }
}
