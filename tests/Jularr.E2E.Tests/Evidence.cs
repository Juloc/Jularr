using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What the browser showed when a test failed: a full-page picture and the page source under TestResults/e2e.
    internal static class Evidence
    {
        public static async Task SaveAsync(IPage page, string name)
        {
            // Its own failure must not hide the real one.
            try
            {
                var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, name + ".png"), FullPage = true });
                await File.WriteAllTextAsync(Path.Combine(folder, name + ".html"), "<!-- " + page.Url + " -->\n" + await page.ContentAsync());
            }
            catch (PlaywrightException)
            {
            }
        }
    }
}
