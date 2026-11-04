using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Discover's media-type bar, highlights, filter and layout, from a film catalog read whole and the series TMDB's trending list last named.
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
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank") VALUES
                    (9603, 'tmdb_movie', 1, 'The Matrix', '1999-03-31', 'https://127.0.0.1/603.jpg', 80, 8.7, 25000, 1),
                    (9604, 'tmdb_movie', 1, 'The Matrix Reloaded', '2003-05-15', 'https://127.0.0.1/604.jpg', 60, 7.0, 10000, 2),
                    (9396, 'tvdb', 2, 'Breaking Bad', '2008-01-20', 'https://127.0.0.1/1396.jpg', 90, 9.0, 20000, 1),
                    (9399, 'tvdb', 2, 'Game of Thrones', '2011-04-17', 'https://127.0.0.1/1399.jpg', 70, 8.4, 30000, 2);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES
                    (9603, 'tmdb_movie', '603'), (9604, 'tmdb_movie', '604'), (9396, 'tvdb', '81189'), (9399, 'tvdb', '121361');
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

                // Shelves keep their lists' order, only a ranking's grid sorts; the layout toggle wraps the shelves and is remembered on the device.
                await Assertions.Expect(page.Locator("select[name=sort]")).ToHaveCountAsync(0);
                await page.Locator("button[data-discover-layout]").ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-discover-layout", "grid");

                // One highlight per type leaves one slide for each of the two types: the films read whole, the series from Discover's series list.
                await page.Locator("button[data-drawer-pane=view]").ClickAsync();
                await page.Locator("#discover-panel .choice-group__item", new() { HasTextRegex = new Regex("^1$") }).ClickAsync();
                await Assertions.Expect(page.Locator(".hero-carousel__counter")).ToHaveTextAsync("1 / 2");
                await Evidence.SaveAsync(page, "discover-layout");
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-layout-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ShelfHeadings_KeepTheirTitlesClearOfTheLink_AtEveryPhoneWidth()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            // German has Discover's longest shelf titles, such as "Neuerscheinungen", and "Alle anzeigen".
            await jularr.RunSqlAsync("""
                UPDATE "Profiles" SET "UiLocaleId" = (SELECT "Id" FROM "UiLocales" WHERE "Locale" LIKE 'de%' ORDER BY "Locale" LIMIT 1);
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank")
                SELECT 21000 + rank, 'tmdb_movie', 1, 'Film ' || rank, current_date - rank, 'https://127.0.0.1/' || rank || '.jpg', 1000 - rank, 7.5, 1000, rank FROM generate_series(1, 20) AS rank;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT "Id", 'tmdb_movie', "Id"::text FROM "Works" WHERE "Id" BETWEEN 21001 AND 21020;
                """);

            // Where each heading's title, link and visible arrows are, and the title's height; overlapping boxes are reported.
            const string Headings = """
                () => [...document.querySelectorAll('#discover-recommendations .media-shelf__heading')].map(heading => {
                    const box = element => element.getBoundingClientRect();
                    const title = box(heading.querySelector('h2'));
                    const others = [...heading.querySelectorAll('a, .media-shelf__control:not([hidden])')].map(box).filter(other => other.width > 0);
                    const overlap = others.some(other => title.left < other.right - 0.5 && other.left < title.right - 0.5 && title.top < other.bottom - 0.5 && other.top < title.bottom - 0.5);
                    return { text: heading.querySelector('h2').textContent, width: Math.round(title.width), top: Math.round(title.top + scrollY), height: Math.round(title.height), overlap,
                        inside: others.every(other => other.right <= innerWidth + 0.5) };
                })
                """;

            try
            {
                foreach (var width in new[] { 320, 338, 360, 390, 430 })
                {
                    await page.SetViewportSizeAsync(width, 800);
                    await page.GotoAsync("/?type=movies");
                    var shelves = page.Locator("#discover-recommendations .media-shelf");
                    await Assertions.Expect(shelves.GetByRole(AriaRole.Heading, new() { Name = "Neuerscheinungen" })).ToBeVisibleAsync();
                    await Assertions.Expect(shelves.First.GetByRole(AriaRole.Link, new() { Name = "Alle anzeigen" })).ToBeVisibleAsync();
                    var before = await page.EvaluateAsync<JsonElement>(Headings);
                    Evidence.Log($"[test] {width}px: {before}");
                    foreach (var heading in before.EnumerateArray())
                    {
                        Assert.IsFalse(heading.GetProperty("overlap").GetBoolean(), $"{width}px: {heading}");
                        Assert.IsTrue(heading.GetProperty("inside").GetBoolean(), $"{width}px: {heading}");
                        Assert.IsTrue(heading.GetProperty("width").GetInt32() >= 120, $"{width}px: {heading}");
                    }

                    // Swiping a shelf sideways moves none of the headings.
                    await shelves.First.Locator(".media-shelf__items").EvaluateAsync("items => items.scrollBy({ left: 300 })");
                    await page.WaitForTimeoutAsync(200);
                    var after = await page.EvaluateAsync<JsonElement>(Headings);
                    Assert.AreEqual(before.ToString(), after.ToString(), $"{width}px");
                    if (width is 320 or 390) await Evidence.SaveAsync(page, $"discover-shelf-headings-{width}");
                }
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-shelf-headings-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Filter_ListsTypeThenAvailability_AndOnlyTheFiltersTheChosenTypesHave()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            try
            {
                // The media types come first, then the local availability, then the type's filters (the year range) and the rankings the chosen types have.
                await page.GotoAsync("/?type=movies");
                await page.Locator("button[data-drawer-pane=filter]").ClickAsync();
                var drawer = page.Locator("#discover-panel");
                await Assertions.Expect(drawer).ToBeVisibleAsync();
                async Task<float> TopAsync(ILocator element) => (await element.BoundingBoxAsync())!.Y;
                var types = await TopAsync(drawer.Locator(".drawer__section").First);
                var availability = await TopAsync(drawer.Locator(".choice-group").Filter(new() { Has = page.Locator("input[name=availability]") }));
                var year = await TopAsync(drawer.Locator(".discover-range").Filter(new() { Has = page.Locator("input[name=yearFrom]") }));
                var rankings = drawer.Locator(".choice-group").Filter(new() { Has = page.Locator("input[name=feed]") });
                Assert.IsTrue(types < availability && availability < year && year < await TopAsync(rankings), $"{types} {availability} {year}");
                await Assertions.Expect(rankings.GetByText("Upcoming", new() { Exact = true })).ToBeVisibleAsync();

                // Choosing music instead takes the rankings away at once, since music has none; movies bring them back.
                await drawer.Locator(".choice-group__check", new() { HasText = "Movies" }).ClickAsync();
                await drawer.Locator(".choice-group__check", new() { HasText = "Music" }).ClickAsync();
                await Assertions.Expect(rankings).ToBeHiddenAsync();
                await Assertions.Expect(drawer.GetByText("Can be requested", new() { Exact = true })).ToBeVisibleAsync();
                await drawer.Locator(".choice-group__check", new() { HasText = "Movies" }).ClickAsync();
                await Assertions.Expect(rankings).ToBeVisibleAsync();
                await Assertions.Expect(rankings.GetByText("Upcoming", new() { Exact = true })).ToBeVisibleAsync();

                // A page for a type without rankings does not offer them at all.
                await page.GotoAsync("/?type=music");
                await page.Locator("button[data-drawer-pane=filter]").ClickAsync();
                await Assertions.Expect(page.Locator("#discover-panel input[name=feed]")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "discover-filter-music");
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-filter-adapts-failed");
                throw;
            }
        }
    }
}
