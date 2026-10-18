using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class DownloadMappingTests
    {
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(180_000)]
        public async Task CompletedDownloadStorage_BrowsesAndPersistsWithoutLosingTheDraft(int width, int height)
        {
            using var folders = new Folders("Complete & Bücher");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.AddInitScriptAsync("window.jularrLayoutShift = 0; new PerformanceObserver(list => { for (const entry of list.getEntries()) { if (!entry.hadRecentInput) window.jularrLayoutShift += entry.value; } }).observe({ type: 'layout-shift', buffered: true });");
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library);
                await page.GotoAsync("/Admin/Acquisition/Downloads");
                await page.GetByRole(AriaRole.Link, new() { Name = "Add download client", Exact = true }).ClickAsync();
                var form = page.Locator("form").Filter(new() { Has = page.Locator("[name=BaseUrl]") });
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.AreEqual(0d, await page.EvaluateAsync<double>("() => window.jularrLayoutShift"));
                await form.Locator("[name=Name]").FillAsync("My download client");
                await form.Locator("[name=BaseUrl]").FillAsync("https://sab.example.test");
                await form.Locator("[name=ApiKey]").FillAsync("test-only-key");
                await form.Locator("[name=RemotePath]").FillAsync("/data/downloads/complete");
                await form.Locator("[name=LocalPath]").FillAsync("/old-container-path");
                await form.Locator("[name=DownloadStorageMountId]").SelectOptionAsync("1");
                var originalAddress = page.Url;
                var navigations = 0;
                page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };
                await form.GetByRole(AriaRole.Link, new() { Name = "Browse folders", Exact = true }).ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await dialog.GetByRole(AriaRole.Link, new() { Name = "Complete & Bücher", Exact = true }).ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await Assertions.Expect(form.Locator("[name=LocalPath]")).ToHaveValueAsync("Complete & Bücher");
                await Assertions.Expect(form.Locator("[name=Name]")).ToHaveValueAsync("My download client");
                await Assertions.Expect(form.Locator("[name=ApiKey]")).ToHaveValueAsync("test-only-key");
                Assert.AreEqual(originalAddress, page.Url);
                Assert.AreEqual(0, navigations);
                await Steps.SubmitAsync(page, form);
                form = page.Locator("form").Filter(new() { Has = page.Locator("[name=BaseUrl]") });
                await Assertions.Expect(form.Locator("[name=DownloadStorageMountId]")).ToHaveValueAsync("1");
                await Assertions.Expect(form.Locator("[name=LocalPath]")).ToHaveValueAsync("Complete & Bücher");
                await Assertions.Expect(form.Locator("[name=ApiKey]")).ToHaveValueAsync("");
                await form.GetByRole(AriaRole.Link, new() { Name = "Browse folders", Exact = true }).ClickAsync();
                await dialog.GetByRole(AriaRole.Link, new() { Name = "Connection root", Exact = true }).ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await Steps.SubmitAsync(page, form);
                form = page.Locator("form").Filter(new() { Has = page.Locator("[name=BaseUrl]") });
                await Assertions.Expect(form.Locator("[name=DownloadStorageMountId]")).ToHaveValueAsync("1");
                await Assertions.Expect(form.Locator("[name=LocalPath]")).ToHaveValueAsync("");
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", "download-mapping-" + width + ".png") });
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "download-mapping-" + width);
                throw;
            }
        }
    }
}
