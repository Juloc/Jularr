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

        [AssemblyCleanup]
        public static async Task CloseAsync() => await BrowserSession.CloseAsync();
    }

    // A browser on a started instance, kept on this machine, with the problems its page causes. All tests share one Chromium, started at the first
    // test; each test has a new context of its own (cookies, storage, routes, viewport), in English and UTC wherever it runs.
    internal sealed class BrowserSession : IAsyncDisposable
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static IPlaywright? _playwright;
        private static IBrowser? _browser;

        private BrowserSession(IBrowserContext context, IPage page, ConcurrentQueue<string> problems)
        {
            Context = context;
            Page = page;
            Problems = problems;
        }

        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public ConcurrentQueue<string> Problems { get; }

        public static async Task<BrowserSession> StartAsync(JularrInstance jularr)
        {
            var browser = await BrowserAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString(), Locale = "en-US", TimezoneId = "UTC" });
            await Steps.KeepLocalAsync(context);
            var page = await context.NewPageAsync();
            return new BrowserSession(context, page, Steps.Watch(page));
        }

        // Closes the test's context with its pages; the browser stays for the next test. A browser that went away took the context with it.
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Context.CloseAsync();
            }
            catch (PlaywrightException) when (_browser is not { IsConnected: true })
            {
            }
        }

        // The shared browser; launched again when it is gone, so a crash fails only the test that saw it.
        private static async Task<IBrowser> BrowserAsync()
        {
            await Gate.WaitAsync();
            try
            {
                if (_browser is { IsConnected: true })
                {
                    return _browser;
                }

                _playwright ??= await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync();
                return _browser;
            }
            finally
            {
                Gate.Release();
            }
        }

        internal static async Task CloseAsync()
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync();
                _browser = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
    }
}
