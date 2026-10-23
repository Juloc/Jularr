using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public static class BrowserInstall
    {
        // Playwright keeps its browsers in its own cache; this fetches Chromium once for every test.
        [AssemblyInitialize]
        public static void Install(TestContext _)
        {
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                JularrInstance.Unavailable("Chromium for Playwright could not be installed.");
            }
        }
    }

    // One browser on a started instance, kept on this machine, with the problems its page causes.
    internal sealed class BrowserSession : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;

        private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, ConcurrentQueue<string> problems)
        {
            _playwright = playwright;
            _browser = browser;
            Context = context;
            Page = page;
            Problems = problems;
        }

        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public ConcurrentQueue<string> Problems { get; }

        public static async Task<BrowserSession> StartAsync(JularrInstance jularr)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            await Steps.KeepLocalAsync(context);
            var page = await context.NewPageAsync();
            return new BrowserSession(playwright, browser, context, page, Steps.Watch(page));
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }
}
