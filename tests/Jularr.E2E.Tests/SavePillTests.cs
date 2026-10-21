using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The page's one save pill: it counts what differs from the loaded page, discards in place and saves every changed form, keeping a failing form's error.
    [TestClass]
    public sealed class SavePillTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task NamingPage_SavesChangedFormsTogether_DiscardsInPlace_AndKeepsAFailingFormsError()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            var asked = false;
            page.Dialog += (_, dialog) => { asked = true; _ = dialog.DismissAsync(); };
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Naming");
            var pill = page.Locator("[data-save-pill]");
            var save = pill.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true });
            var file = page.Locator("form[data-form-kind$=UpdateNamingPatternDataV1] [name=File]").First;
            var replaces = page.Locator("form[data-form-kind$=UpdateNamingSettingsDataV1] [name=ReplacesIllegalCharacters]").First;
            var toggle = page.Locator("form[data-form-kind$=UpdateNamingSettingsDataV1] .toggle").First;
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("[data-form-save]").First).ToBeHiddenAsync();

            // Two changes show in the pill; discarding restores the saved page without asking.
            var original = await file.InputValueAsync();
            await file.FillAsync("{Title} ({Year}) pill");
            await toggle.ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Evidence.SaveAsync(page, "save-pill-desktop");
            await page.SetViewportSizeAsync(390, 844);
            await Evidence.SaveAsync(page, "save-pill-mobile");
            await page.SetViewportSizeAsync(1280, 720);
            await Steps.FollowAsync(page, pill.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }));
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(file).ToHaveValueAsync(original);
            await Assertions.Expect(replaces).ToBeCheckedAsync();

            // An empty file name fails on its own form; the other form is saved all the same.
            await file.FillAsync("");
            await toggle.ClickAsync();
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill).ToContainTextAsync("1 not saved");
            await Assertions.Expect(page.Locator("form[data-save] [role=alert]")).ToHaveCountAsync(1);
            await Assertions.Expect(replaces).Not.ToBeCheckedAsync();

            // The corrected name is saved; a fresh load shows both values.
            await file.FillAsync("{Title} ({Year}) pill");
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill.GetByRole(AriaRole.Button, new() { Name = "Saved", Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Assertions.Expect(file).ToHaveValueAsync("{Title} ({Year}) pill");
            await Assertions.Expect(replaces).Not.ToBeCheckedAsync();

            // The profile settings save the same way.
            await page.GotoAsync("/Settings");
            var displayName = page.Locator("form[data-save] [name=DisplayName]");
            await displayName.FillAsync("Pill profile");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(displayName).ToHaveValueAsync("Pill profile");
            Assert.IsFalse(asked, "Discarding or saving asked before leaving.");
        }
    }
}
