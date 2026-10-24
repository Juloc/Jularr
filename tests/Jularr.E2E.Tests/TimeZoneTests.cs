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
            var formatted = await germanTime.EvaluateAsync<string>("element => new Intl.DateTimeFormat(document.documentElement.lang || navigator.language, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(element.getAttribute('datetime')))");
            var germanLabel = germanTime.Locator(".local-time__label");
            await Assertions.Expect(germanLabel).ToHaveTextAsync(formatted);
            var germanText = await germanLabel.InnerTextAsync();
            // Replacing the visible timestamp must not change the width of its containing element.
            var width = (await germanTime.BoundingBoxAsync())!.Width;
            await germanLabel.EvaluateAsync("element => element.textContent = 'x'");
            Assert.IsTrue(Math.Abs((await germanTime.BoundingBoxAsync())!.Width - width) < 1, "The initial UTC fallback must reserve space so localized times do not shift neighboring content.");

            await CheckCalendarTodayAsync(germanPage, "Europe/Berlin");

            // Reuse the authenticated session without converting Playwright cookie result types.
            var sessionState = await berlin.StorageStateAsync();
            await using var newYork = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = jularr.Address.ToString(), TimezoneId = "America/New_York", StorageState = sessionState
            });
            var americanPage = await newYork.NewPageAsync();
            await americanPage.GotoAsync("/Account/Sessions");

            var americanTime = americanPage.Locator("time[data-local-time]").First;
            await Assertions.Expect(americanTime).ToBeVisibleAsync();
            Assert.AreEqual(instant, await americanTime.GetAttributeAsync("datetime"));
            var americanFormatted = await americanTime.EvaluateAsync<string>("element => new Intl.DateTimeFormat(document.documentElement.lang || navigator.language, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(element.getAttribute('datetime')))");
            await Assertions.Expect(americanTime.Locator(".local-time__label")).ToHaveTextAsync(americanFormatted);
            Assert.AreNotEqual(germanText, americanFormatted, "The same UTC instant must be shown with two different hours.");

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

            // HTMX keeps the document and page in place; correcting a stale cookie never reloads it.
            await page.EvaluateAsync("() => { window.__timeZoneDocumentMarker = true; document.cookie = 'jularr_time_zone=UTC; Path=/'; }");
            var link = page.Locator(".calendar__navigation a[href]").First;
            await link.ClickAsync();
            await Assertions.Expect(page.Locator(".calendar__grid")).ToBeVisibleAsync();
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => window.__timeZoneDocumentMarker === true"));
            Assert.AreEqual(expectedZone, await page.EvaluateAsync<string>("() => document.cookie.split('; ').find(c => c.startsWith('jularr_time_zone='))?.split('=')[1] ?? ''"));
        }
    }
}
