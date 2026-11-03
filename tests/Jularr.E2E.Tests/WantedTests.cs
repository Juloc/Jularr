using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The wanted list under the shared media-type bar: a type of the bar leaves only its own targets and counts, the address keeps the choice through the views.
    [TestClass]
    public sealed class WantedTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Wanted_MediaTypeBar_NarrowsTheTargetsAndCounts()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (9101, 2, 1, 'Wanted Anime', 2019), (9102, 3, 2, 'Wanted Manga', 2001), (9103, 1, NULL, 'Wanted Film', 2010);
                INSERT INTO "WantedItems" ("WorkId") VALUES (9101), (9102), (9103);
                """);

            await page.GotoAsync("/Admin/Wanted");
            var bar = page.Locator(".capsule-bar");
            await Assertions.Expect(bar).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".wanted-list a.list-identity__title")).ToHaveCountAsync(3);
            await Evidence.SaveAsync(page, "wanted-types-all");

            // Manga is a subtype across types; one type leaves its targets and its count, the others go.
            await bar.Locator(".capsule-group__item", new() { HasTextString = "Manga" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=manga$"));
            await Assertions.Expect(page.Locator(".wanted-list a.list-identity__title")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".wanted-list")).ToContainTextAsync("Wanted Manga");
            await Assertions.Expect(page.Locator(".navigation-tabs a[aria-current=page]")).ToContainTextAsync("1");

            // The views keep the types.
            await page.Locator(".navigation-tabs a", new() { HasTextString = "Missing" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("View=Missing&type=manga$"));
            await Assertions.Expect(page.Locator(".wanted-list a.list-identity__title")).ToHaveCountAsync(1);
            await Evidence.SaveAsync(page, "wanted-types-manga");

            await page.SetViewportSizeAsync(375, 812);
            await page.WaitForTimeoutAsync(600);
            await Evidence.SaveAsync(page, "wanted-types-manga-phone");
            await page.SetViewportSizeAsync(1280, 900);

            await bar.Locator(".capsule-group__item", new() { HasTextString = "Movies" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=movies,manga$"));
            await Assertions.Expect(page.Locator(".wanted-list a.list-identity__title")).ToHaveCountAsync(2);
        }
    }
}
