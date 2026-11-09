using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The three dots of a card in the home page's continue row: what they offer, that an entry works at once without leaving the page, and that they fit a phone.
    [TestClass]
    public sealed class ContinueMenuTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task ContinueRow_ThreeDotsMenu_RemovesMarksAndFavoritesFromTheCard_AtEveryWidth()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Three films with a file, each begun by the profile.
            await jularr.RunSqlAsync("""
                DO $$
                DECLARE pid bigint; mount bigint; root bigint; ed bigint; ver bigint; asset bigint; n integer;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Menu', 3, 'C:\menu', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Menu films', 1, mount, 'films') RETURNING "Id" INTO root;
                    FOR n IN 1..3 LOOP
                        INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (7100 + n, 1, 'Menu Film ' || n, 2020 + n);
                        INSERT INTO "WorkEditions" ("WorkId", "LanguageCode") VALUES (7100 + n, 'en') RETURNING "Id" INTO ed;
                        INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (7100 + n, ed) RETURNING "Id" INTO ver;
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (7100 + n, ver, 1) RETURNING "Id" INTO asset;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "QualityKey")
                        VALUES (root, 'films/' || n || '.mkv', 1000, now(), asset, now(), now(), 'matroska', 6000000, 'WEBDL-1080p');
                        INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "UpdatedAt")
                        VALUES (pid, 7100 + n, 1, 600000, 6000000, now() - n * interval '1 minute');
                    END LOOP;
                END $$;
                """);

            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/");
            var shelf = page.Locator(".media-shelf", new() { Has = page.Locator(".media-card--resume") });
            var cards = shelf.Locator(".media-card--menu");
            await Assertions.Expect(cards).ToHaveCountAsync(3);
            var dots = (int index) => cards.Nth(index).Locator("summary[aria-label='More actions for this title']");
            await Assertions.Expect(dots(0)).ToBeVisibleAsync();

            // The entries, as the menu of Plex has them.
            await dots(0).ClickAsync();
            var panel = cards.Nth(0).Locator(".action-menu__panel");
            await Assertions.Expect(panel).ToBeVisibleAsync();
            await Assertions.Expect(panel.Locator(".action-menu__item")).ToHaveTextAsync(new[] { "Remove from Continue", "Mark watched", "Mark unwatched", "Add to favorites", "Open details" });

            // The keyboard walks the entries; Escape closes and keeps the focus on the button.
            await page.Keyboard.PressAsync("ArrowDown");
            await Assertions.Expect(panel.Locator(".action-menu__item").First).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(panel).ToBeHiddenAsync();
            await Assertions.Expect(dots(0)).ToBeFocusedAsync();

            // The open menu as a picture (a full-page capture resizes the screen, so it comes after the keyboard checks).
            await dots(0).ClickAsync();
            await Assertions.Expect(panel).ToBeVisibleAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", "continue-menu-desktop.png") });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(panel).ToBeHiddenAsync();

            // Favorites flip in place: the card stays and the entry now takes it out.
            await dots(0).ClickAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Add to favorites" }).ClickAsync();
            await Assertions.Expect(cards.Nth(0).Locator("form[data-card-menu=favorite]")).ToHaveAttributeAsync("data-state", "true");
            await Assertions.Expect(cards).ToHaveCountAsync(3);
            await dots(0).ClickAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Remove from favorites" })).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");

            // Removed from the row: the card is gone at once, nothing reloads, the progress stays and the title returns when it is played again.
            var navigations = 0;
            page.FrameNavigated += (_, _) => navigations++;
            await dots(0).ClickAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "Remove from Continue" }).ClickAsync();
            await Assertions.Expect(cards).ToHaveCountAsync(2);
            Assert.AreEqual(0, navigations, "Removing a card reloaded the page.");
            await page.ReloadAsync();
            await Assertions.Expect(shelf.Locator(".media-card--menu")).ToHaveCountAsync(2);
            Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" = 7101 AND \"PositionMs\" = 600000 AND NOT \"IsCompleted\""));

            // Watched leaves the row as well and shows on the title's page.
            await dots(0).ClickAsync();
            await cards.Nth(0).Locator(".action-menu__panel").GetByRole(AriaRole.Button, new() { Name = "Mark watched" }).ClickAsync();
            await Assertions.Expect(cards).ToHaveCountAsync(1);
            Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"MediaProgress\" WHERE \"WorkId\" = 7102 AND \"IsCompleted\""));

            // The panel stays inside the screen on a tablet and a phone, and the last card's menu opens at the screen's edge.
            foreach (var (width, height, name) in new[] { (768, 1024, "tablet"), (375, 812, "phone") })
            {
                await page.SetViewportSizeAsync(width, height);
                await page.WaitForTimeoutAsync(400);
                await page.ReloadAsync();
                var only = page.Locator(".media-shelf .media-card--menu").First;
                await only.Locator("summary").ClickAsync();
                var box = (await only.Locator(".action-menu__panel").BoundingBoxAsync())!;
                Assert.IsTrue(box.X >= 0 && box.X + box.Width <= width + .5 && box.Y >= 0 && box.Y + box.Height <= height + .5, name + " panel leaves the screen: " + box.X + "," + box.Y + " " + box.Width + "x" + box.Height);
                var button = (await only.Locator("summary").BoundingBoxAsync())!;
                Assert.IsTrue(name == "tablet" || (button.Width >= 40 && button.Height >= 40), "The dots are too small for a finger: " + button.Width + "x" + button.Height);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), name + " scrolls sideways.");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", "continue-menu-" + name + ".png") });
                await page.Keyboard.PressAsync("Escape");
            }
        }
    }
}
