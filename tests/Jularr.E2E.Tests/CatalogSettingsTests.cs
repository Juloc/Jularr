using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Discover catalog page: a sync window is added as a draft, typed in the desktop time fields and saved through the pill.
    [TestClass]
    public sealed class CatalogSettingsTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task AniListWindow_AddedTypedAndSaved_StaysAfterReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Catalog");
            await Assertions.Expect(page.GetByText("Full local discovery is off", new() { Exact = false })).ToBeVisibleAsync();

            var aniList = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "AniList", Exact = true }) }).Locator("form[data-save]");
            await Steps.FollowAsync(page, aniList.GetByRole(AriaRole.Button, new() { Name = "Add window", Exact = true }));
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeVisibleAsync();

            // The desktop shows hour and minute fields that fill the native time field.
            var parts = aniList.Locator(".ui-time__part");
            await Assertions.Expect(parts).ToHaveCountAsync(4);
            await parts.Nth(0).FillAsync("22");
            await parts.Nth(1).FillAsync("30");
            await parts.Nth(2).FillAsync("01");
            await parts.Nth(3).FillAsync("00");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Evidence.SaveAsync(page, "catalog-settings");
            await Steps.SaveAsync(page);

            await page.ReloadAsync();
            await Assertions.Expect(aniList.Locator("[name='Windows[0].StartsAt']")).ToHaveValueAsync("22:30");
            await Assertions.Expect(aniList.Locator("[name='Windows[0].EndsAt']")).ToHaveValueAsync("01:00");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();

            // The cache of images of other titles is sized here and saved the same way.
            var covers = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Images of other titles", Exact = true }) }).Locator("form[data-save]");
            await Assertions.Expect(covers.GetByLabel("Keep unused images for days")).ToHaveValueAsync("7");
            await covers.GetByLabel("Largest size in MB, 0 keeps none").FillAsync("120");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(covers.GetByLabel("Largest size in MB, 0 keeps none")).ToHaveValueAsync("120");
        }
    }
}
