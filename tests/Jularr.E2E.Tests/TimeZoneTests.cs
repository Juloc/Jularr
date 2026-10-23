using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class TimeZoneTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task BrowserZone_FormatsUtcInstantsAndCalendarTodayInTwoZones()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();

            await using var berlin = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = jularr.Address.ToString(), TimezoneId = "Europe/Berlin"
            });
            var germanPage = await berlin.NewPageAsync();
            await Owner.SetUpAndSignInAsync(germanPage);
            await germanPage.GotoAsync("/Account/Sessions");

            var germanTime = germanPage.Locator("time[data-local-time]").First;
            await Assertions.Expect(germanTime).ToBeVisibleAsync();
            var instant = await germanTime.GetAttributeAsync("datetime");
            var germanText = await germanTime.InnerTextAsync();
            var formatted = await germanTime.EvaluateAsync<string>("element => new Intl.DateTimeFormat(document.documentElement.lang || navigator.language, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(element.getAttribute('datetime')))");
            Assert.AreEqual(formatted, germanText, "The page must show the browser-local hour, not Docker UTC.");

            await CheckCalendarTodayAsync(germanPage, "Europe/Berlin");

            await using var newYork = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = jularr.Address.ToString(), TimezoneId = "America/New_York"
            });
            await newYork.AddCookiesAsync(await berlin.CookiesAsync());
            var americanPage = await newYork.NewPageAsync();
            await americanPage.GotoAsync("/Account/Sessions");

            var americanTime = americanPage.Locator("time[data-local-time]").First;
            await Assertions.Expect(americanTime).ToBeVisibleAsync();
            Assert.AreEqual(instant, await americanTime.GetAttributeAsync("datetime"));
            Assert.AreNotEqual(germanText, await americanTime.InnerTextAsync(), "The same UTC instant must be shown with two different hours.");

            await CheckCalendarTodayAsync(americanPage, "America/New_York");
        }

        private static async Task CheckCalendarTodayAsync(IPage page, string expectedZone)
        {
            await page.GotoAsync("/Calendar");
            var cell = page.Locator(".calendar__cell--today");
            await Assertions.Expect(cell).ToHaveCountAsync(1);
            var day = await page.EvaluateAsync<string>("() => { const d = new Date(); return [d.getFullYear(), String(d.getMonth() + 1).padStart(2, '0'), String(d.getDate()).padStart(2, '0')].join('-'); }");
            Assert.AreEqual(day, await cell.GetAttributeAsync("data-calendar-day"));
            var cookieZone = await page.EvaluateAsync<string>("() => document.cookie.split('; ').find(c => c.startsWith('jularr_time_zone='))?.split('=')[1] ?? ''");
            Assert.AreEqual(expectedZone, cookieZone);
        }
    }
}
