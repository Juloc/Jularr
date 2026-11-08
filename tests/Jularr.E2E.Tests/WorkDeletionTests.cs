using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class WorkDeletionTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task Delete_LostConfirmation_DoesNotRetryAndStillProcessesTheOtherFilms()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle")
                SELECT id, 1, 'Unconfirmed target ' || id FROM generate_series(2101, 2103) AS id;
                INSERT INTO "WantedItems" ("Id", "WorkId") SELECT id, id FROM generate_series(2101, 2103) AS id;
                """);
            var calls = 0;
            await page.RouteAsync("**/Admin/Works?**handler=DeleteOne**", async route =>
            {
                Interlocked.Increment(ref calls);
                if (route.Request.PostData?.Contains("2102", StringComparison.Ordinal) == true)
                {
                    // The deletion commits, but its response never reaches the browser.
                    await route.FetchAsync();
                    await route.AbortAsync();
                }
                else await route.ContinueAsync();
            });
            await page.GotoAsync("/Admin/Works?view=Posters&type=movies&sort=0&q=Unconfirmed");
            foreach (var box in await page.Locator("input[name=workIds][form=works-bulk]").AllAsync()) await box.CheckAsync();
            await page.Locator("[data-works-delete]").ClickAsync();
            await page.Locator("#works-delete button[type=submit]").ClickAsync();
            var progress = page.Locator(".works-delete-progress");
            await Assertions.Expect(progress).ToHaveAttributeAsync("data-running", "false", new() { Timeout = 30_000 });
            await Assertions.Expect(progress).ToContainTextAsync("Delete: 2 of 3 titles");
            await Assertions.Expect(progress).ToContainTextAsync("Unconfirmed target 2102");
            await Assertions.Expect(progress).ToContainTextAsync("No confirmation received");
            Assert.AreEqual(3, calls);
            Assert.AreEqual(0L, await jularr.ScalarAsync("""SELECT count(*) FROM "Works" WHERE "Id" BETWEEN 2101 AND 2103"""));
            await Assertions.Expect(page.Locator("input[name=workIds][form=works-bulk]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#works-delete button[type=submit]")).ToBeDisabledAsync();
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Delete_FiftyFilms_ShowsProgressContinuesAfterFailureAndKeepsOnlyFailuresSelected()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'Acquisition';
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle")
                SELECT id, 1, 'Delete target ' || id FROM generate_series(2001, 2050) AS id;
                INSERT INTO "WantedItems" ("Id", "WorkId") SELECT id, id FROM generate_series(2001, 2050) AS id;
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId")
                VALUES (2025, 2025, 'delete-busy', 'Busy film', 1);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId") VALUES (2025, 2, 2, 2025);
                """);

            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await page.RouteAsync("**/Admin/Works?**handler=DeleteOne**", async route =>
            {
                Interlocked.Increment(ref calls);
                if (route.Request.PostData?.Contains("2002", StringComparison.Ordinal) == true) await resume.Task;
                await route.ContinueAsync();
            });
            try
            {
                await page.GotoAsync("/Admin/Works?view=Table&type=movies&sort=0&q=Delete");
                await Assertions.Expect(page.Locator("input[name=workIds][form=works-bulk]")).ToHaveCountAsync(50);
                await page.EvaluateAsync("window.keptWorksPage = document.getElementById('app-content')");
                await page.Locator("input[data-dock-select-all]").CheckAsync();
                await page.Locator("[data-works-delete]").ClickAsync();
                var form = page.Locator("#works-delete form");
                await form.Locator("button[type=submit]").ClickAsync();
                var progress = page.Locator(".works-delete-progress");
                await Assertions.Expect(progress).ToContainTextAsync("1 of 50 titles processed");
                await Assertions.Expect(progress.Locator("progress")).ToHaveAttributeAsync("value", "1");
                await Assertions.Expect(progress).ToContainTextAsync("Deleting: Delete target 2002");
                await Assertions.Expect(page.Locator(".action-dock [data-dock-count]")).ToHaveTextAsync("49 selected");
                await Assertions.Expect(form.Locator("button[type=submit]")).ToBeDisabledAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator("#works-delete")).ToBeVisibleAsync();
                // Even a second submit event cannot start another deletion run.
                await form.EvaluateAsync("form => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))");
                Assert.AreEqual(2, calls);
                await Evidence.SaveAsync(page, "works-delete-progress-desktop");
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "works-delete-progress-phone");
                resume.TrySetResult();

                await Assertions.Expect(progress).ToHaveAttributeAsync("data-running", "false", new() { Timeout = 60_000 });
                await Assertions.Expect(progress).ToContainTextAsync("Delete: 49 of 50 titles");
                await Assertions.Expect(progress).ToContainTextAsync("Delete target 2025");
                await Assertions.Expect(progress).ToContainTextAsync("A download is running");
                Assert.AreEqual(50, calls);
                await Assertions.Expect(page.Locator("input[name=workIds][form=works-bulk]:checked")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("input[name=workIds][form=works-bulk]:checked")).ToHaveValueAsync("2025");
                await Assertions.Expect(page.Locator(".action-dock [data-dock-count]")).ToHaveTextAsync("1 selected");
                Assert.IsTrue(await page.EvaluateAsync<bool>("window.keptWorksPage === document.getElementById('app-content')"));
                StringAssert.Contains(page.Url, "type=movies");
                StringAssert.Contains(page.Url, "q=Delete");
                await Evidence.SaveAsync(page, "works-delete-failure-phone");

                await jularr.RunSqlAsync("""UPDATE "Operations" SET "OperationStatusTypeId" = 3, "FinishedAt" = now() WHERE "Id" = 2025""");
                await form.Locator("button[type=submit]").ClickAsync();
                await Assertions.Expect(progress).ToHaveAttributeAsync("data-running", "false", new() { Timeout = 30_000 });
                await Assertions.Expect(progress).ToContainTextAsync("Delete: 1 of 1 titles");
                await Assertions.Expect(page.Locator("input[name=workIds][form=works-bulk]")).ToHaveCountAsync(0);
                Assert.AreEqual(51, calls);
                await Assertions.Expect(form.Locator("button[type=submit]")).ToBeDisabledAsync();
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "failure-works-delete");
                throw;
            }
            finally
            {
                resume.TrySetResult();
            }
        }
    }
}
