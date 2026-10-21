using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Throwaway: screenshots of the dash_mock site (never committed).
    [TestClass]
    public sealed class ZzShots
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task Shots()
        {
            var password = File.ReadAllLines(@"D:\git\worktrees\dash-mock\owner-login.txt").First(line => line.StartsWith("password:", StringComparison.Ordinal))[9..].Trim();
            var pages = (Environment.GetEnvironmentVariable("SHOT_PAGES") ?? "/Admin").Split(',');
            var sizes = (Environment.GetEnvironmentVariable("SHOT_SIZES") ?? "1440x1000").Split(',');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            foreach (var size in sizes)
            {
                var parts = size.Split('x');
                await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = int.Parse(parts[0]), Height = int.Parse(parts[1]) }, BaseURL = "http://127.0.0.1:5284/" });
                var page = await context.NewPageAsync();
                await page.GotoAsync("/Account/Login");
                await page.FillAsync("[name=Username]", "owner");
                await page.FillAsync("[name=Password]", password);
                await page.Locator("form button[type=submit]").Last.ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Account/Login");
                foreach (var path in pages)
                {
                    await page.GotoAsync(path);
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(700);
                    var name = path.Trim('/').Replace('/', '-');
                    await page.ScreenshotAsync(new() { Path = $@"D:\git\worktrees\dash-mock\shots\final-{(name.Length == 0 ? "root" : name)}-{size}.png", FullPage = true });
                }
            }
        }
    }
}
