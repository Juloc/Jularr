using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ArtworkSettingsUiTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task OwnerCanConfigureNasAndCacheSeparatelyUsingTheExistingInstanceUI()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance");

            var settings = page.Locator("form[action*='handler=Artwork']");
            await Assertions.Expect(settings).ToBeVisibleAsync();
            await Assertions.Expect(settings.Locator("[name=NasModeId][value='1']")).ToBeCheckedAsync();
            await Assertions.Expect(settings.Locator("[name=CacheFormatId][value='1']")).ToBeCheckedAsync();

            await settings.Locator("[name=NasModeId][value='2']").CheckAsync();
            await settings.Locator("[name=CacheFormatId][value='2']").CheckAsync();
            await settings.Locator("[name=CacheQualityId]").SelectOptionAsync("3");
            await settings.Locator("[name=CacheLimitBytes]").SelectOptionAsync("536870912");
            await settings.Locator("button[type=submit]").ClickAsync();
            await page.WaitForURLAsync("**/Admin/Instance");

            settings = page.Locator("form[action*='handler=Artwork']");
            await Assertions.Expect(settings.Locator("[name=NasModeId][value='2']")).ToBeCheckedAsync();
            await Assertions.Expect(settings.Locator("[name=CacheFormatId][value='2']")).ToBeCheckedAsync();
            await Assertions.Expect(settings.Locator("[name=CacheQualityId]")).ToHaveValueAsync("3");
            await Assertions.Expect(settings.Locator("[name=CacheLimitBytes]")).ToHaveValueAsync("536870912");
            await Assertions.Expect(page.GetByText("Artwork settings saved.")).ToBeVisibleAsync();
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task ArtworkSettings_MaintainUsableMobileLayout()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync("/Admin/Instance");

            var settings = page.Locator("form[action*='handler=Artwork']");
            await Assertions.Expect(settings).ToBeVisibleAsync();
            foreach (var selector in new[] { "[name=NasModeId]", "[name=CacheFormatId]", "[name=CacheQualityId]", "[name=CacheLimitBytes]" })
            {
                var box = await settings.Locator(selector).First.BoundingBoxAsync();
                Assert.IsNotNull(box, selector + " must be laid out.");
                Assert.IsTrue(box.X >= -1 && box.X + box.Width <= 391, selector + " must fit the mobile viewport.");
            }
        }
    }
}
