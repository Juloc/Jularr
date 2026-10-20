using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The management page for anime (a second number and a switch between the numberings) and for manga (volumes in one table, with their files).
    [TestClass]
    public sealed class WorkTypesTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Anime_ShowsAbsoluteNumbers_AndSwitchesToAniListNumbers_AndManga_ShowsItsVolumes()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // An anime of two seasons; the second is split into two AniList entries after its second episode.
                var anime = await CreateAsync(page, "Series", "Anime", "Test Anime");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") VALUES ({anime}, 1, 1), ({anime}, 1, 2);
                    INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                    SELECT {anime}, season."Id", 2, episode."Number", date '2020-01-01' + episode."Number"
                    FROM "WorkUnits" AS season CROSS JOIN generate_series(1, 3) AS episode ("Number")
                    WHERE season."WorkId" = {anime} AND season."WorkUnitTypeId" = 1;
                    INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset")
                    SELECT season."Id", 'anilist', CASE WHEN offsets."Offset" = 0 THEN '1001' ELSE '1002' END, offsets."Offset"
                    FROM "WorkUnits" AS season CROSS JOIN (VALUES (0), (2)) AS offsets ("Offset")
                    WHERE season."WorkId" = {anime} AND season."WorkUnitTypeId" = 1 AND season."Number" = 2;
                    """);
                await page.GotoAsync("/Admin/Works/" + anime);
                await Assertions.Expect(page.Locator("th[data-col=absolute]").First).ToBeVisibleAsync();
                var season = page.Locator(".work-season").First;
                var third = season.Locator("tr.work-row").Filter(new() { Has = page.Locator("td[data-col=number]").GetByText("3", new() { Exact = true }) });
                await Assertions.Expect(third.Locator("td[data-col=absolute]")).ToHaveTextAsync("6");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=standard]")).ToBeVisibleAsync();

                // The AniList numbering only changes what is shown: the third episode of the season is the first of its second entry.
                await page.GetByRole(AriaRole.Button, new() { Name = "AniList", Exact = true }).ClickAsync();
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=anilist]")).ToHaveTextAsync("1");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=standard]")).ToBeHiddenAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "AniList", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=anilist]")).ToBeVisibleAsync();

                // A manga has its volumes in one table without seasons.
                var manga = await CreateAsync(page, "Book", "Manga", "Test Manga");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "ReleasedOn") VALUES ({manga}, 3, 1, date '2020-01-01'), ({manga}, 3, 2, date '2020-06-01');
                    """);
                await page.GotoAsync("/Admin/Works/" + manga);
                foreach (var tab in new[] { "Volumes", "History", "Manage files", "Metadata" })
                {
                    await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = tab })).ToBeVisibleAsync();
                }

                Assert.AreEqual(0, await page.Locator(".work-season").CountAsync(), "A manga has no seasons.");
                var rows = page.Locator("[data-work-table=volumes] tr.work-row");
                Assert.AreEqual(2, await rows.CountAsync());
                StringAssert.Contains(await rows.First.InnerTextAsync(), "Volume 2");
                StringAssert.Contains(await rows.First.InnerTextAsync(), "Missing");
                await Assertions.Expect(page.Locator("th[data-col=released]").First).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "work-manga-volumes");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-work-types");
                throw;
            }
        }

        // A title made by hand on the Works page, as far as its management page.
        private static async Task<string> CreateAsync(IPage page, string type, string subtype, string title)
        {
            await page.GotoAsync("/Admin/Works");
            var form = page.Locator("main form", new() { Has = page.Locator("[name=CanonicalTitle]") });
            await form.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = type });
            await form.Locator("[name=MediaSubtypeId]").SelectOptionAsync(new SelectOptionValue { Label = subtype });
            await form.Locator("[name=CanonicalTitle]").FillAsync(title);
            await Steps.SubmitAsync(page, form);
            return Regex.Match(page.Url, @"/Admin/Works/(\d+)").Groups[1].Value;
        }
    }
}
