using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Discover catalog page: the catalogs are cards with their mode, counts and inputs; a sync window is added as a draft in the card, typed in the desktop time fields and saved by the page's pill.
    // How much is kept is the Caching module's choice on the Modules page: its days, and while it is on its sub-options.
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
            // TMDB keeps only films whole; series come from TheTVDB.
            var cards = page.Locator(".card-grid .feature-card");
            await Assertions.Expect(cards).ToHaveCountAsync(2);
            await Assertions.Expect(cards.Filter(new() { HasText = "TMDB series" })).ToHaveCountAsync(0);
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

            // A card has no choice of its own how much is kept; the cache of images of other titles is sized here and saved through the page's pill.
            Assert.AreEqual(0, await aniList.GetByRole(AriaRole.Radio).CountAsync());
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Cache", Exact = true }).CountAsync());
            var covers = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Images of other titles", Exact = true }) }).Locator("form[data-save]");
            await Assertions.Expect(covers.GetByLabel("Keep unused images for")).ToHaveValueAsync("14");
            await covers.GetByLabel("Largest size, 0 keeps none").FillAsync("120");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(covers.GetByLabel("Largest size, 0 keeps none")).ToHaveValueAsync("120");
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task CachingModule_ShowsItsDaysAndSubOptions_AndDecidesHowEveryCatalogIsKept()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Modules");

            // One card: the module's switch, the days with their tooltip, and while it is on the full catalog and detailing Discover in advance.
            var card = page.Locator(".setting-card[data-module=DiscoverCache]");
            var own = card.Locator(".setting-card__head input[type=checkbox]");
            var subs = card.Locator(".option-subs");
            var days = card.GetByLabel("Keep extended data");
            var full = subs.Locator("[data-module=LocalDiscovery]");
            var ahead = subs.GetByRole(AriaRole.Switch, new() { Name = "Detail Discover in advance", Exact = true });
            await Assertions.Expect(card.Locator(".setting-card__name .truncated__text")).ToHaveTextAsync("Caching");
            await Assertions.Expect(own).ToBeCheckedAsync();
            await Assertions.Expect(days).ToHaveValueAsync("14");
            await Assertions.Expect(subs).ToBeVisibleAsync();
            await Assertions.Expect(full).ToContainTextAsync("Keep full catalog");
            await Assertions.Expect(page.Locator(".setting-card[data-module=LocalDiscovery]")).ToHaveCountAsync(0);
            await card.Locator(".ui-field__info").ClickAsync();
            await Assertions.Expect(card.GetByRole(AriaRole.Tooltip)).ToContainTextAsync("Seasons and episodes, cast, trailers, descriptions and relations");

            await days.FillAsync("30");
            await full.Locator(".toggle").ClickAsync();
            await ahead.CheckAsync(new() { Force = true });
            await Evidence.SaveAsync(page, "caching-module");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(days).ToHaveValueAsync("30");
            await Assertions.Expect(full.Locator("input[type=checkbox]")).ToBeCheckedAsync();
            await Assertions.Expect(ahead).ToBeCheckedAsync();

            // The catalogs with a full list are kept whole now; the page names no choice of its own.
            await page.GotoAsync("/Admin/Catalog");
            var aniList = page.Locator(".card-grid .feature-card").Filter(new() { HasText = "AniList" });
            await Assertions.Expect(aniList).ToContainTextAsync("Whole");
            await Assertions.Expect(page.GetByText("Full local discovery is off", new() { Exact = false })).ToHaveCountAsync(0);
            Assert.IsFalse(await page.GetByRole(AriaRole.Button, new() { Name = "Sync now", Exact = true }).IsDisabledAsync());

            // Off: the sub-options hide, the days stay; Discover asks the providers each time.
            await page.GotoAsync("/Admin/Instance/Modules");
            await full.Locator(".toggle").ClickAsync();
            await card.Locator(".setting-card__head .toggle").ClickAsync();
            await Assertions.Expect(own).Not.ToBeCheckedAsync();
            await Assertions.Expect(subs).ToBeHiddenAsync();
            await Assertions.Expect(days).ToBeVisibleAsync();
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(own).Not.ToBeCheckedAsync();
            await Assertions.Expect(days).ToHaveValueAsync("30");
            await page.GotoAsync("/Admin/Catalog");
            await Assertions.Expect(aniList).ToContainTextAsync("Live");
            await Assertions.Expect(page.GetByText("Discover keeps nothing locally", new() { Exact = false })).ToBeVisibleAsync();
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

            // Six catalogs, each a card with an icon tile and its display name; no card is named by a raw key.
            var catalogRows = page.Locator(".card-grid .feature-card");
            await Assertions.Expect(catalogRows).ToHaveCountAsync(6);
            foreach (var name in new[] { "TMDB films", "AniList", "TVDB", "Open Library", "MusicBrainz", "IGDB" })
            {
                await Assertions.Expect(catalogRows.Filter(new() { HasText = name })).ToHaveCountAsync(1);
            }

            await Assertions.Expect(catalogRows.Locator(".icon-tile .ui-icon")).ToHaveCountAsync(6);

            var text = await page.Locator("#app-content").InnerTextAsync();
            foreach (var key in new[] { "tmdb_movie", "tmdb_tv", "tvdb", "openlibrary", "musicbrainz", "igdb", "anilist", "admin.catalog." })
            {
                Assert.IsFalse(text.Contains(key, StringComparison.Ordinal), key);
            }
        }
    }
}
