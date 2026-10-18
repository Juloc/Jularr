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
                StringAssert.Contains(await page.Locator("select[name=UiLocaleId]").InnerTextAsync(), "machine translated");
                await page.FillAsync("[name=Email]", Owner.Email);
                await page.FillAsync("[name=DisplayName]", "Owner");
                await page.FillAsync("[name=Password]", Owner.Password);
                await page.FillAsync("[name=PasswordConfirmation]", Owner.Password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Create Owner", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Instance");
                Assert.AreEqual(5, await page.Locator(".setup-wizard .navigation-tabs a").CountAsync());
                Assert.AreEqual(1, await page.Locator("form[action='/Setup/Instance']").CountAsync());
                Assert.AreEqual(0, await page.Locator("[name=UsersConfirmSources]").CountAsync());
                await page.GetByRole(AriaRole.Button, new() { Name = "Requests", Exact = true }).ClickAsync();
                Assert.IsTrue(await page.GetByRole(AriaRole.Switch, new() { Name = "Arr+", Exact = true }).IsCheckedAsync());
                Assert.IsTrue(await page.GetByRole(AriaRole.Switch, new() { Name = "Let visitors ask for an account an administrator enables", Exact = true }).IsCheckedAsync());
                await page.GetByRole(AriaRole.Button, new() { Name = "Complete", Exact = true }).ClickAsync();
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
                Assert.AreEqual(6, await page.Locator(".setup-wizard .navigation-tabs").Nth(1).Locator("a").CountAsync());
                await page.FillAsync("[name=TmdbKey]", "setup-browser-key");
                await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Sources");
                await Assertions.Expect(page.Locator("[name=TmdbKey]")).ToHaveValueAsync("");

                await OpenSourceTabAsync(page, "/Setup/Indexers");
                await SaveConnectionAsync(page, "Setup indexer", "https://indexer.example.test");
                await CheckViewportAsync(page, "indexers", width);
                await OpenSourceTabAsync(page, "/Setup/Downloads");
                await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Configure download storage", Exact = true })).ToBeVisibleAsync();
                await SaveConnectionAsync(page, "Setup downloader", "https://downloader.example.test");
                await CheckViewportAsync(page, "downloads", width);
                await OpenSourceTabAsync(page, "/Setup/Profiles");
                await Assertions.Expect(page.Locator(".acquisition-settings")).ToBeVisibleAsync();
                await CheckViewportAsync(page, "profiles", width);
                await page.Locator("a[href^='/Setup/Profiles/']").Filter(new() { HasText = "Edit" }).First.ClickAsync();
                await Assertions.Expect(page.Locator(".setup-wizard .navigation-tabs").Nth(1).Locator("a[aria-current=page]")).ToHaveAttributeAsync("href", "/Setup/Profiles");
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
            await page.Locator(".setup-wizard .navigation-tabs").Nth(1).Locator($"a[href='{path}']").ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == path);
            await Assertions.Expect(page.Locator(".setup-wizard .navigation-tabs").First.Locator("a[aria-current=page]")).ToHaveAttributeAsync("href", "/Setup/Sources");
            await Assertions.Expect(page.Locator(".setup-wizard .navigation-tabs").Nth(1).Locator("a[aria-current=page]")).ToHaveAttributeAsync("href", path);
        }

        private static async Task SaveConnectionAsync(IPage page, string name, string address)
        {
            var form = page.Locator("form").Filter(new() { Has = page.Locator("[name=AcquisitionConnectionTypeId]") });
            await form.Locator("[name=Name]").FillAsync(name);
            await form.Locator("[name=BaseUrl]").FillAsync(address);
            await form.Locator("[name=ApiKey]").FillAsync("browser-test-key");
            await form.Locator("[name=IsEnabled][value=False]").CheckAsync();
            await form.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("[name=Name]")).ToHaveValueAsync(name);
            await Assertions.Expect(page.Locator("[name=ApiKey]")).ToHaveValueAsync("");
            await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true })).ToBeVisibleAsync();
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
