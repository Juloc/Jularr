using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The app is installable: a manifest with working icons and a worker that only passes requests through.
    // With the worker controlling the page, setup, sign-in, page swaps, the live stream and range requests behave as without it.
    [TestClass]
    public sealed class PwaTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task InstalledPwa_ShowsUpdateButNeverReloadsUntilUserChooses()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;

            // Model Chrome's installed-app display mode without depending on a platform launcher.
            await browser.Context.AddInitScriptAsync("""
                (() => {
                    const native = window.matchMedia.bind(window);
                    window.matchMedia = query => query === '(display-mode: standalone)'
                        ? { matches: true, media: query, addEventListener() {}, removeEventListener() {} }
                        : native(query);
                })();
                """);
            await browser.Context.RouteAsync("**/pwa/version", route => route.FulfillAsync(new()
            {
                ContentType = "application/json",
                Body = """{"build":"AAAAAAAAAAAAAAAA"}"""
            }));

            await page.GotoAsync("/");
            var notice = page.Locator("[data-pwa-update]");
            await Assertions.Expect(notice).ToBeVisibleAsync();
            // The notification cannot force a reload; it leaves an active document marker intact.
            await page.EvaluateAsync("() => { window.pwaDocumentMarker = 1; }");
            await notice.Locator("[data-pwa-later]").ClickAsync();
            await Assertions.Expect(notice).ToBeHiddenAsync();
            Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.pwaDocumentMarker"));
            await page.EvaluateAsync("() => window.dispatchEvent(new Event('pageshow'))");
            await Assertions.Expect(notice).ToBeHiddenAsync();
            Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.pwaDocumentMarker"));
            CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task ExpiredSession_FromBoostedLink_SignsInAndRestoresDeepLink()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Jobs");

            var paused = page.GetByRole(AriaRole.Navigation, new() { Name = "Job states" }).GetByRole(AriaRole.Link, new() { Name = "Paused" });
            var href = await paused.GetAttributeAsync("href");
            Assert.IsFalse(string.IsNullOrWhiteSpace(href));
            var requested = new Uri(jularr.Address, href).PathAndQuery;

            // The document is still running its old session when the browser loses the cookie.
            await browser.Context.ClearCookiesAsync();
            await paused.ClickAsync();
            await page.WaitForURLAsync(url => url.Contains("/Account/Login?ReturnUrl=", StringComparison.Ordinal));

            var query = new Uri(page.Url).Query.TrimStart('?');
            var encodedReturn = query.Split('&').Single(part => part.StartsWith("ReturnUrl=", StringComparison.OrdinalIgnoreCase))["ReturnUrl=".Length..];
            Assert.AreEqual(requested, Uri.UnescapeDataString(encodedReturn), "The PWA login must preserve the destination of the interrupted GET.");

            await page.FillAsync("[name=Username]", Owner.Username);
            await page.FillAsync("[name=Password]", Owner.Password);
            await page.Locator("main form button[type=submit]").First.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == requested);
            Assert.AreEqual(requested, new Uri(page.Url).PathAndQuery);
            await Assertions.Expect(page.Locator("#app-content")).ToBeVisibleAsync();
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task WorkerRegistered_ManifestAndIconsServed_AndNavigationLoginSwapsLiveAndRangesStillWork()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;

            // The first page (setup) already offers the manifest and the worker takes over before anyone signs in.
            await page.GotoAsync("/");
            await page.WaitForURLAsync("**/Setup**");
            await Assertions.Expect(page.Locator("link[rel=manifest]")).ToHaveAttributeAsync("href", "/manifest.webmanifest");
            await Assertions.Expect(page.Locator("meta[name=apple-mobile-web-app-capable]")).ToHaveAttributeAsync("content", "yes");
            await Assertions.Expect(page.Locator("meta[name=theme-color]")).ToHaveCountAsync(1);
            Assert.AreEqual("activated", await page.EvaluateAsync<string>("""
                async () => {
                    const registration = await navigator.serviceWorker.ready;
                    await new Promise(resolve => registration.active.state === 'activated' ? resolve() : registration.active.addEventListener('statechange', () => registration.active.state === 'activated' && resolve()));
                    return registration.active.state;
                }
                """));
            Assert.AreEqual("/", await page.EvaluateAsync<string>("async () => new URL((await navigator.serviceWorker.ready).scope).pathname"));

            // Setup and sign-in work with the worker registered, and the signed-out sign-in page carries the same tags.
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Account/Logout");
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Sign out"));
            await page.GotoAsync("/Account/Login");
            await Assertions.Expect(page.Locator("link[rel=manifest]")).ToHaveAttributeAsync("href", "/manifest.webmanifest");
            await Assertions.Expect(page.Locator("link[rel=apple-touch-icon]")).ToHaveCountAsync(1);
            await page.FillAsync("[name=Username]", Owner.Username);
            await page.FillAsync("[name=Password]", Owner.Password);
            await page.Locator("form button[type=submit]").First.ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));

            // The manifest names the app and every icon it lists resolves to an image.
            var manifest = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
                async () => {
                    const response = await fetch('/manifest.webmanifest');
                    const body = await response.json();
                    body.contentType = response.headers.get('content-type');
                    body.icons = await Promise.all(body.icons.map(async icon => {
                        const picture = await fetch(icon.src);
                        return { ...icon, status: picture.status, type: picture.headers.get('content-type') };
                    }));
                    return body;
                }
                """);
            StringAssert.StartsWith(manifest.GetProperty("contentType").GetString(), "application/manifest+json");
            Assert.AreEqual("Jularr", manifest.GetProperty("name").GetString());
            Assert.AreEqual("standalone", manifest.GetProperty("display").GetString());
            Assert.AreEqual("/", manifest.GetProperty("start_url").GetString());
            Assert.AreEqual("/", manifest.GetProperty("scope").GetString());
            var icons = manifest.GetProperty("icons").EnumerateArray().ToArray();
            Assert.IsTrue(icons.Any(icon => icon.GetProperty("sizes").GetString() == "192x192"));
            Assert.IsTrue(icons.Any(icon => icon.GetProperty("sizes").GetString() == "512x512" && icon.GetProperty("purpose").GetString() == "any"));
            Assert.IsTrue(icons.Any(icon => icon.GetProperty("purpose").GetString() == "maskable"));
            foreach (var icon in icons)
            {
                Assert.AreEqual(200, icon.GetProperty("status").GetInt32(), icon.GetProperty("src").GetString());
                Assert.AreEqual("image/png", icon.GetProperty("type").GetString());
            }

            // The worker file is served from the root with its scope and is never cached.
            var worker = await page.EvaluateAsync<string[]>("""
                async () => {
                    const response = await fetch('/service-worker.js');
                    return [String(response.status), response.headers.get('service-worker-allowed'), response.headers.get('cache-control'), response.headers.get('content-type')];
                }
                """);
            CollectionAssert.AreEqual(new[] { "200", "/", "no-cache", "text/javascript" }, worker.Select(value => value?.Split(';')[0]).ToArray());

            // After a reload the worker controls the page; the page does not notice it.
            await page.GotoAsync("/Admin/Jobs");
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => navigator.serviceWorker.controller !== null"), "The worker does not control the page.");

            // A link swaps the page content in place (the document stays) with the worker in between.
            await page.EvaluateAsync("() => { window.documentMarker = 'same document'; }");
            await Steps.FollowAsync(page, page.GetByRole(AriaRole.Navigation, new() { Name = "Job states" }).GetByRole(AriaRole.Link, new() { Name = "Paused" }));
            StringAssert.Contains(page.Url, "Paused", StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual("same document", await page.EvaluateAsync<string>("() => window.documentMarker"));

            // The live stream opens and delivers its snapshot, and a range request is answered by the server with a partial body.
            Assert.AreEqual("snapshot", await page.EvaluateAsync<string>("""
                () => new Promise((resolve, reject) => {
                    const stream = new EventSource('/live/events');
                    const timer = setTimeout(() => { stream.close(); reject(new Error('No snapshot arrived.')); }, 15000);
                    stream.addEventListener('snapshot', () => { clearTimeout(timer); stream.close(); resolve('snapshot'); });
                })
                """));
            var range = await page.EvaluateAsync<string[]>($$"""
                async () => {
                    const response = await fetch({{System.Text.Json.JsonSerializer.Serialize(icons[0].GetProperty("src").GetString())}}, { headers: { Range: 'bytes=0-7' } });
                    return [String(response.status), response.headers.get('content-range'), String((await response.arrayBuffer()).byteLength)];
                }
                """);
            Assert.AreEqual("206", range[0]);
            StringAssert.StartsWith(range[1], "bytes 0-7/");
            Assert.AreEqual("8", range[2]);

            // A full navigation (address bar) after all that still lands on the page and nothing failed on the way.
            await page.GotoAsync("/Admin/Instance");
            await Assertions.Expect(page.Locator("#app-content")).ToBeVisibleAsync();
            CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
        }
    }
}
