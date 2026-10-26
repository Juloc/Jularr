using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The naming page: the colon replacement and the folder of season 0 are changed together and saved through the pill.
    [TestClass]
    public sealed class NamingSettingsTests
    {
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task EpisodeNames_ColonAndSpecialsFolderChanged_StayAfterReload(int width, int height)
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Naming");

            // Anime episodes have a format of their own beside the series' one.
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Anime episode files", Exact = true })).ToBeVisibleAsync();
            var settings = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Episode files", Exact = true }) })
                .Locator("form[data-form-kind$=UpdateNamingSettingsDataV1]");
            await settings.Locator("[name=ColonReplacementId]").SelectOptionAsync(new SelectOptionValue { Label = "Own" });
            await settings.Locator("[name=CustomColonReplacement]").FillAsync("~");
            await settings.Locator("[name=SpecialsFolder]").FillAsync("Specials - ");
            await page.GetByRole(AriaRole.Button, new() { Name = "Placeholders", Exact = true }).ClickAsync();
            await page.Locator("[data-naming-token='{ImdbId}']").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(settings.Locator("[name=SpecialsFolder]")).ToHaveValueAsync("Specials - {ImdbId}");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeVisibleAsync();
            await Evidence.SaveAsync(page, "naming-settings-" + width);
            await Steps.SaveAsync(page);

            await page.ReloadAsync();
            await Assertions.Expect(settings.Locator("[name=ColonReplacementId] option:checked")).ToHaveTextAsync("Own");
            await Assertions.Expect(settings.Locator("[name=CustomColonReplacement]")).ToHaveValueAsync("~");
            await Assertions.Expect(settings.Locator("[name=SpecialsFolder]")).ToHaveValueAsync("Specials - {ImdbId}");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
        }
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task AllMediaNames_IdsDailyAndMultiEpisodeSettings_PreviewAndPersist(int width, int height)
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Naming");
            foreach (var name in new[] { "Book files", "Manga files", "Light novel files", "Music files", "Game files" })
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true })).ToBeVisibleAsync();

            var books = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Book files", Exact = true }) });
            var bookPattern = books.Locator("form[data-form-kind$=UpdateNamingPatternDataV1]").First;
            const string bookFile = "{Book Title} {olid-{OpenLibraryId}} [{Book Language}]";
            await bookPattern.Locator("[name=File]").FillAsync(bookFile);
            await Assertions.Expect(bookPattern.Locator("+ .naming-examples [data-naming-example]")).ToContainTextAsync("{olid-OL12345W}");

            var series = page.Locator(".ui-section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "Episode files", Exact = true }) });
            var standard = series.Locator("form[data-form-kind$=UpdateNamingPatternDataV1]").First;
            const string daily = "{Series Title} - {Air-Date} - {Episode Title} {ImdbId}";
            await standard.Locator("[name=DailyFile]").FillAsync(daily);
            await standard.Locator("[name=SeasonFolder]").FillAsync("Season {season} - {ImdbId}");
            var settings = series.Locator("form[data-form-kind$=UpdateNamingSettingsDataV1]");
            await settings.Locator("[name=MultiEpisodeStyleId]").SelectOptionAsync("3");
            await Assertions.Expect(standard.Locator("+ .naming-examples [data-naming-multi]")).ToContainTextAsync("S01E01-E02-E03");
            await Assertions.Expect(standard.Locator("+ .naming-examples [data-naming-daily]")).ToContainTextAsync("2013-10-30");
            await Evidence.SaveAsync(page, "all-media-naming-" + width);
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(bookPattern.Locator("[name=File]")).ToHaveValueAsync(bookFile);
            await Assertions.Expect(standard.Locator("[name=DailyFile]")).ToHaveValueAsync(daily);
            await Assertions.Expect(standard.Locator("[name=SeasonFolder]")).ToHaveValueAsync("Season {season} - {ImdbId}");
            await Assertions.Expect(settings.Locator("[name=MultiEpisodeStyleId]")).ToHaveValueAsync("3");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
        }
    }
}
