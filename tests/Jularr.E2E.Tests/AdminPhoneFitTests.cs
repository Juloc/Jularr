using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What an administration page shows on a phone fits its width: a release name without spaces wraps inside its card, a status word stays whole in its tile.
    [TestClass]
    public sealed class AdminPhoneFitTests
    {
        private const string LongRelease = "Hello.I.am.a.Witch.and.my.Crush.Wants.me.to.Make.a.Love.Potion.S01E06.1080p.CR.WEB-DL.JPN.AAC2.0.H.264.MSubs-ToonsHub";

        [TestMethod]
        [Timeout(300_000)]
        public async Task BlockedAndDashboard_OnAPhone_FitTheScreen()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await jularr.RunSqlAsync("""UPDATE "Providers" SET "IsEnabled" = FALSE WHERE "ProtectedSecret" IS NULL;""");
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("""
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Requests');
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9701, 1, 'Blocked Film', 2020);
                    INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (9701, 9701);
                    """);
                await jularr.RunSqlAsync($$"""
                    INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId")
                    VALUES (9701, 9701, 'blocked-film', '{{LongRelease}}', 4);
                    """);

                foreach (var (width, height) in new[] { (375, 812), (768, 1024), (1440, 900) })
                {
                    await page.SetViewportSizeAsync(width, height);

                    // The blocked list: the page is no wider than the screen, and the whole release name is inside its row.
                    await page.GotoAsync("/Admin/Queue/Blocked");
                    var row = Steps.ListRow(page, "Blocked Film");
                    await Assertions.Expect(row).ToHaveCountAsync(1);
                    Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), $"{width}: the blocked list scrolls sideways.");
                    if (width < 700)
                    {
                        Assert.IsTrue(await row.EvaluateAsync<bool>("""
                            row => { const box = row.getBoundingClientRect(), name = row.querySelector('.list-row__caption .truncated__text').getBoundingClientRect(); return name.left >= box.left && name.right <= box.right; }
                            """), $"{width}: the release name leaves its card.");
                    }

                    await Evidence.SaveAsync(page, $"blocked-{width}");

                    // The dashboard: the widest word a status tile can show fits its tile.
                    await page.GotoAsync("/Admin/Dashboard");
                    var value = page.Locator(".kpi-tile--card .kpi-tile__value").First;
                    await Assertions.Expect(value).ToBeVisibleAsync();
                    await value.EvaluateAsync("element => { element.textContent = 'Warnung'; }");
                    Assert.IsTrue(await value.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth"), $"{width}: the status word is cut off.");
                    await Evidence.SaveAsync(page, $"dashboard-{width}");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-admin-phone-fit");
                throw;
            }
        }
    }
}
