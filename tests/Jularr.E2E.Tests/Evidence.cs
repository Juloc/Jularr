using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What a failed test leaves under TestResults/e2e: a full-page picture, the page source and the server and fake-service log.
    internal static class Evidence
    {
        private static readonly ConcurrentQueue<string> Lines = new();

        public static void Log(string line)
        {
            Console.WriteLine(line);
            Lines.Enqueue(line);
        }

        public static async Task SaveAsync(IPage page, string name)
        {
            var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
            await File.WriteAllLinesAsync(Path.Combine(folder, name + ".log"), Lines);

            // Its own failure must not hide the real one.
            try
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, name + ".png"), FullPage = true });
                await File.WriteAllTextAsync(Path.Combine(folder, name + ".html"), "<!-- " + page.Url + " -->\n" + await page.ContentAsync());
            }
            catch (PlaywrightException)
            {
            }
        }
    }
}
