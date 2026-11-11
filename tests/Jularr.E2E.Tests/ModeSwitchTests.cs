using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The five modes of the user area: the switch under the brand, that every page shows only the active mode, that the mode is kept, and the search chip that widens the search.
    [TestClass]
    public sealed class ModeSwitchTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Modes_SwitchAtTheBrand_NarrowEveryPage_AreKept_AndTheSearchCanWidenToAllModes()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // One title of each kind, all favorites of the profile so that they belong to its library.
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'anilist');
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "DetailedAt", "CatalogNamespace", "ImageUrl") VALUES
                    (7201, 1, NULL, 'Dune Film', 2021, now(), 'tmdb_movie', 'https://example.test/a.jpg'), (7202, 2, NULL, 'Dune Series', 2022, now(), 'tvdb', 'https://example.test/b.jpg'),
                    (7203, 1, NULL, 'Dune Remake', 2023, now(), 'tmdb_movie', 'https://example.test/c.jpg'), (7204, 3, NULL, 'Dune Book', 1965, now(), 'openlibrary', 'https://example.test/d.jpg'),
                    (7205, 3, 2, 'Dune Manga', 2020, now(), 'anilist', 'https://example.test/e.jpg'), (7206, 7, NULL, 'Dune Game', 2001, now(), 'igdb', 'https://example.test/f.jpg'),
                    (7207, 6, NULL, 'Dune Album', 2019, now(), 'musicbrainz', 'https://example.test/g.jpg');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (7201, 'tmdb_movie', '7201'), (7203, 'tmdb_movie', '7203'), (7205, 'anilist', '7205');
                DO $$
                DECLARE mount bigint; root bigint; ed bigint; ver bigint; asset bigint; item record;
                BEGIN
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Modes', 3, 'C:\modes', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Modes', 1, mount, 'modes') RETURNING "Id" INTO root;
                    FOR item IN SELECT * FROM (VALUES (7201, 1, 'film.mkv'), (7204, 3, 'book.epub')) AS files ("WorkId", "AssetType", "Name") LOOP
                        INSERT INTO "WorkEditions" ("WorkId", "LanguageCode") VALUES (item."WorkId", 'en') RETURNING "Id" INTO ed;
                        INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (item."WorkId", ed) RETURNING "Id" INTO ver;
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (item."WorkId", ver, item."AssetType") RETURNING "Id" INTO asset;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt") VALUES (root, 'modes/' || item."Name", 1000, now(), asset, now());
                    END LOOP;
                END $$;
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT (SELECT "Id" FROM "Profiles" ORDER BY "Id" LIMIT 1), id FROM generate_series(7201, 7207) AS id;
                """);

            // Schauen is the first mode: films and series only, in the library and the favorites.
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/Library");
            var switchButton = page.Locator(".mode-switch__button");
            await Assertions.Expect(switchButton).ToContainTextAsync("Watch");
            var titles = page.Locator(".media-card__title");

            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Film" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Book" })).ToHaveCountAsync(0);

            await page.GotoAsync("/Favorites");
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Remake" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Series" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Album" })).ToHaveCountAsync(0);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Game" })).ToHaveCountAsync(0);

            // The switch opens the list of the five modes; a choice leads home in that mode.
            await page.GotoAsync("/Library");
            await switchButton.ClickAsync();
            await Assertions.Expect(page.Locator(".mode-switch__item")).ToHaveTextAsync(new[] { "Watch", "Anime and manga", "Read", "Listen", "Play" });
            await Evidence.SaveAsync(page, "mode-switch-desktop");
            await page.Locator(".mode-switch__item", new() { HasText = "Read" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/$"));
            await Assertions.Expect(page.Locator(".mode-switch__button")).ToContainTextAsync("Read");

            // The mode stays on every page and after a reload; Anime and manga holds the manga, not the book.
            await page.GotoAsync("/Library");
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Book" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Film" })).ToHaveCountAsync(0);
            await page.GotoAsync("/Favorites");
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Book" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Manga" })).ToHaveCountAsync(0);
            await page.GotoAsync("/?mode=animanga");
            await page.GotoAsync("/Favorites");
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Manga" })).ToHaveCountAsync(1);
            await Assertions.Expect(titles.Filter(new() { HasText = "Dune Book" })).ToHaveCountAsync(0);

            // Listen says that audiobooks are coming; the media-type bar of Discover shows only the mode's types.
            await page.GotoAsync("/?mode=hear");
            await Assertions.Expect(page.Locator(".discover-mode-note")).ToContainTextAsync("Audiobooks are coming soon");
            await page.GotoAsync("/?mode=watch");
            await Assertions.Expect(page.Locator(".capsule-bar").GetByText("Books")).ToHaveCountAsync(0);

            // The search stays in the mode; the chip widens it to every mode.
            var field = page.Locator(".app-search .search-box__input");
            await field.FillAsync("Dune");
            var results = page.Locator(".app-search__results");
            await Assertions.Expect(results.GetByText("Dune Film")).ToBeVisibleAsync();
            await Assertions.Expect(results.GetByText("Dune Manga")).ToHaveCountAsync(0);
            await Evidence.SaveAsync(page, "mode-search-in-mode");
            await page.Locator(".app-search__scope").ClickAsync();
            await Assertions.Expect(results.GetByText("Dune Manga")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".app-search__scope")).ToContainTextAsync("Everywhere");
            await Evidence.SaveAsync(page, "mode-search-everywhere");
            await field.PressAsync("Enter");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("scope=all"));

            // The same switch on a tablet (icons) and a phone (the drawer); nothing scrolls sideways.
            foreach (var (width, height, name) in new[] { (768, 1024, "tablet"), (375, 812, "phone") })
            {
                await page.GotoAsync("/?mode=read");
                await page.SetViewportSizeAsync(width, height);
                await page.WaitForTimeoutAsync(400);
                if (name == "phone") await page.Locator("[data-sidebar-toggle]").ClickAsync();
                await page.Locator(".mode-switch__button").ClickAsync();
                await Assertions.Expect(page.Locator(".mode-switch__item").Last).ToBeVisibleAsync();
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), name + " scrolls sideways.");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", "mode-switch-" + name + ".png") });
            }
        }
    }
}
