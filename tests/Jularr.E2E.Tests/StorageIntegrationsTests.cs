using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The storage and integration pages: connections and providers as cards, the media servers' users as a list.
    [TestClass]
    public sealed class StorageIntegrationsTests
    {
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task ConnectionCard_ShowsSpace_AnOfflineConnectionStaysListedWithItsState(int width, int height)
        {
            using var folders = new Folders("Movies");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library);
                await page.GotoAsync("/Admin/Storage");

                // A new connection has not been looked at yet; checking it names the state, how full the volume is and what the connection offers; the add card follows it.
                var card = page.Locator(".feature-card", new() { HasText = "Library" });
                await Assertions.Expect(card).ToContainTextAsync("Not checked yet");
                await Steps.FollowAsync(page, card.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }));
                card = page.Locator(".feature-card", new() { HasText = "Library" });
                await Assertions.Expect(card).ToContainTextAsync("Online");
                await Assertions.Expect(card).ToContainTextAsync("% used");
                await Assertions.Expect(card.Locator("progress")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".feature-card--add")).ToContainTextAsync("Add SMB connection");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "Online" }).Locator(".kpi-tile__value")).ToHaveTextAsync("1");

                // A connection that cannot be reached stays: offline is a state, not a deletion; it says why instead of showing a stale bar.
                var offline = folders.Library + ".offline";
                Directory.Move(folders.Library, offline);
                await Steps.FollowAsync(page, card.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }));
                card = page.Locator(".feature-card", new() { HasText = "Library" });
                await Assertions.Expect(card).ToContainTextAsync("Offline");
                await Assertions.Expect(card).ToContainTextAsync("Not reachable or not mounted");
                await Assertions.Expect(card).Not.ToContainTextAsync("% used");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "Not reachable" }).Locator(".kpi-tile__value")).ToHaveTextAsync("1");
                await Assertions.Expect(card.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true })).ToBeVisibleAsync();
                Directory.Move(offline, folders.Library);
                await Steps.FollowAsync(page, card.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }));
                await Assertions.Expect(page.Locator(".feature-card", new() { HasText = "Library" })).ToContainTextAsync("Online");

                // Removing asks first; cancelling leaves the card.
                await card.GetByRole(AriaRole.Button, new() { Name = "Remove connection", Exact = true }).ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Remove connection", Exact = true });
                await Assertions.Expect(dialog).ToBeVisibleAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".feature-card", new() { HasText = "Library" })).ToHaveCountAsync(1);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "storage-cards-" + width);

                // Download storage is the other chip of the same row.
                Assert.AreEqual(1, await page.Locator("main .navigation-tabs").CountAsync());
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Navigation, new() { Name = "Pages of this area" }).GetByRole(AriaRole.Link, new() { Name = "Download storage", Exact = true }));
                Assert.IsTrue(page.Url.EndsWith("/Admin/DownloadStorage", StringComparison.Ordinal));
                Assert.AreEqual(1, await page.Locator("main .navigation-tabs").CountAsync());
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "storage-cards-" + width);
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ProviderCard_ShowsTheKeyOnlyAsState_AndSwitchesWithoutReloading()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Providers");

                // A provider that needs a key says so until one is stored.
                var tmdb = page.Locator("#provider-tmdb");
                await Assertions.Expect(tmdb).ToContainTextAsync("Setup needed");
                await Assertions.Expect(tmdb).ToContainTextAsync("Missing");

                // The key field sits in the card itself: no Settings button, no dialog; typing shows the page's pill and the pill saves it.
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).CountAsync());
                var key = tmdb.Locator("[name=Secret]");
                await Assertions.Expect(key).ToBeVisibleAsync();
                await Assertions.Expect(key).ToHaveAttributeAsync("type", "password");
                await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
                await key.FillAsync(FakeServices.TmdbToken);
                await Assertions.Expect(page.Locator("[data-save-pill] .save-pill__count")).ToHaveTextAsync("1 change");
                await Steps.SaveAsync(page);
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Dialog).CountAsync());

                // The key is never written back: the card only says one is stored, and the field is empty again.
                tmdb = page.Locator("#provider-tmdb");
                await Assertions.Expect(tmdb).ToContainTextAsync("Saved");
                await Assertions.Expect(tmdb).Not.ToContainTextAsync("Setup needed");
                Assert.IsFalse((await page.ContentAsync()).Contains(FakeServices.TmdbToken, StringComparison.Ordinal), "The page shows the stored key.");
                await Assertions.Expect(tmdb.Locator("[name=Secret]")).ToHaveValueAsync("");
                await page.ReloadAsync();
                Assert.IsFalse((await page.ContentAsync()).Contains(FakeServices.TmdbToken, StringComparison.Ordinal), "The reloaded page shows the stored key.");
                await Assertions.Expect(page.Locator("#provider-tmdb")).ToContainTextAsync("Saved");

                // The switch swaps its card in place: no navigation, the state follows, and it stays after a reload.
                var navigations = 0;
                page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };
                var openLibrary = page.Locator("#provider-openlibrary");
                await Assertions.Expect(openLibrary).ToContainTextAsync("Enabled");
                await Steps.FollowAsync(page, openLibrary.GetByRole(AriaRole.Switch, new() { Name = "Provider status", Exact = true }));
                await Assertions.Expect(page.Locator("#provider-openlibrary")).ToContainTextAsync("Disabled");
                Assert.AreEqual(0, navigations);
                await page.ReloadAsync();
                await Assertions.Expect(page.Locator("#provider-openlibrary")).ToContainTextAsync("Disabled");
                await Assertions.Expect(page.Locator("#provider-openlibrary").GetByRole(AriaRole.Switch)).ToHaveAttributeAsync("aria-checked", "false");

                // One chip row joins the integrations: providers and the catalog; the media servers join it once one is set up.
                var chips = page.GetByRole(AriaRole.Navigation, new() { Name = "Integrations" });
                await Assertions.Expect(chips.GetByRole(AriaRole.Link)).ToHaveCountAsync(2);
                Assert.AreEqual(1, await page.Locator("main .navigation-tabs").CountAsync());
                await Evidence.SaveAsync(page, "provider-cards");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "provider-cards");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task MediaServers_AreCards_AndTheirUsersAreListedWithProfileAndState()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("""
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'ProgressSync';
                    UPDATE "Providers" SET "BaseUrl" = 'http://127.0.0.1:9/', "ExternalId" = 'jelly', "ProtectedSecret" = '\x0102', "SyncedAt" = now() WHERE "Key" = 'jellyfin';
                    INSERT INTO "ProviderProfileLinks" ("ProviderId", "ExternalUserId", "ExternalName", "ProfileId", "IsConfirmed", "SyncedAt")
                        SELECT provider."Id", 'u1', 'jules', (SELECT "Id" FROM "Profiles" ORDER BY "Id" LIMIT 1), TRUE, now() FROM "Providers" AS provider WHERE provider."Key" = 'jellyfin';
                    INSERT INTO "ProviderProfileLinks" ("ProviderId", "ExternalUserId", "ExternalName")
                        SELECT provider."Id", 'u2', 'guest' FROM "Providers" AS provider WHERE provider."Key" = 'jellyfin';
                    """);
                await page.GotoAsync("/Admin/Providers");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Navigation, new() { Name = "Integrations" }).GetByRole(AriaRole.Link, new() { Name = "Media servers" }));

                // Each server is a card; a connected one can be tested, one that is not shows what is missing.
                var jellyfin = page.Locator("#provider-jellyfin");
                await Assertions.Expect(jellyfin).ToContainTextAsync("Connected");
                await Assertions.Expect(jellyfin.GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("#provider-emby")).ToContainTextAsync("Setup needed");
                await Assertions.Expect(page.Locator("#provider-emby").GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true })).ToHaveCountAsync(0);

                // The connection is typed in the card (address and key side by side), never behind a button or dialog; the stored key is not echoed.
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).CountAsync());
                await Assertions.Expect(jellyfin.Locator("[name=BaseUrl]")).ToHaveValueAsync("http://127.0.0.1:9/");
                await Assertions.Expect(jellyfin.Locator("[name=ApiKey]")).ToHaveValueAsync("");
                await Assertions.Expect(jellyfin.Locator("[name=ApiKey]")).ToHaveAttributeAsync("type", "password");
                await Assertions.Expect(page.Locator("#provider-emby [name=BaseUrl]")).ToBeVisibleAsync();
                await page.Locator("#provider-emby [name=BaseUrl]").FillAsync("http://emby.test:8096");
                await Assertions.Expect(page.Locator("[data-save-pill] .save-pill__count")).ToHaveTextAsync("1 change");
                await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("#provider-emby [name=BaseUrl]")).ToHaveValueAsync("");

                // The users of the server: who is linked, who still waits for a profile.
                var rows = page.Locator(".providers-links .list-row");
                await Assertions.Expect(rows).ToHaveCountAsync(2);
                await Assertions.Expect(rows.Filter(new() { HasText = "jules" })).ToContainTextAsync("Linked");
                await Assertions.Expect(rows.Filter(new() { HasText = "guest" })).ToContainTextAsync("Not linked");
                await Assertions.Expect(rows.Filter(new() { HasText = "guest" }).Locator("select")).ToBeVisibleAsync();
                Assert.AreEqual(1, await page.Locator("main .navigation-tabs").CountAsync());
                await Evidence.SaveAsync(page, "media-servers");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "media-servers");
                throw;
            }
        }
    }
}
