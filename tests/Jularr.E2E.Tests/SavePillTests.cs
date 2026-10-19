using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Instance page saves its settings and module switches through one pill: discarding shows the saved page in place,
    // a failing form keeps its error while the other changed forms are saved.
    [TestClass]
    public sealed class SavePillTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task InstancePage_SavesChangedFormsTogether_DiscardsInPlace_AndKeepsAFailingFormsError()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            var asked = false;
            page.Dialog += (_, dialog) => { asked = true; _ = dialog.DismissAsync(); };
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance");
            var pill = page.Locator("[data-save-pill]");
            var save = pill.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true });
            var name = page.Locator("form[data-save] [name=Name]");
            var module = page.Locator("main form[data-save]").Filter(new() { Has = page.Locator("legend", new() { HasText = "Let visitors ask for an account" }) });
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("[data-form-save]").First).ToBeHiddenAsync();

            // Two changes show in the pill; discarding restores the saved page without asking.
            var original = await name.InputValueAsync();
            await name.FillAsync("Pill test");
            await module.Locator("[name=IsEnabled][value=True]").CheckAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Evidence.SaveAsync(page, "save-pill-desktop");
            await page.SetViewportSizeAsync(390, 844);
            await Evidence.SaveAsync(page, "save-pill-mobile");
            await page.SetViewportSizeAsync(1280, 720);
            await Steps.FollowAsync(page, pill.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }));
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(name).ToHaveValueAsync(original);
            await Assertions.Expect(module.Locator("[name=IsEnabled][value=False]")).ToBeCheckedAsync();

            // An empty name fails on its own form; the module switch is saved all the same.
            await name.FillAsync("");
            await module.Locator("[name=IsEnabled][value=True]").CheckAsync();
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill).ToContainTextAsync("1 not saved");
            await Assertions.Expect(page.Locator("form[data-save] [role=alert]")).ToHaveCountAsync(1);
            await Assertions.Expect(module.Locator("[name=IsEnabled][value=True]")).ToBeCheckedAsync();

            // The corrected name is saved; a fresh load shows both values.
            await name.FillAsync("Pill test");
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill.GetByRole(AriaRole.Button, new() { Name = "Saved", Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Assertions.Expect(name).ToHaveValueAsync("Pill test");
            await Assertions.Expect(module.Locator("[name=IsEnabled][value=True]")).ToBeCheckedAsync();

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
