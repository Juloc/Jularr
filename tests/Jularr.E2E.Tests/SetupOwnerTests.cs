using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class SetupOwnerTests
    {
        [TestMethod]
        [DataRow(1440, 900, false)]
        [DataRow(1440, 900, true)]
        [DataRow(390, 844, false)]
        [DataRow(390, 844, true)]
        [Timeout(300_000)]
        public async Task OwnerStep_ShowsSteps_RevealsPasswords_AndCreatesAccountAndProfileWithTheirNames(int width, int height, bool dark)
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
            try
            {
                await page.GotoAsync("/Setup");

                // The steps stand at the top, the first one current and nothing else marked, and the page has no heading of its own.
                await Assertions.Expect(page.Locator(".stepper__step")).ToHaveCountAsync(12);
                await Assertions.Expect(page.Locator(".stepper__step--current")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step--done")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".stepper a[aria-current=step], .stepper span[aria-current=step]")).ToContainTextAsync("Owner");
                await Assertions.Expect(page.Locator("main h1, main h2")).ToHaveCountAsync(0);

                // The language shows its flag; the fields are the shared form fields.
                await Assertions.Expect(page.Locator("[data-option-icons] [data-option-icon]:not([hidden])")).ToHaveCountAsync(1);
                await Assertions.Expect(page.GetByLabel("User name")).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByLabel("Profile name")).ToBeVisibleAsync();

                // Each password has its eye.
                await page.FillAsync("[name=Password]", Owner.Password);
                await page.FillAsync("[name=PasswordConfirmation]", Owner.Password);
                Assert.AreEqual(2, await page.Locator(".password-reveal").CountAsync());
                await Assertions.Expect(page.Locator("[name=Password]")).ToHaveAttributeAsync("type", "password");
                await page.Locator(".password-reveal").First.ClickAsync();
                await Assertions.Expect(page.Locator("[name=Password]")).ToHaveAttributeAsync("type", "text");
                await Assertions.Expect(page.Locator("[name=PasswordConfirmation]")).ToHaveAttributeAsync("type", "password");
                await page.Locator(".password-reveal").Last.ClickAsync();
                await Assertions.Expect(page.Locator("[name=PasswordConfirmation]")).ToHaveAttributeAsync("type", "text");
                await page.Locator(".password-reveal").First.ClickAsync();
                await Assertions.Expect(page.Locator("[name=Password]")).ToHaveAttributeAsync("type", "password");
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), "The owner step overflows its viewport.");
                await Evidence.SaveAsync(page, "setup-owner-" + width + (dark ? "-dark" : "-light"));

                // The user name and the profile name are two values.
                await page.FillAsync("[name=Username]", "login_name");
                await page.FillAsync("[name=ProfileName]", "Julia at home");
                await page.GetByRole(AriaRole.Button, new() { Name = "Create Owner", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Begin");
                Assert.AreEqual("login_name", await jularr.ScalarAsync("SELECT \"Username\" FROM \"Accounts\""));
                Assert.AreEqual("login_name", await jularr.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\""));
                Assert.AreEqual("Julia at home", await jularr.ScalarAsync("SELECT \"DisplayName\" FROM \"Profiles\""));
                Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"Profiles\""));

                // The first choice is the next step: the Owner is marked done, the ones after it wait.
                await Assertions.Expect(page.Locator(".stepper__step--done")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step--done .stepper__bubble .ui-icon")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step:not(.stepper__step--done):not(.stepper__step--current)")).ToHaveCountAsync(10);
                await page.GetByRole(AriaRole.Button, new() { Name = "New setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).Last.ClickAsync();
                await page.WaitForURLAsync("**/Setup/Instance");
                await Assertions.Expect(page.Locator(".stepper__step--done, .stepper__step--skipped")).ToHaveCountAsync(3);
                await Assertions.Expect(page.Locator(".stepper__step--current")).ToHaveCountAsync(1);
                await Evidence.SaveAsync(page, "setup-modules-" + width + (dark ? "-dark" : "-light"));

                // Three starting sets; the choices of a media type sit under it, and a switch that needs another one turns it on or says why it stays.
                await Assertions.Expect(page.Locator(".option-preset")).ToHaveCountAsync(3);
                await Assertions.Expect(page.Locator(".option-group")).ToHaveCountAsync(7);
                var library = page.Locator(".option-row[data-module=Library]");
                await library.Locator(".toggle").ClickAsync();
                await Assertions.Expect(library.Locator("input[type=checkbox]")).ToBeCheckedAsync();
                await Assertions.Expect(library.Locator(".option-row__reason")).ToBeVisibleAsync();
                var playback = page.Locator(".option-row[data-module=Playback] input[type=checkbox]");
                await page.Locator(".option-row[data-module=Playback] .toggle").ClickAsync();
                await Assertions.Expect(playback).Not.ToBeCheckedAsync();
                await page.Locator(".option-type .option-row[data-module=Trickplay] .toggle").ClickAsync();
                await Assertions.Expect(playback).ToBeCheckedAsync();

                // Every Admin module exists once in Setup, and the advanced groups
                // at the bottom are already expanded rather than silently hidden.
                await Assertions.Expect(page.Locator("[data-module]")).ToHaveCountAsync(27);
                await Assertions.Expect(page.Locator(".option-groups--extras .option-group")).ToHaveCountAsync(3);
                await Assertions.Expect(page.Locator(".option-groups--extras .option-group:not([open])")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".option-groups--extras [data-module=Profiles]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".option-groups--extras [data-module=Diagnostics]")).ToBeVisibleAsync();
                // Optional settings are shown even when their media type is off.
                await page.Locator("[data-media=Book] .toggle").ClickAsync();
                await Assertions.Expect(page.Locator("[data-media=Book] input[type=checkbox]")).Not.ToBeCheckedAsync();
                await Assertions.Expect(page.Locator("[data-module=Translation]")).ToBeVisibleAsync();

                // Complete means every module AND all media types/subtypes.
                var complete = page.Locator("[data-setup-preset=complete]");
                await complete.ClickAsync();
                await Assertions.Expect(complete).ToHaveAttributeAsync("aria-pressed", "true");
                await Assertions.Expect(page.Locator("[data-module] input[type=checkbox]:not(:checked)")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("[data-media] input[type=checkbox]:not(:checked)")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "setup-all-modules-" + width + (dark ? "-dark" : "-light"));

                await Steps.SubmitAsync(page, page.Locator(".setup-modules__form form"));
                await page.WaitForURLAsync("**/Setup/Profiles");
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"InstanceModules\" WHERE NOT \"IsEnabled\""));
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"MediaTypes\" WHERE NOT \"IsEnabled\""));
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"MediaSubtypes\" WHERE NOT \"IsEnabled\""));
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-owner-failed-" + width);
                throw;
            }
        }
    }
}
