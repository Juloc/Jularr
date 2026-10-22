using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class SetupWizardTests
    {
        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task SetupSteps_SourceTabsSaveInPlaceAndResumeAtEveryStep(int width, int height)
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await page.GotoAsync("/Setup");
                await Assertions.Expect(page.Locator("select[name=UiLocaleId]")).ToContainTextAsync("machine translated");
                await page.FillAsync("[name=Username]", Owner.Username);
                await page.FillAsync("[name=ProfileName]", "Owner");
                await page.FillAsync("[name=Password]", Owner.Password);
                await page.FillAsync("[name=PasswordConfirmation]", Owner.Password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Create Owner", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Instance");
                Assert.AreEqual(5, await page.Locator(".setup-wizard .stepper__step").CountAsync());
                Assert.AreEqual(1, await page.Locator("form[action='/Setup/Instance']").CountAsync());
                Assert.AreEqual(0, await page.Locator("[name=UsersConfirmSources]").CountAsync());
                await page.Locator("[data-setup-preset=requests]").ClickAsync();
                Assert.IsTrue(await page.GetByRole(AriaRole.Switch, new() { Name = "Arr+", Exact = true }).IsCheckedAsync());
                Assert.IsTrue(await page.Locator("[data-module=Registration] input[type=checkbox]").IsCheckedAsync());
                await page.Locator("[data-setup-preset=complete]").ClickAsync();
                await CheckViewportAsync(page, "modules", width);
                await page.FillAsync("[name=Name]", "Wizard test");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await CheckViewportAsync(page, "libraries", width);
                await page.GotoAsync("/Setup/Progress");
                await page.WaitForURLAsync("**/Setup/Storage");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/DownloadStorage");
                await CheckViewportAsync(page, "download-storage", width);
                await page.GotoAsync("/Setup/Progress");
                await page.WaitForURLAsync("**/Setup/DownloadStorage");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Sources");
                await CheckViewportAsync(page, "sources", width);
                Assert.AreEqual(6, await page.Locator(".setup-wizard .navigation-tabs a").CountAsync());
                await page.FillAsync("[name=TmdbKey]", "setup-browser-key");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }));
                await page.WaitForURLAsync("**/Setup/Sources");
                await Assertions.Expect(page.Locator("[name=TmdbKey]")).ToHaveValueAsync("");

                await OpenSourceTabAsync(page, "/Setup/Indexers");
                await SaveConnectionAsync(page, "Setup indexer", "https://indexer.example.test", "Add indexer");
                await CheckViewportAsync(page, "indexers", width);
                await OpenSourceTabAsync(page, "/Setup/Downloads");
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Configure download storage", Exact = true })).ToBeVisibleAsync();
                await SaveConnectionAsync(page, "Setup downloader", "https://downloader.example.test", "Add download client");
                await CheckViewportAsync(page, "downloads", width);
                await OpenSourceTabAsync(page, "/Setup/Profiles");
                await Assertions.Expect(page.Locator(".profiles-list")).ToBeVisibleAsync();
                await CheckViewportAsync(page, "profiles", width);
                await page.Locator(".profiles-list a[href^='/Setup/Profiles/']").First.ClickAsync();
                await Assertions.Expect(page.Locator(".setup-wizard .navigation-tabs a[aria-current=page]")).ToHaveAttributeAsync("href", "/Setup/Profiles");
                await Assertions.Expect(page.Locator("[name=Name]")).ToBeVisibleAsync();
                await OpenSourceTabAsync(page, "/Setup/Takeover");
                await Assertions.Expect(page.Locator("[name=BaseUrl]")).ToBeVisibleAsync();
                await CheckViewportAsync(page, "takeover", width);
                await OpenSourceTabAsync(page, "/Setup/Providers");
                await CheckViewportAsync(page, "providers", width);
                await page.GotoAsync("/Setup/Progress");
                await page.WaitForURLAsync("**/Setup/Sources");
                await page.GetByRole(AriaRole.Button, new() { Name = "Finish setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                await page.GotoAsync("/Setup/Progress");
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), "The setup overflows its viewport.");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-wizard-" + width);
                throw;
            }
        }

        private static async Task OpenSourceTabAsync(IPage page, string path)
        {
            await page.Locator(".setup-wizard .navigation-tabs").Locator($"a[href='{path}']").ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == path);
            await Assertions.Expect(page.Locator(".setup-wizard .stepper a[aria-current=step]")).ToHaveAttributeAsync("href", "/Setup/Sources");
            await Assertions.Expect(page.Locator(".setup-wizard .navigation-tabs a[aria-current=page]")).ToHaveAttributeAsync("href", path);
        }

        // The connection is added in the dialog of its list.
        private static async Task SaveConnectionAsync(IPage page, string name, string address, string add)
        {
            await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = add, Exact = true }));
            var form = page.Locator("dialog#connection-editor form").Filter(new() { Has = page.Locator("[name=AcquisitionConnectionTypeId]") });
            await form.Locator("[name=Name]").FillAsync(name);
            await form.Locator("[name=BaseUrl]").FillAsync(address);
            await form.Locator("[name=ApiKey]").FillAsync("browser-test-key");
            await form.Locator("[name=IsEnabled][value=False]").CheckAsync();
            await Steps.SubmitAsync(page, form);
            await Assertions.Expect(page.Locator(".list-row", new() { HasText = name })).ToBeVisibleAsync();
            Assert.IsTrue(new Uri(page.Url).AbsolutePath.StartsWith("/Setup/", StringComparison.Ordinal));
        }

        private static async Task CheckViewportAsync(IPage page, string step, int width)
        {
            Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), step + " overflows its viewport.");
            var directory = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "setup-" + step + "-" + width + ".png"), FullPage = true });
        }
    }
}
