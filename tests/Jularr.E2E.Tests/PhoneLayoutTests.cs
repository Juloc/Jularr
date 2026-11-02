using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Phone (375px) layout defects measured live: the carousel arrows over the title, the relation dialog's cut-off search button and the two same-looking indexer dock actions.
    [TestClass]
    public sealed class PhoneLayoutTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Phone_CarouselArrowsStayOffTheTitle_RelationDialogFitsItsButton_IndexerDockActionsDiffer()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES
                    (9603, 'tmdb_movie', 1, 'The Matrix', '1999-03-31', 'https://127.0.0.1/603.jpg', 80, 8.7, 25000, 1),
                    (9604, 'tmdb_movie', 1, 'The Matrix Reloaded', '2003-05-15', 'https://127.0.0.1/604.jpg', 60, 7.0, 10000, 2);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (9603, 'tmdb_movie', '603'), (9604, 'tmdb_movie', '604');
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (8001, 1, 'Source Film', 2001);
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (8001, 9604, 1, 'tmdb');
                """);
            await page.SetViewportSizeAsync(375, 812);

            // The title never sits under the arrows, and the carousel's own controls are at least 36px.
            await page.GotoAsync("/");
            var title = page.Locator(".hero-carousel__slide:not([hidden]) .hero-carousel__title");
            await Assertions.Expect(title).ToHaveCountAsync(1);
            var titleBox = (await title.BoundingBoxAsync())!;
            foreach (var control in new[] { ".hero-carousel__arrow--previous", ".hero-carousel__arrow--next", ".hero-carousel__pause" })
            {
                var box = (await page.Locator(control).BoundingBoxAsync())!;
                Assert.IsTrue(box.Width >= 36 && box.Height >= 36, $"{control} is {box.Width}x{box.Height}, below the 36px hit area.");
                if (control.Contains("arrow", StringComparison.Ordinal))
                {
                    var apart = box.X + box.Width <= titleBox.X || titleBox.X + titleBox.Width <= box.X || box.Y + box.Height <= titleBox.Y || titleBox.Y + titleBox.Height <= box.Y;
                    Assert.IsTrue(apart, $"{control} ({box.X},{box.Y},{box.Width},{box.Height}) overlaps the title ({titleBox.X},{titleBox.Y},{titleBox.Width},{titleBox.Height}).");
                }
            }

            // The relation dialog's search button lies inside the dialog box, with room below it.
            await page.GotoAsync("/Details/8001?tab=related");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add relation" }).ClickAsync();
            var dialog = page.Locator("dialog#details-relation-add");
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            var dialogBox = (await dialog.BoundingBoxAsync())!;
            var searchBox = (await dialog.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).BoundingBoxAsync())!;
            Assert.IsTrue(searchBox.Y + searchBox.Height <= dialogBox.Y + dialogBox.Height - 8, $"The search button ends at {searchBox.Y + searchBox.Height}, the dialog at {dialogBox.Y + dialogBox.Height}.");

            // The two indexer actions are told apart by name and by what a phone shows of them: their icon.
            await page.GotoAsync("/Admin/Acquisition");
            var dock = page.Locator(".action-dock .action-dock__actions");
            var indexer = dock.Locator("a[href*='Type=Newznab']");
            var nyaa = dock.Locator("a[href*='Type=Nyaa']");
            await Assertions.Expect(indexer).ToHaveCountAsync(1);
            await Assertions.Expect(nyaa).ToHaveCountAsync(1);
            Assert.AreNotEqual((await indexer.TextContentAsync())!.Trim(), (await nyaa.TextContentAsync())!.Trim(), "Both indexer actions have the same accessible name.");
            Assert.AreNotEqual(await indexer.Locator("use").GetAttributeAsync("href"), await nyaa.Locator("use").GetAttributeAsync("href"), "Both indexer actions show the same icon on a phone.");
        }
    }
}
