using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The naming page: the colon replacement and the folder of season 0 are changed together and saved through the pill.
    [TestClass]
    public sealed class NamingSettingsTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task EpisodeNames_ColonAndSpecialsFolderChanged_StayAfterReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Library/Naming");

            // Anime episodes have a format of their own beside the series' one.
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Anime episode files", Exact = true })).ToBeVisibleAsync();
            var settings = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Episode names", Exact = true }) }).Locator("form[data-save]");
            await settings.Locator("[name=ColonReplacementId]").SelectOptionAsync(new SelectOptionValue { Label = "Own" });
            await settings.Locator("[name=CustomColonReplacement]").FillAsync("~");
            await settings.Locator("[name=SpecialsFolder]").FillAsync("Extras");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeVisibleAsync();
            await Evidence.SaveAsync(page, "naming-settings");
            await Steps.SaveAsync(page);

            await page.ReloadAsync();
            await Assertions.Expect(settings.Locator("[name=ColonReplacementId] option:checked")).ToHaveTextAsync("Own");
            await Assertions.Expect(settings.Locator("[name=CustomColonReplacement]")).ToHaveValueAsync("~");
            await Assertions.Expect(settings.Locator("[name=SpecialsFolder]")).ToHaveValueAsync("Extras");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
        }
    }
}
