using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The identity reviews under the shared media-type bar: a type of the bar leaves only its own reviews and the address keeps the choice.
    [TestClass]
    public sealed class MatchesTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Matches_MediaTypeBar_NarrowsTheReviews()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear") VALUES (9101, 2, 1, 'Review Anime', 2019), (9102, 3, 2, 'Review Manga', 2001), (9103, 6, NULL, 'Review Song', 2005);
                INSERT INTO "WorkIdentityMatches" ("WorkId", "Namespace", "IdentityMatchStatusTypeId", "Candidates") VALUES
                    (9101, 'anilist', 2, '[{"Id": 1, "Title": "Review Anime"}]'), (9102, 'anilist', 2, '[{"Id": 2, "Title": "Review Manga"}]'), (9103, 'anilist', 2, '[]');
                """);

            await page.GotoAsync("/Admin/Works/Matches");
            await Assertions.Expect(page.Locator(".capsule-bar")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".ui-section")).ToHaveCountAsync(3);

            // Manga is a subtype across types; one type leaves its reviews, the others go.
            await page.Locator(".capsule-group__item", new() { HasTextString = "Manga" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=manga$"));
            await Assertions.Expect(page.Locator(".ui-section")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator(".ui-section")).ToContainTextAsync("Review Manga");
            await page.Locator(".capsule-group__item", new() { HasTextString = "Music" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("type=manga,music$"));
            await Assertions.Expect(page.Locator(".ui-section")).ToHaveCountAsync(2);
        }
    }
}
