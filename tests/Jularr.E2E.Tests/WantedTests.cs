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

            // The views keep the types; Missing is the first and the default, All the last.
            await Assertions.Expect(page.Locator(".navigation-tabs a[aria-current=page]")).ToContainTextAsync("Missing");
            await Assertions.Expect(page.Locator(".navigation-tabs__link").Last).ToContainTextAsync("All");
            await page.Locator(".navigation-tabs a", new() { HasTextString = "All" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("View=All&type=manga$"));
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

        // Scrolled to the end, the last row of a page lies above the floating bar, which itself stays clear of the bar of the book being read
        // and of the minimized player (a box of the player's size here): on a computer and on a phone.
        private const string EndOfPage = """
            async () => {
                scrollTo(0, document.documentElement.scrollHeight);
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                const pill = document.querySelector('.action-dock').getBoundingClientRect();
                const rows = [...document.querySelectorAll('.wanted-list .list-row')];
                const last = rows[rows.length - 1].getBoundingClientRect();
                const under = [...document.querySelectorAll('.continue-bar, [data-test-player]')].map(element => element.getBoundingClientRect());
                return [last.bottom <= pill.top, under.every(box => pill.bottom <= box.top), pill.bottom <= innerHeight];
            }
            """;

        [TestMethod]
        [Timeout(300_000)]
        public async Task Wanted_TheFloatingBarCoversNoRow_AndTheStateSaysWhyInFull()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // More than a page of targets that are not due, every other one with a last search that found the download storage unavailable.
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") SELECT 9200 + n, 1, 'Wanted Film ' || n, 2000 + n FROM generate_series(1, 30) AS n;
                INSERT INTO "WantedItems" ("WorkId", "NextSearchAt", "LastOutcomeKey") SELECT 9200 + n, now() + interval '1 day', CASE WHEN n % 2 = 1 THEN 'download_storage_unavailable' END FROM generate_series(1, 30) AS n;
                """);

            try
            {
                foreach (var (width, height, phone) in new[] { (1440, 900, false), (390, 844, true) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync("/Admin/Wanted");

                    // The pill says it in a short word, the reason stands whole under it.
                    var state = page.Locator(".wanted-list .wanted-status").First;
                    await Assertions.Expect(state.Locator(".state-chip")).ToHaveTextAsync("Requested");
                    await Assertions.Expect(state.Locator(".wanted-status__reason")).ToHaveTextAsync("Download storage is unavailable or its mount changed");
                    Assert.IsTrue(await state.EvaluateAsync<bool>("state => [...state.querySelectorAll('.state-chip__label, .wanted-status__reason')].every(part => part.scrollWidth <= part.clientWidth + 1)"), "Nothing of the state is cut off.");

                    var name = phone ? "phone" : "desktop";
                    CollectionAssert.AreEqual(new[] { true, true, true }, await page.EvaluateAsync<bool[]>(EndOfPage), "Alone: " + name);
                    await Evidence.SaveAsync(page, "wanted-end-" + name);

                    await page.EvaluateAsync("""document.querySelector('.app-main').insertAdjacentHTML('beforeend', '<section class="continue-bar"><span class="continue-bar__cover"></span></section>')""");
                    CollectionAssert.AreEqual(new[] { true, true, true }, await page.EvaluateAsync<bool[]>(EndOfPage), "Over the bar of the book being read: " + name);
                    await Evidence.SaveAsync(page, "wanted-end-continue-" + name);

                    await page.EvaluateAsync($$"""
                        () => {
                            document.querySelector('.continue-bar').remove();
                            document.body.classList.add('player-mini');
                            document.body.insertAdjacentHTML('beforeend', '<div data-test-player style="position: fixed; right: 12px; bottom: {{(phone ? 84 : 12)}}px; width: min(720px, calc(100% - 24px)); height: 88px; background: #333"></div>');
                        }
                        """);
                    CollectionAssert.AreEqual(new[] { true, true, true }, await page.EvaluateAsync<bool[]>(EndOfPage), "Over the minimized player: " + name);
                    await page.EvaluateAsync("scrollTo(0, 400)");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.querySelector('.action-dock').getBoundingClientRect().bottom <= document.querySelector('[data-test-player]').getBoundingClientRect().top"), "While the page scrolls the bar floats above the player: " + name);
                    await Evidence.SaveAsync(page, "wanted-player-" + name);
                }
            }
            catch
            {
                await Evidence.SaveAsync(page, "wanted-floating-failed");
                throw;
            }
        }
    }
}
