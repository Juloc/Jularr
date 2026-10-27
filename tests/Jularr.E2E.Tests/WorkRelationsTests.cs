using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class WorkRelationsTests
    {
        // A Work with a sequel in the library and an adaptation that is only a catalog title: both rows open their Work, the second says it is not in the library and offers the request.
        // The Owner adds and removes a relation without the page reloading; the other Work shows the inverse.
        [TestMethod]
        [Timeout(300_000)]
        public async Task Relations_LibraryRowAndCatalogRow_AddRemoveAndInverse_WorkOnTheTitlesPage()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "ImageUrl", "CatalogNamespace") VALUES
                    (8001, 1, 'Source Film', 2001, NULL, NULL),
                    (8002, 1, 'Linked Sequel', 2004, 'https://image.tmdb.org/t/p/w342/linked.jpg', 'tmdb_movie'),
                    (8101, 1, 'Outside Remake', 2020, NULL, 'tmdb_movie'),
                    (8102, 1, 'Third Film', 2010, 'https://image.tmdb.org/t/p/w342/third.jpg', 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (8002, 'tmdb_movie', '557'), (8101, 'tmdb_movie', '555'), (8102, 'tmdb_movie', '556');
                INSERT INTO "WantedItems" ("WorkId", "IsPaused") VALUES (8002, TRUE);
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES
                    (8001, 8002, 1, 'tmdb'),
                    (8001, 8101, 8, 'anilist');
                """);

            // The Overview previews the block; its own tab lists both rows with the relation, its type and what the library has.
            await page.GotoAsync("/Details/8001");
            await Assertions.Expect(page.Locator(".details-block__heading", new() { HasText = "Related works" })).ToBeVisibleAsync();
            await page.Locator(".details-tabs .navigation-tabs__link", new() { HasText = "Related works" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("tab=related"));
            var rows = page.Locator(".relation-list .list-row");
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            var work = rows.Filter(new() { HasText = "Linked Sequel" });
            var catalog = rows.Filter(new() { HasText = "Outside Remake" });
            await Assertions.Expect(work.Locator(".state-chip").First).ToContainTextAsync("Sequel");
            await Assertions.Expect(work.Locator(".relation__title")).ToHaveAttributeAsync("href", "/Details/8002");
            await Assertions.Expect(catalog.Locator(".state-chip").First).ToContainTextAsync("Alternative version");
            await Assertions.Expect(catalog.Locator(".state-chip").First).ToHaveAttributeAsync("title", "Source: AniList");
            await Assertions.Expect(catalog.Locator(".state-chip", new() { HasText = "Not in library" }).First).ToBeVisibleAsync();
            await Assertions.Expect(catalog.Locator(".relation__title")).ToHaveAttributeAsync("href", "/Details/8101");
            await Assertions.Expect(catalog.GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(work.GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true })).ToHaveCountAsync(0);
            await Evidence.SaveAsync(page, "relations-list");

            // A click anywhere on the Work's row opens the Work.
            var box = (await work.BoundingBoxAsync())!;
            await page.Mouse.ClickAsync((float)(box.X + box.Width * 0.55), box.Y + box.Height / 2);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Details/8002"));

            // The other Work shows the inverse by itself, and it opens the first.
            await page.GotoAsync("/Details/8002?tab=related");
            var inverse = page.Locator(".relation-list .list-row");
            await Assertions.Expect(inverse).ToHaveCountAsync(1);
            await Assertions.Expect(inverse.Locator(".state-chip").First).ToContainTextAsync("Prequel");
            await Assertions.Expect(inverse.Locator(".relation__title")).ToHaveAttributeAsync("href", "/Details/8001");

            // The Owner adds a catalog title without the page reloading: type, words, match.
            await page.GotoAsync("/Details/8001?tab=related");
            await page.EvaluateAsync("window.__kept = 'same page'");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add relation" }).ClickAsync();
            var dialog = page.Locator("#details-relation-add");
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await dialog.Locator("select").SelectOptionAsync(new SelectOptionValue { Label = "Side story" });
            await dialog.GetByLabel("Search a title").FillAsync("Third");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
            var matches = page.Locator(".relation-results .list-row");
            await Assertions.Expect(matches).ToHaveCountAsync(1);
            await Assertions.Expect(matches.First).ToContainTextAsync("Third Film");
            await matches.First.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(3);
            await Assertions.Expect(rows.Filter(new() { HasText = "Third Film" }).Locator(".state-chip").First).ToContainTextAsync("Side story");
            await Assertions.Expect(rows.Filter(new() { HasText = "Third Film" }).Locator(".state-chip").First).ToHaveAttributeAsync("title", "Source: Added by hand");
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            Assert.AreEqual("same page", await page.EvaluateAsync<string>("window.__kept"));

            // The same title and type again is refused; the list stays as it is.
            await page.GetByRole(AriaRole.Button, new() { Name = "Add relation" }).ClickAsync();
            await dialog.Locator("select").SelectOptionAsync(new SelectOptionValue { Label = "Side story" });
            await dialog.GetByLabel("Search a title").FillAsync("Third");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
            await page.Locator(".relation-results .list-row").First.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
            await Assertions.Expect(page.Locator("[role=alert]").First).ToBeVisibleAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(3);

            // Removing a relation takes the row away at once and for good: the provider's relation stays hidden.
            await page.GotoAsync("/Details/8001?tab=related");
            await page.EvaluateAsync("window.__kept = 'same page'");
            await rows.Filter(new() { HasText = "Outside Remake" }).GetByRole(AriaRole.Button, new() { Name = "Remove relation" }).ClickAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            Assert.AreEqual("same page", await page.EvaluateAsync<string>("window.__kept"));
            await page.ReloadAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(2);
            await Assertions.Expect(rows.Filter(new() { HasText = "Outside Remake" })).ToHaveCountAsync(0);
            Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"WorkRelations\" WHERE \"SourceKey\" = 'anilist' AND \"IsSuppressed\""));

            // A Work without relations has no tab for them; the Owner starts from its menu, and the tab appears with the first one.
            await jularr.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\", \"ReleaseYear\") VALUES (8003, 1, 'Lonely Film', 2005)");
            await page.GotoAsync("/Details/8003");
            await Assertions.Expect(page.Locator(".details-tabs .navigation-tabs__link", new() { HasText = "Related works" })).ToHaveCountAsync(0);
            await Evidence.SaveAsync(page, "relations-empty-work");
            await page.Locator("summary[aria-label='More']").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Add relation" }).ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await dialog.Locator("select").SelectOptionAsync(new SelectOptionValue { Label = "Sequel" });
            await dialog.GetByLabel("Search a title").FillAsync("Linked");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
            await page.Locator(".relation-results .list-row", new() { HasText = "Linked Sequel" }).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
            await Assertions.Expect(page.Locator(".relation-list .list-row")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".relation-list .relation__title")).ToHaveAttributeAsync("href", "/Details/8002");
        }

        // A related catalog title is requested from its row with the Work's normal request: the page stays (no navigation), a hint says so and the row shows the Work's state.
        // Without the Requests module the row has no button.
        [TestMethod]
        [Timeout(300_000)]
        public async Task Relations_RequestFromTheRow_MakesTheRequestWithoutLeavingThePage()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await Steps.SetTmdbKeyAsync(page);
            await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));

            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (8201, 1, 'Source Film', 2001, NULL), (8301, 1, 'Big Buck Bunny', 2008, 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (8301, 'tmdb_movie', '10378');
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (8201, 8301, 1, 'tmdb');
                """);
            await page.GotoAsync("/Details/8201?tab=related");
            await page.EvaluateAsync("window.__kept = 'same page'");
            var row = page.Locator(".relation-list .list-row").Filter(new() { HasText = "Big Buck Bunny" });
            await Assertions.Expect(row.Locator(".state-chip", new() { HasText = "Not in library" }).First).ToBeVisibleAsync();
            await Evidence.SaveAsync(page, "relation-request-before");
            var url = page.Url;

            await row.GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true }).ClickAsync();

            // The row swaps in place: no button, the title opens the new Work, and the quiet hint confirms it.
            await Assertions.Expect(page.Locator("[data-action-toast] .action-toast__text")).ToHaveTextAsync("Requested");
            await Assertions.Expect(row.GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true })).ToHaveCountAsync(0);
            await Assertions.Expect(row.Locator(".relation__title")).ToHaveAttributeAsync("href", "/Details/8301");
            await Assertions.Expect(row.Locator(".state-chip", new() { HasText = "Not in library" })).ToHaveCountAsync(0);
            Assert.AreEqual(url, page.Url);
            Assert.AreEqual("same page", await page.EvaluateAsync<string>("window.__kept"));
            Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = 8301"));
            Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '10378'"));
            await Evidence.SaveAsync(page, "relation-request-after");

            // Requests switched off: the row offers nothing to request.
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (8302, 1, 'Third Film', 2010, 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (8302, 'tmdb_movie', '556');
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (8201, 8302, 3, 'tmdb');
                """);
            await page.GotoAsync("/Details/8201?tab=related");
            await Assertions.Expect(page.Locator(".relation-list .list-row").Filter(new() { HasText = "Third Film" }).GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true })).ToBeVisibleAsync();
            await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Requests'");
            await page.GotoAsync("/Details/8201?tab=related");
            await Assertions.Expect(page.Locator(".relation-list .list-row")).ToHaveCountAsync(2);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Request", Exact = true })).ToHaveCountAsync(0);
        }
    }
}
