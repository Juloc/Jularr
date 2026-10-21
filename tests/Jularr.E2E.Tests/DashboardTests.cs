using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Administration's first page, the load page and the log, as the Owner reaches them.
    [TestClass]
    public sealed class DashboardTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task OwnerSeesTheDashboard_ChecksTheServicesAgain_AndOpensPerformanceAndLogs()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin");

                // Four figures, and only the services that are set up: here the database alone.
                foreach (var figure in new[] { "System status", "Media", "Downloads", "Open requests" })
                {
                    await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = figure })).ToHaveCountAsync(1);
                }

                var services = page.Locator(".dashboard-services__row");
                await Assertions.Expect(services).ToHaveCountAsync(1);
                await Assertions.Expect(services.First).ToContainTextAsync("PostgreSQL");

                // Check now asks again; the page shows the answer with the time of the check.
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Check now", Exact = true }));
                await Assertions.Expect(services.First).ToContainTextAsync("Last checked");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "System status" })).ToContainTextAsync("Stable");
                await Evidence.SaveAsync(page, "dashboard");

                // The sidebar's System group leads to the load and to the log.
                await page.GotoAsync("/Admin/Performance");
                await Assertions.Expect(page.Locator(".kpi-tile", new() { HasText = "Response time" })).ToHaveCountAsync(1);
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Services by runtime" })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "performance");
                await page.GotoAsync("/Admin/Logs");
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Error", Exact = true })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "logs");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "dashboard");
                throw;
            }
        }
    }
}
