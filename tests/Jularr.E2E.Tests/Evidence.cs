using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What a failed test leaves under TestResults/e2e: a full-page picture, the page source and the server and fake-service log.
    internal static class Evidence
    {
        private static readonly ConcurrentQueue<string> Lines = new();

        // Where the local instance to click through writes its log as it goes (scripts/dev-instance.sh); tests keep it in memory for a failure.
        public static string? Mirror { get; set; }

        public static void Log(string line)
        {
            if (Mirror is not null)
            {
                lock (Lines) File.AppendAllText(Mirror, line + Environment.NewLine);
                return;
            }

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
