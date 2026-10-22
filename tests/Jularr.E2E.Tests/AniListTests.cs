using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A profile's own AniList: the Owner's application, the code of AniList's PIN page, the list taken over and the progress sent, all against a fake AniList.
    [TestClass]
    public sealed class AniListTests
    {
        private const long Work = 7100;

        [TestMethod]
        [Timeout(300_000)]
        public async Task Profile_ConnectsItsAniList_TakesOverItsList_AndSendsProgress_UntilItDisconnects()
        {
            using var folders = new Folders();
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:AniList"] = fakes.AniList.ToString(),
                ["Providers:AniListAccount"] = fakes.AniListAccount.ToString()
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // An anime of one season; AniList's first entry covers it. The sync is a module, off until the Owner switches it on.
                await jularr.RunSqlAsync($"""
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('ProgressSync', 'AniList');
                    INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES ({Work}, 2, 1, 'Example Anime');
                    INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (71000, {Work}, 1, 1);
                    INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                    SELECT {Work}, 71000, 2, episode."Number", date '2020-01-01' + episode."Number" FROM generate_series(1, 4) AS episode ("Number");
                    INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset") VALUES (71000, 'anilist', '7001', 0);
                    """);

                // Until the Owner sets the application up, a profile has nothing to connect.
                await page.GotoAsync("/Settings");
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "Open AniList" }).CountAsync());

                // The Owner enters the application's client id and secret; the secret is never shown again.
                await page.GotoAsync("/Admin/Providers");
                var provider = page.Locator("#provider-anilist");
                await provider.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
                var application = page.GetByRole(AriaRole.Dialog, new() { Name = "AniList", Exact = true }).Locator("form[action*='handler=Secret']");
                await application.Locator("[name=ClientId]").FillAsync(FakeServices.AniListClientId);
                await application.Locator("[name=Secret]").FillAsync(FakeServices.AniListSecret);
                await Steps.SubmitAsync(page, application);
                Assert.IsFalse((await page.ContentAsync()).Contains(FakeServices.AniListSecret, StringComparison.Ordinal));

                // The profile signs in at AniList and pastes the code its PIN page shows.
                await page.GotoAsync("/Settings");
                var open = page.GetByRole(AriaRole.Link, new() { Name = "Open AniList" });
                await Assertions.Expect(open).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex("client_id=" + FakeServices.AniListClientId + "&redirect_uri=.*&response_type=code$"));
                await page.GetByLabel("Code from AniList").FillAsync("not-the-code");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Connect AniList" }));
                Assert.AreEqual(0, await page.Locator(".anilist-card__account").CountAsync(), "A code AniList refuses connects nothing.");
                await page.GotoAsync("/Settings");
                await page.GetByLabel("Code from AniList").FillAsync(FakeServices.AniListCode);
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Connect AniList" }));
                await Assertions.Expect(page.Locator(".anilist-card__account .truncated__text")).ToHaveTextAsync("Connected as E2E Fan");
                Assert.IsFalse((await page.ContentAsync()).Contains(FakeServices.AniListToken, StringComparison.Ordinal));
                await Evidence.SaveAsync(page, "anilist-card");

                // The job takes over the list: episode 1 is watched there; the title it does not know waits and is not created.
                await RunJobAsync(page);
                await WaitForWatchedAsync(page, expectedEpisodes: 1);

                // Sending is the profile's choice. Episode 2 finished afterwards goes out, nothing else does.
                await page.GotoAsync("/Settings");
                await page.GetByRole(AriaRole.Switch, new() { Name = "Send my progress to AniList" }).CheckAsync(new() { Force = true });
                await Steps.SaveAsync(page);
                await jularr.RunSqlAsync($"""
                    INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "IsCompleted", "CompletedAt")
                    SELECT profile."Id", {Work}, unit."Id", 1, 0, TRUE, now()
                    FROM "Profiles" AS profile CROSS JOIN "WorkUnits" AS unit
                    WHERE unit."WorkId" = {Work} AND unit."WorkUnitTypeId" = 2 AND unit."Number" = 2
                    LIMIT 1;
                    """);
                Assert.AreEqual(0, fakes.AniListWrites.Count);
                await RunJobAsync(page);
                for (var attempt = 0; attempt < 60 && !fakes.AniListWrites.Contains("8801:2"); attempt++)
                {
                    await Task.Delay(500);
                }

                CollectionAssert.AreEqual(new[] { "8801:2" }, fakes.AniListWrites.ToArray());
                Assert.AreEqual(3, fakes.AniListProgress[8802], "The entry of a title Jularr does not know stays as it was.");

                // Disconnecting offers the connection again; the progress in Jularr stays.
                await page.GotoAsync("/Settings");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Disconnect", Exact = true }));
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Connect AniList" })).ToBeVisibleAsync();
                await WaitForWatchedAsync(page, expectedEpisodes: 2);

                // AniList is a module of its own that needs the library (the account also needs the progress sync); off, a profile has nothing to connect.
                await page.GotoAsync("/Admin/Instance/Modules");
                var module = page.Locator(".setting-card[data-module=AniList]");
                await Assertions.Expect(module.Locator("[data-needs]")).ToHaveCountAsync(1);
                await page.GotoAsync("/Settings");
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open AniList" })).ToBeVisibleAsync();
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'AniList'");
                await page.GotoAsync("/Settings");
                await Assertions.Expect(page.Locator(".anilist-card")).ToHaveCountAsync(0);
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Open AniList" })).ToHaveCountAsync(0);

                // The code AniList refused answered 503 on purpose (AniList is the unavailable party); nothing else may fail.
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.Where(problem => !problem.Contains("503", StringComparison.Ordinal)).ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "anilist-failure");
                throw;
            }
        }

        // Run now of the job, as the Owner does on the Jobs page.
        private static async Task RunJobAsync(IPage page)
        {
            await page.GotoAsync("/Admin/Jobs");
            var row = page.Locator(".list-body .list-row").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "AniList sync", Exact = true }) });
            await Steps.FollowAsync(page, row.GetByRole(AriaRole.Button, new() { Name = "Run now", Exact = true }));
        }

        // The episodes watched of the series, as its hero says it ("1 / 4 episodes").
        private static async Task<int> WatchedAsync(IPage page)
        {
            var caption = page.Locator(".media-hero__progress-caption");
            if (await caption.CountAsync() == 0) return 0;
            var match = System.Text.RegularExpressions.Regex.Match(await caption.First.InnerTextAsync(), @"(\d+)\s*/\s*\d+ episodes");
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static async Task WaitForWatchedAsync(IPage page, int expectedEpisodes)
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await page.GotoAsync("/Details/" + Work);
                if (await WatchedAsync(page) == expectedEpisodes)
                {
                    return;
                }

                await Task.Delay(500);
            }

            Assert.AreEqual(expectedEpisodes, await WatchedAsync(page));
        }
    }
}
