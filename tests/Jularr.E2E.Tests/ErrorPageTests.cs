using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The styled error pages of both skins and the stand-in for a missing cover.
    [TestClass]
    public sealed class ErrorPageTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task UnknownAddress_ShowsThe404Page_InCleanAndOriginal()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Clean, inside the application shell: the mark large above the bold title, the line and the two actions.
            var response = await page.GotoAsync("/does-not-exist");
            Assert.AreEqual(404, response!.Status);
            await Assertions.Expect(page.Locator(".app-layout .error-state")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".error-state__title")).ToHaveTextAsync("404 Not Found");
            await Assertions.Expect(page.Locator(".error-state__mark svg")).ToBeVisibleAsync();
            Assert.IsTrue((await page.Locator(".error-state__mark").BoundingBoxAsync())!.Width >= 130);
            await Assertions.Expect(page.Locator(".error-state__art")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator(".error-state__actions--clean .button")).ToHaveCountAsync(2);

            // The home link of the page leaves the error behind.
            await page.Locator(".error-state__actions--clean a", new() { HasText = "Home" }).ClickAsync();
            await Assertions.Expect(page.Locator(".error-state")).ToHaveCountAsync(0);

            // A link inside the application that leads nowhere swaps the error page in place and keeps the shell.
            await page.EvaluateAsync("document.querySelector('#app-content').insertAdjacentHTML('beforeend', '<a id=\"dead\" href=\"/dead-link\">x</a>')");
            await page.Locator("#dead").ClickAsync();
            await Assertions.Expect(page.Locator(".app-layout .error-state__title")).ToHaveTextAsync("404 Not Found");

            // Original: its artwork fills the whole page, with exactly two buttons and no other visible text.
            await page.EvaluateAsync("localStorage.setItem('jularr-ui-skin', 'original')");
            foreach (var (width, height) in new[] { (1440, 900), (390, 844) })
            {
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync("/does-not-exist");
                var art = page.Locator(".error-state__art");
                await Assertions.Expect(art).ToBeVisibleAsync();
                var box = await art.BoundingBoxAsync();
                Assert.AreEqual(width, box!.Width, 1);
                Assert.AreEqual(height, box.Height, 1);
                var image = await art.EvaluateAsync<string>("element => getComputedStyle(element).backgroundImage");
                StringAssert.Matches(image, new Regex(width > 640 ? @"404-desktop-.*\.webp" : @"404-mobile-.*\.webp"));
                await Assertions.Expect(page.Locator(".error-state__actions--original .button")).ToHaveCountAsync(2);
                await Assertions.Expect(page.Locator(".error-state__actions--original .button").First).ToHaveTextAsync("Try again");
                await Assertions.Expect(page.Locator(".error-state__actions--clean")).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator(".error-state__mark")).ToBeHiddenAsync();
                var visibleText = await page.Locator(".error-state").EvaluateAsync<string>(
                    "root => [...root.querySelectorAll('*')].filter(e => e.children.length === 0 && e.getBoundingClientRect().width > 4 && getComputedStyle(e).display !== 'none').map(e => e.textContent.trim()).filter(Boolean).join('|')");
                Assert.AreEqual("Try again|Go to home page", visibleText);
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task UnhandledException_ShowsThe500Page_WithoutDetails_AndApiKeepsItsAnswer()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);

            var response = await page.GotoAsync("/Development/Fail");
            Assert.AreEqual(500, response!.Status);
            await Assertions.Expect(page.Locator(".error-state__title")).ToHaveTextAsync("500 Internal Server Error");
            var content = await page.ContentAsync();
            Assert.IsFalse(content.Contains("secret detail", StringComparison.OrdinalIgnoreCase), "the exception text must not be shown");
            Assert.IsFalse(content.Contains("InvalidOperationException", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("Program.cs", StringComparison.Ordinal));
            await Assertions.Expect(page.Locator(".error-state__reference")).ToContainTextAsync("Reference ");
            // Retry reads the address that failed again.
            await Assertions.Expect(page.Locator(".error-state__actions--clean a", new() { HasText = "Try again" })).ToHaveAttributeAsync("href", "/Development/Fail");

            // An unknown API route is not a page: the plain 404 stays, even for a client that accepts HTML.
            var api = await page.APIRequest.GetAsync("/api/v1/does-not-exist", new() { Headers = new Dictionary<string, string> { ["Accept"] = "text/html" } });
            Assert.AreEqual(404, api.Status);
            Assert.IsFalse((await api.TextAsync()).Contains("error-state", StringComparison.Ordinal));
            Assert.IsFalse(api.Headers.TryGetValue("content-type", out var type) && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task TitleWithoutCover_ShowsThePlaceholder_AndABrokenImageSwapsToIt()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9301, 1, 'No Cover Film', 2020), (9302, 1, 'Dead Cover Film', 2021);
                INSERT INTO "WantedItems" ("WorkId") SELECT "Id" FROM "Works" WHERE "Id" BETWEEN 9301 AND 9302;
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (9302, 'Poster', '/missing/poster.jpg', true);
                """);

            await page.GotoAsync("/Library");
            var cards = page.Locator(".media-card");
            await Assertions.Expect(cards).ToHaveCountAsync(2);

            // The title without any poster renders the placeholder from the server; the one whose image does not load swaps to it in the browser.
            await Assertions.Expect(page.Locator(".media-card__art .no-cover")).ToHaveCountAsync(2);
            await Assertions.Expect(page.Locator(".media-card__art img.media-image")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".media-card__art .no-cover svg")).ToHaveCountAsync(2);
            var art = await cards.First.Locator(".media-card__art").BoundingBoxAsync();
            var cover = await cards.First.Locator(".no-cover").BoundingBoxAsync();
            Assert.AreEqual(art!.Width, cover!.Width, 1);
            Assert.AreEqual(art.Height, cover.Height, 1);
        }
    }
}
