using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Library, Favorites and Discover share the media-type bar, the filter panel, the order and the layout.
    [TestClass]
    public sealed class ListFilterTests
    {
        private const string Seed = """
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES
                (9201, 1, 'Old Film', 1999), (9202, 1, 'New Film', 2022), (9203, 2, 'New Series', 2023), (9204, 3, 'Old Book', 2001);
            INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works" WHERE "Id" BETWEEN 9201 AND 9204;
            -- The Library defaults to locally available titles. A Wanted item alone
            -- is intentionally not available; seed real inventory associations.
            INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly")
                VALUES ('E2E Filter Storage', 3, '/tmp/jularr-listfilter', FALSE);
            INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath")
            SELECT kind."Name", kind."MediaTypeId", mount."Id", kind."Path"
            FROM "StorageMounts" AS mount
            CROSS JOIN (VALUES ('Filter Movies', 1, 'movies'), ('Filter Series', 2, 'series'), ('Filter Books', 3, 'books'))
                AS kind("Name", "MediaTypeId", "Path")
            WHERE mount."Name" = 'E2E Filter Storage';
            INSERT INTO "WorkEditions" ("Id", "WorkId")
                SELECT "Id", "Id" FROM "Works" WHERE "Id" BETWEEN 9201 AND 9204;
            INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId")
                SELECT "Id", "Id", "Id" FROM "Works" WHERE "Id" BETWEEN 9201 AND 9204;
            INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId")
                SELECT "Id", "Id", "Id", CASE WHEN "MediaTypeId" = 3 THEN 3 ELSE 1 END
                FROM "Works" WHERE "Id" BETWEEN 9201 AND 9204;
            INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt")
            SELECT root."Id",
                'fixtures/' || work."Id" || CASE WHEN work."MediaTypeId" = 3 THEN '.epub' ELSE '.mkv' END,
                1024, now(), asset."Id", now()
            FROM "Works" AS work
            JOIN "MediaAssets" AS asset ON asset."WorkId" = work."Id"
            JOIN "LibraryRoots" AS root ON root."MediaTypeId" = work."MediaTypeId"
            JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId" AND mount."Name" = 'E2E Filter Storage'
            WHERE work."Id" BETWEEN 9201 AND 9204;
            INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT p."Id", w FROM "Profiles" AS p CROSS JOIN (VALUES (9201), (9202), (9204)) AS v (w);
            INSERT INTO "Collections" ("ProfileId", "Name") SELECT "Id", 'Weekend' FROM "Profiles";
            INSERT INTO "CollectionWorks" ("CollectionId", "WorkId", "Position") SELECT c."Id", 9202, 1 FROM "Collections" AS c;
            """;

        [TestMethod]
        [Timeout(300_000)]
        public async Task Library_HasTheSameBarsAsFavorites_NarrowsOrdersAndChangesLayout()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            // Seed already creates editions, versions, assets, and stored files.
            // Calling SeedLibraryFilesAsync again violates UX_WorkEditions_Name.
            await jularr.RunSqlAsync(Seed);

            try
            {
                // The one media-type bar, with the filter at its end, the order and the layout beside the search.
                await page.GotoAsync("/Library");
                var bar = page.Locator(".capsule-bar");
                await Assertions.Expect(bar).ToHaveCountAsync(1);
                await Assertions.Expect(bar.Locator(".capsule-bar__all--on")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".media-card")).ToHaveCountAsync(4);
                await Assertions.Expect(page.Locator("button.choice-select__control")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-layout-toggle]")).ToBeVisibleAsync();
                var libraryBar = await bar.Locator(".capsule-bar__areas").InnerTextAsync();

                // Types are chosen one by one, several at once; the choice is in the address.
                await bar.Locator(".capsule-group__item", new() { HasText = "Books" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=books"));
                await Assertions.Expect(page.Locator(".media-card")).ToHaveCountAsync(1);
                await bar.Locator(".capsule-group__item", new() { HasText = "Movies" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=movies,books"));
                await Assertions.Expect(page.Locator(".media-card")).ToHaveCountAsync(3);

                // The filter panel keeps the types and narrows by release year; the filter button counts the choice.
                await page.Locator("button[data-drawer-open=library-filter]").ClickAsync();
                var drawer = page.Locator("dialog#library-filter");
                await Assertions.Expect(drawer.Locator("input[name=type][value=movies]")).ToBeCheckedAsync();
                await Assertions.Expect(drawer.Locator("input[name=type][value=series]")).Not.ToBeCheckedAsync();
                await drawer.GetByText("Since 2020", new() { Exact = true }).ClickAsync();
                await Steps.FollowAsync(page, drawer.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true }));
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("year=2020"));
                await Assertions.Expect(page.Locator(".media-card")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".media-card__title")).ToContainTextAsync("New Film");
                await Assertions.Expect(page.Locator("button[data-drawer-open=library-filter] .capsule-bar__badge")).ToHaveTextAsync("1");

                // The order and the layout stay with the choices; the layout is the device's and rows replace the grid.
                await page.GotoAsync("/Library");
                await page.Locator("select.choice-select__control").SelectOptionAsync("Year");
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("sort=Year"));
                await Assertions.Expect(page.Locator(".media-card__title").First).ToContainTextAsync("New Series");
                await page.Locator("[data-layout-toggle]").ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-list-layout", "list");
                await Assertions.Expect(page.Locator(".library-grid .media-card").First).ToHaveCSSAsync("flex-direction", "row");
                await Evidence.SaveAsync(page, "library-list");
                await page.Locator("[data-layout-toggle]").ClickAsync();
                await Assertions.Expect(page.Locator(".library-grid .media-card").First).ToHaveCSSAsync("flex-direction", "column");

                // Favorites shows the same bar and the same type labels.
                await page.GotoAsync("/Favorites");
                await Assertions.Expect(page.Locator(".capsule-bar")).ToHaveCountAsync(1);
                Assert.AreEqual(libraryBar, await page.Locator(".capsule-bar .capsule-bar__areas").InnerTextAsync());
                await Assertions.Expect(page.Locator("[data-layout-toggle]")).ToBeVisibleAsync();

                // Only the list that is shown is purple: all favorites, or the collection, never both.
                var all = page.Locator(".favorites-collection__card", new() { HasText = "All favorites" });
                var weekend = page.Locator(".favorites-collection__card", new() { HasText = "Weekend" });
                await Assertions.Expect(all).ToHaveClassAsync(new Regex("favorites-collection__card--current"));
                await Assertions.Expect(all).ToHaveCSSAsync("background-image", new Regex("linear-gradient"));
                await Assertions.Expect(weekend).ToHaveCSSAsync("background-image", "none");
                await Steps.FollowAsync(page, weekend);
                await Assertions.Expect(weekend).ToHaveCSSAsync("background-image", new Regex("linear-gradient"));
                await Assertions.Expect(all).ToHaveCSSAsync("background-image", "none");
                await Assertions.Expect(all).Not.ToHaveClassAsync(new Regex("favorites-collection__card--current"));
                await Evidence.SaveAsync(page, "favorites-collection-selected");
            }
            catch
            {
                await Evidence.SaveAsync(page, "list-filter-failed");
                throw;
            }
        }
    }
}
