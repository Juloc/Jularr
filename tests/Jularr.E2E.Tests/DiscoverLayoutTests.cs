using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover's media-type bar, highlights, filter and layout, from catalogs read whole.
    [TestClass]
    public sealed class DiscoverLayoutTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Discover_ChoosesTypesInTheBar_ShowsHighlights_AndFiltersInTheDrawer()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'tmdb_tv');
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES
                    ('tmdb_movie', '603', 1, 'The Matrix', '1999-03-31', 'https://127.0.0.1/603.jpg', 80, 8.7, 25000, 1),
                    ('tmdb_movie', '604', 1, 'The Matrix Reloaded', '2003-05-15', 'https://127.0.0.1/604.jpg', 60, 7.0, 10000, 2),
                    ('tmdb_tv', '1396', 2, 'Breaking Bad', '2008-01-20', 'https://127.0.0.1/1396.jpg', 90, 9.0, 20000, 1),
                    ('tmdb_tv', '1399', 2, 'Game of Thrones', '2011-04-17', 'https://127.0.0.1/1399.jpg', 70, 8.4, 30000, 2);
                """);

            try
            {
                // The highlights come first, one at a time, and move on with the arrows.
                await page.GotoAsync("/");
                var shown = page.Locator(".hero-carousel__slide:not([hidden]) .hero-carousel__title");
                await Assertions.Expect(shown).ToHaveCountAsync(1);
                var first = await shown.InnerTextAsync();
                await page.Locator(".hero-carousel__arrow--next").ClickAsync();
                await Assertions.Expect(shown).Not.ToHaveTextAsync(first);
                await Assertions.Expect(page.Locator(".hero-carousel__counter")).ToContainTextAsync("2 /");

                // A type of the bar narrows the page to it; the choice is in the address.
                await page.Locator(".capsule-group__item", new() { HasText = "Series" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=series"));
                await Assertions.Expect(page.Locator(".media-shelf").GetByText("Breaking Bad").First).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByText("The Matrix Reloaded")).ToHaveCountAsync(0);

                // Another type is added; the filter drawer shows the choice and applies its availability.
                await page.Locator(".capsule-group__item", new() { HasText = "Movies" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=movies,series"));
                await page.Locator("button[data-drawer-pane=filter]").ClickAsync();
                var drawer = page.Locator("#discover-panel");
                await Assertions.Expect(drawer).ToBeVisibleAsync();
                await Assertions.Expect(drawer.Locator("input[name=type][value=movies]")).ToBeCheckedAsync();
                await Assertions.Expect(drawer.Locator("input[name=type][value=anime]")).Not.ToBeCheckedAsync();
                await drawer.Locator(".choice-group__item", new() { HasText = "Can be requested" }).ClickAsync();
                await Evidence.SaveAsync(page, "discover-filter");
                await drawer.GetByRole(AriaRole.Button, new() { Name = "Apply & close" }).ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("availability=request"));
                await Assertions.Expect(page.Locator("button[data-drawer-pane=filter] .capsule-bar__badge")).ToHaveTextAsync("1");

                // The sort reorders every list; the layout toggle wraps the shelves and is remembered on the device.
                await page.Locator("select.choice-select__control").SelectOptionAsync("az");
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("sort=az"));
                await page.Locator("[data-discover-layout]").ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-discover-layout", "grid");

                // One highlight per type leaves one slide for each of the two types.
                await page.Locator("button[data-drawer-pane=view]").ClickAsync();
                await page.Locator("#discover-panel .choice-group__item", new() { HasText = "1 title" }).ClickAsync();
                await Assertions.Expect(page.Locator(".hero-carousel__counter")).ToHaveTextAsync("1 / 2");
                await Evidence.SaveAsync(page, "discover-layout");
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-layout-failed");
                throw;
            }
        }
    }
}
