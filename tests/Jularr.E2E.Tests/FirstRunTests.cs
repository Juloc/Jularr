using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A fresh installation in a real browser: the Owner is created in setup, signs in, and every page of the menu opens cleanly.
    [TestClass]
    public sealed class FirstRunTests
    {
        private const string Password = "e2e-owner-password-123";

        [ClassInitialize]
        public static void InstallBrowser(TestContext _)
        {
            // Playwright keeps its browsers in its own cache; this fetches Chromium once.
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                JularrInstance.Unavailable("Chromium for Playwright could not be installed.");
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task FreshInstance_OwnerSetupSignInAndMenuPages_OpenWithoutErrors()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var page = await context.NewPageAsync();
            var problems = new ConcurrentQueue<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error") problems.Enqueue(page.Url + ": " + message.Text);
            };
            page.PageError += (_, error) => problems.Enqueue(page.Url + ": " + error);
            page.Response += (_, response) =>
            {
                if (response.Status >= 500) problems.Enqueue(response.Url + ": HTTP " + response.Status);
            };

            try
            {
                // The first visit leads to setup, where the Owner is created.
                await page.GotoAsync("/");
                await page.WaitForURLAsync("**/Setup**");
                await page.FillAsync("[name=Email]", "owner@example.test");
                await page.FillAsync("[name=DisplayName]", "Owner");
                await page.FillAsync("[name=Password]", Password);
                await page.FillAsync("[name=PasswordConfirmation]", Password);
                await page.Locator("form button[type=submit]").Last.ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Setup");

                // Signed out and in again with the new account.
                await context.ClearCookiesAsync();
                await page.GotoAsync("/Account/Login");
                await page.FillAsync("[name=Email]", "owner@example.test");
                await page.FillAsync("[name=Password]", Password);
                await page.Locator("form button[type=submit]").First.ClickAsync();
                await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

                // Every menu entry opens with its content and without server or script errors.
                var links = await page.Locator("nav a[href^='/']").EvaluateAllAsync<string[]>("links => [...new Set(links.map(link => link.getAttribute('href')))]");
                Assert.IsTrue(links.Length > 5, "The menu shows too few pages: " + string.Join(", ", links));
                foreach (var link in links.Where(link => !link.StartsWith("/Account/Logout", StringComparison.Ordinal)))
                {
                    var response = await page.GotoAsync(link);
                    Assert.IsNotNull(response, link);
                    Assert.IsTrue(response.Status < 400, link + " answered " + response.Status);
                    Assert.AreEqual(1, await page.Locator("main").CountAsync(), link + " has no page content");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), problems.ToArray());
            }
            catch
            {
                // The picture helps; its own failure must not hide the real one.
                try
                {
                    var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, "first-run.png"), FullPage = true });
                }
                catch (PlaywrightException)
                {
                }

                throw;
            }
        }
    }
}
