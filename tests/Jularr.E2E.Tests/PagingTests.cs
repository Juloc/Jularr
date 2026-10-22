using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // One paging model for every list: a full page offers Next, a short one does not, page 1 has no way back, and the pager sits in the dock.
    [TestClass]
    public sealed class PagingTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task TranslationTable_PagesWithTheDock_WithoutCountingPages()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Localization?UiLocaleId=1");
                var rows = page.Locator("main table tbody tr");
                var pager = page.GetByRole(AriaRole.Navigation, new() { Name = "Pages", Exact = true });
                await Assertions.Expect(rows).ToHaveCountAsync(25);
                await Assertions.Expect(pager).ToContainTextAsync("Page 1");
                await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true })).ToBeDisabledAsync();
                Assert.AreEqual(1, await page.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).CountAsync(), "Only the dock offers Next, the table has no buttons of its own.");

                // Next, then back: the address carries the page and the locale.
                await pager.GetByRole(AriaRole.Link, new() { Name = "Next", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(new Regex("pageNumber=2"));
                await Assertions.Expect(pager).ToContainTextAsync("Page 2");
                await Assertions.Expect(rows).ToHaveCountAsync(25);
                await pager.GetByRole(AriaRole.Link, new() { Name = "Previous", Exact = true }).ClickAsync();
                await Assertions.Expect(pager).ToContainTextAsync("Page 1");
                await Evidence.SaveAsync(page, "translation-table-paging");
            }
            catch
            {
                await Evidence.SaveAsync(page, "translation-table-paging-failed");
                throw;
            }
        }
    }
}
