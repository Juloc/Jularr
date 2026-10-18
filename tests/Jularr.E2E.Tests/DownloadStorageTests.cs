using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class DownloadStorageTests
    {
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(180_000)]
        public async Task NativeStorage_BrowsesBothPathsAndPersistsExplicitSettings(int width, int height)
        {
            using var folders = new Folders("Incomplete", "Complete & Bücher");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            await page.AddInitScriptAsync("window.jularrLayoutShift = 0; new PerformanceObserver(list => { for (const entry of list.getEntries()) { if (!entry.hadRecentInput) window.jularrLayoutShift += entry.value; } }).observe({ type: 'layout-shift', buffered: true });");
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library);
                await page.GotoAsync("/Admin/DownloadStorage");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.AreEqual(0d, await page.EvaluateAsync<double>("() => window.jularrLayoutShift"));
                var form = page.Locator("form[action*='downloadStorageAreaId=1']");
                await Assertions.Expect(form.Locator("[name=IncompleteStorageMountId]")).ToHaveValueAsync("");
                await Assertions.Expect(form.Locator("[name=MaxJobBytes]")).ToHaveValueAsync("");
                await Assertions.Expect(form.Locator("[name=MaxParallelJobs]")).ToHaveValueAsync("2");
                var originalAddress = page.Url;
                var navigations = 0;
                page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };
                var reserve = form.Locator("[data-byte-size]").Filter(new() { Has = page.Locator("[name=FreeSpaceReserveBytes]") });
                await reserve.Locator("[data-byte-input]").FillAsync("0.5");
                await Assertions.Expect(form.Locator("[name=FreeSpaceReserveBytes]")).ToHaveValueAsync("536870912");
                await reserve.Locator("select").SelectOptionAsync("1048576");
                await Assertions.Expect(reserve.Locator("[data-byte-input]")).ToHaveValueAsync("512");
                await reserve.Locator("[data-byte-input]").FillAsync("0");

                await ChooseAsync(page, form, folders.Library, "Incomplete", 0);
                await ChooseAsync(page, form, folders.Library, "Complete & Bücher", 1);
                await Assertions.Expect(form.Locator("[name=IncompletePath]")).ToHaveValueAsync("Incomplete");
                await Assertions.Expect(form.Locator("[name=CompletePath]")).ToHaveValueAsync("Complete & Bücher");
                Assert.AreEqual(originalAddress, page.Url);
                Assert.AreEqual(0, navigations);
                await Assertions.Expect(form.Locator("[name=FreeSpaceReserveBytes]")).ToHaveValueAsync("0");
                await Steps.SubmitAsync(page, form);
                form = page.Locator("form[action*='downloadStorageAreaId=1']");
                await Assertions.Expect(form.Locator("[name=IncompletePath]")).ToHaveValueAsync("Incomplete");
                await Assertions.Expect(form.Locator("[name=CompletePath]")).ToHaveValueAsync("Complete & Bücher");
                await Assertions.Expect(form.Locator("[name=MaxJobBytes]")).ToHaveValueAsync("");
                await Assertions.Expect(page.Locator("form[action*='downloadStorageAreaId=2'] [name=IncompleteStorageMountId]")).ToHaveValueAsync("");
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                var directory = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "download-storage-" + width + ".png"), FullPage = true });
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "download-storage-" + width);
                throw;
            }
        }

        private static async Task ChooseAsync(IPage page, ILocator form, string mountPath, string path, int field)
        {
            await form.GetByRole(AriaRole.Link, new() { Name = "Browse folders", Exact = true }).Nth(field).ClickAsync();
            var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
            await picker.GetByRole(AriaRole.Link, new() { Name = "Library · " + mountPath, Exact = true }).ClickAsync();
            await picker.GetByRole(AriaRole.Link, new() { Name = path, Exact = true }).ClickAsync();
            await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
            await Assertions.Expect(picker).Not.ToBeVisibleAsync();
        }
    }
}
