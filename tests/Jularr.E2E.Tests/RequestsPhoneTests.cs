using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The phone layout of My requests (preference chips and tab row) and the page behind an open dialog or request panel staying put.
    [TestClass]
    public sealed class RequestsPhoneTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task RequestsOnAPhone_KeepChipsInTheirRowAndTabsApartFromTools_AndDialogsLockThePageBehind()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                DO $$ DECLARE pid bigint; aid bigint; qid bigint;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    SELECT "Id" INTO aid FROM "Accounts" ORDER BY "Id" LIMIT 1;
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Library', 'Requests');
                    INSERT INTO "AcquisitionProfiles" ("Name", "MediaTypeId", "Definition")
                    SELECT 'Ultra HD Remux Lossless Audio Edition', 1, "Definition" FROM "AcquisitionProfiles" WHERE "MediaTypeId" = 1 AND "IsDefault" LIMIT 1
                    RETURNING "Id" INTO qid;
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle")
                    SELECT 9000 + n, 1, 'Phone film ' || lpad(n::text, 2, '0') FROM generate_series(1, 12) AS n;
                    INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionProfileId")
                    SELECT "Id", pid, aid, 1, qid FROM "Works";
                END $$;
                """);
            try
            {
                await page.SetViewportSizeAsync(375, 700);
                await page.GotoAsync("/Requests?state=All&type=movies");
                var rows = page.Locator(".request-row");
                await Assertions.Expect(rows).ToHaveCountAsync(12);

                // Every preference chip lies inside its row, none is cut off.
                var chips = await rows.EvaluateAllAsync<bool[]>("""
                    rows => rows.flatMap(row => {
                        const box = row.getBoundingClientRect();
                        return [...row.querySelectorAll('.request-row__prefs .tag')].map(tag => {
                            const chip = tag.getBoundingClientRect();
                            return chip.left >= box.left - 0.5 && chip.right <= box.right + 0.5 && tag.scrollWidth <= tag.clientWidth + 1;
                        });
                    })
                    """);
                Assert.IsTrue(chips.Length >= 24, "Each request shows its language and quality chip.");
                Assert.IsTrue(chips.All(inside => inside), "A preference chip is cut off at 375 px.");

                // The tabs have the first line; the tools do not overlap them and stay at the right edge.
                var bar = await page.EvaluateAsync<double[]>("""
                    () => {
                        const rect = selector => document.querySelector(selector).getBoundingClientRect();
                        const tabs = rect('.requests-bar .navigation-tabs'), tools = rect('.requests-tools'), bar = rect('.requests-bar');
                        return [tabs.bottom, tools.top, tabs.right, tools.left, tools.right, bar.right];
                    }
                    """);
                Assert.IsTrue(bar[0] <= bar[1] + 0.5 || bar[2] <= bar[3] + 0.5, "The tabs overlap the tools: " + string.Join(", ", bar));
                Assert.IsTrue(bar[4] >= bar[5] - 2, "The tools left the right edge: " + string.Join(", ", bar));
                await Evidence.SaveAsync(page, "requests-phone");

                // The panel of a request is not a dialog element, yet the page behind it stays put.
                await page.EvaluateAsync("window.scrollTo(0, 150)");
                await page.Mouse.MoveAsync(60, 400);
                await page.Mouse.WheelAsync(0, 300);
                await page.WaitForTimeoutAsync(200);
                Assert.IsTrue(await page.EvaluateAsync<double>("window.scrollY") > 150, "The control scroll did not move the page.");
                await page.EvaluateAsync("window.scrollTo(0, 150)");
                await rows.Nth(3).Locator(".request-row__actions a[href*='request=']").ClickAsync();
                await Assertions.Expect(page.Locator(".request-drawer__head")).ToBeVisibleAsync();
                await AssertPageStaysAsync(page);

                // A modal dialog does the same.
                await page.GotoAsync("/Admin/Acquisition");
                await page.EvaluateAsync("document.body.style.minHeight = '3000px'; window.scrollTo(0, 150)");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add indexer", Exact = true }));
                await Assertions.Expect(page.Locator("dialog#connection-editor")).ToBeVisibleAsync();
                await AssertPageStaysAsync(page);
            }
            catch
            {
                await Evidence.SaveAsync(page, "requests-phone-failed");
                throw;
            }
        }

        private static async Task AssertPageStaysAsync(IPage page)
        {
            Assert.AreEqual("hidden", await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).overflowY"));
            var before = await page.EvaluateAsync<double>("window.scrollY");
            await page.Mouse.MoveAsync(5, 5);
            await page.Mouse.WheelAsync(0, 400);
            await page.WaitForTimeoutAsync(300);
            Assert.AreEqual(before, await page.EvaluateAsync<double>("window.scrollY"), "The page moved behind the dialog.");
        }
    }
}
