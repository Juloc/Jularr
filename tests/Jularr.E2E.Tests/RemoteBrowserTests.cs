using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The built browser component with deterministic image/input faults; API ownership is tested in BrowserAssistanceTests.
    [TestClass]
    public sealed class RemoteBrowserTests
    {
        [TestMethod]
        [DataRow(1440)]
        [Timeout(90_000)]
        public async Task ImageFailure_RetainsLastPictureAndRecoversWithoutReplayingInput(int width)
        {
            await using var fixture = await Fixture.StartAsync(width);
            var page = fixture.Page;
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            fixture.FrameStatus = 503;
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Reconnecting");
            Assert.IsFalse(string.IsNullOrWhiteSpace(await fixture.Image.GetAttributeAsync("src")));
            await Assertions.Expect(fixture.Input).ToBeDisabledAsync();
            fixture.FrameStatus = 200;
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            await Assertions.Expect(fixture.Input).ToBeEnabledAsync();

            fixture.InputStatus = 503;
            await fixture.Input.FillAsync("one explicit input");
            await page.GetByRole(AriaRole.Button, new() { Name = "Send text", Exact = true }).ClickAsync();
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Disconnected");
            await Assertions.Expect(fixture.Reconnect).ToBeVisibleAsync();
            await Task.Delay(700);
            Assert.AreEqual(1, fixture.Inputs.Count);
            StringAssert.Contains(fixture.Inputs.Single(), "one explicit input");
            await Evidence.SaveAsync(page, "browser-retained-" + width);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [DataRow(1440)]
        [Timeout(90_000)]
        public async Task LoadingPage_ShowsItsStatusAtOnceAndThenTheView(int width)
        {
            await using var fixture = await Fixture.StartAsync(width, frameDelay: 2500);
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("The page is loading, this can take up to 30 seconds");
            await Assertions.Expect(fixture.Input).ToBeDisabledAsync();
            await Assertions.Expect(fixture.Reconnect).ToBeHiddenAsync();
            await Evidence.SaveAsync(fixture.Page, "browser-loading-" + width);
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready", new() { Timeout = 8_000 });
            await Assertions.Expect(fixture.Input).ToBeEnabledAsync();
            await Evidence.SaveAsync(fixture.Page, "browser-loaded-" + width);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        // A browser that is still starting: the page says at once what it waits for (with the seconds), shows a skeleton the size of the picture,
        // and swaps it for the picture when the browser is ready.
        [TestMethod]
        [DataRow(1440)]
        [DataRow(390)]
        [Timeout(90_000)]
        public async Task OpeningBrowser_ShowsItsStepAtOnce_ThenThePictureInTheSameSpace(int width)
        {
            await using var fixture = await Fixture.StartAsync(width, active: false, phase: "starting", states: ["starting", "starting", "starting", "ready"]);
            await Assertions.Expect(fixture.Feedback).ToContainTextAsync("Starting the browser");
            await Assertions.Expect(fixture.Feedback).ToContainTextAsync(" s");
            await Assertions.Expect(fixture.Placeholder).ToBeVisibleAsync();
            await Assertions.Expect(fixture.Progress).ToBeVisibleAsync();
            await Assertions.Expect(fixture.Image).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Input).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Reconnect).ToBeHiddenAsync();
            var skeleton = (await fixture.Placeholder.BoundingBoxAsync())!;
            Assert.IsTrue(await fixture.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The opening view runs wider than the screen.");
            await Evidence.SaveAsync(fixture.Page, "browser-opening-" + width);

            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready", new() { Timeout = 15_000 });
            await Assertions.Expect(fixture.Image).ToBeVisibleAsync();
            await Assertions.Expect(fixture.Placeholder).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Progress).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Input).ToBeEnabledAsync();
            var picture = (await fixture.Image.BoundingBoxAsync())!;
            Assert.AreEqual(skeleton.Width, picture.Width, 1, "The picture is not as wide as its skeleton.");
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [DataRow(1440)]
        [DataRow(390)]
        [Timeout(90_000)]
        public async Task OpeningBrowser_ThatFails_SaysWhyAndOffersReconnect(int width)
        {
            await using var fixture = await Fixture.StartAsync(width, active: false, phase: "starting", states: ["starting", "failed"]);
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("The browser did not start.", new() { Timeout = 15_000 });
            await Assertions.Expect(fixture.Reconnect).ToBeVisibleAsync();
            await Assertions.Expect(fixture.Placeholder).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Progress).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Image).ToBeHiddenAsync();
            Assert.IsTrue(await fixture.Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The failed view runs wider than the screen.");
            await Evidence.SaveAsync(fixture.Page, "browser-opening-failed-" + width);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [Timeout(90_000)]
        public async Task RepeatedFrameFailure_StopsAndOffersOneDirectReconnect()
        {
            await using var fixture = await Fixture.StartAsync(1100);
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            fixture.FrameStatus = 503;
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Disconnected", new() { Timeout = 12_000 });
            await Assertions.Expect(fixture.Reconnect).ToBeVisibleAsync();
            await Assertions.Expect(fixture.Input).ToBeDisabledAsync();
            Assert.IsNotNull(await fixture.Image.GetAttributeAsync("src"));
            Assert.AreEqual(800, await fixture.Image.EvaluateAsync<int>("image => image.naturalWidth"));
            var frames = fixture.Frames;
            await Task.Delay(900);
            Assert.AreEqual(frames, fixture.Frames, "A failed view must stop issuing frame requests.");
            await fixture.Reconnect.ClickAsync();
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            await Assertions.Expect(fixture.Input).ToBeEnabledAsync();
            Assert.AreEqual(1, fixture.Reopens);
            await Evidence.SaveAsync(fixture.Page, "browser-reconnected");
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [DataRow(401)]
        [DataRow(403)]
        [Timeout(90_000)]
        public async Task AuthorizationEnded_ClearsPictureAndStopsControls(int status)
        {
            await using var fixture = await Fixture.StartAsync(1100);
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            fixture.FrameStatus = status;
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Access ended");
            Assert.IsNull(await fixture.Image.GetAttributeAsync("src"));
            await Assertions.Expect(fixture.Reconnect).ToBeHiddenAsync();
            await Assertions.Expect(fixture.Input).ToBeDisabledAsync();
            var frames = fixture.Frames;
            await Task.Delay(700);
            Assert.AreEqual(frames, fixture.Frames);
            Assert.AreEqual(0, fixture.Inputs.Count);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [Timeout(90_000)]
        public async Task ExpiredView_OffersReconnectWithoutPollingItsOldLease()
        {
            await using var fixture = await Fixture.StartAsync(390, active: false);
            await Assertions.Expect(fixture.Input).ToBeDisabledAsync();
            await Assertions.Expect(fixture.Reconnect).ToBeVisibleAsync();
            await Task.Delay(700);
            Assert.AreEqual(0, fixture.Frames);
            await fixture.Reconnect.ClickAsync();
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            Assert.AreEqual(1, fixture.Reopens);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        [TestMethod]
        [DataRow(1440)]
        [Timeout(90_000)]
        public async Task DownloadReconnect_ReopensItsOperationInsteadOfTheSourceHomepage(int width)
        {
            await using var fixture = await Fixture.StartAsync(width, active: false, operationId: 5115);
            await Assertions.Expect(fixture.Reconnect).ToBeVisibleAsync();
            await fixture.Reconnect.ClickAsync();
            await Assertions.Expect(fixture.Feedback).ToHaveTextAsync("Ready");
            Assert.AreEqual(1, fixture.Reopens);
            await Evidence.SaveAsync(fixture.Page, "browser-download-reconnected-" + width);
            CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Problems.ToArray());
        }

        private sealed class Fixture(IPlaywright playwright, IBrowser browser, IPage page, string script, string style, long? operationId, string? phase, string[] states) : IAsyncDisposable
        {
            private string ReopenAddress => operationId is { } id ? "/Admin/Browser?handler=OpenDownloadView&operationId=" + id : "/Admin/Browser?handler=OpenView&acquisitionConnectionId=7";
            public IPage Page { get; } = page;
            public ILocator Feedback => Page.Locator("[data-browser-feedback]");
            public ILocator Image => Page.Locator("[data-browser-image]");
            public ILocator Input => Page.Locator("[data-browser-text]");
            public ILocator Reconnect => Page.GetByRole(AriaRole.Button, new() { Name = "Reconnect", Exact = true });
            public ConcurrentQueue<string> Inputs { get; } = new();
            public ConcurrentQueue<string> Problems { get; } = new();
            public volatile int FrameStatus = 200;
            public volatile int InputStatus = 200;
            private int _frames;
            private int _reopens;
            public int Frames => Volatile.Read(ref _frames);
            public int Reopens => Volatile.Read(ref _reopens);

            // phase: the view is still opening ("starting") or has failed ("failed"); states are what the state endpoint answers one after the other (the last stays).
            public static async Task<Fixture> StartAsync(int width, bool active = true, long? operationId = null, int frameDelay = 0, string? phase = null, params string[] states)
            {
                var playwright = await Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync();
                var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 900 } });
                var root = Path.Combine(JularrInstance.Repository, "src", "Jularr.Frontend");
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "Assets", "Generated", "manifest.json")));
                var script = manifest.RootElement.GetProperty("Pages/Admin/Browser/Browser.entry.ts").GetProperty("file").GetString()!;
                var style = manifest.RootElement.GetProperty("Pages/Admin/Browser/Browser.entry.scss").GetProperty("file").GetString()!;
                // The shell's own styles (colours, type), so what the screenshots show is what an administrator sees.
                var shell = manifest.RootElement.GetProperty("Assets/Shared/shell.entry.scss").GetProperty("file").GetString()!;
                var fixture = new Fixture(playwright, browser, page, script, style, operationId, phase, states) { Shell = shell };
                page.PageError += (_, error) => fixture.Problems.Enqueue(error);
                var build = Path.GetFullPath(Path.Combine(root, "wwwroot", "build"));
                await page.RouteAsync("https://browser.test/**", async route =>
                {
                    var uri = new Uri(route.Request.Url);
                    if (uri.AbsolutePath.StartsWith("/build/", StringComparison.Ordinal))
                    {
                        var asset = Path.GetFullPath(Path.Combine(build, uri.AbsolutePath[7..]));
                        if (!asset.StartsWith(build + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { await route.AbortAsync(); return; }
                        await route.FulfillAsync(new() { Path = asset, ContentType = asset.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/javascript" });
                    }
                    else if (uri.AbsolutePath.EndsWith("/frame", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref fixture._frames);
                        if (frameDelay > 0) await Task.Delay(frameDelay);
                        await route.FulfillAsync(new() { Status = fixture.FrameStatus, ContentType = "image/svg+xml",
                            Body = "<svg xmlns='http://www.w3.org/2000/svg' width='800' height='600'><rect width='800' height='600' fill='#eee'/><text x='40' y='80'>Isolated source page</text></svg>" });
                    }
                    else if (uri.AbsolutePath.EndsWith("/state", StringComparison.Ordinal))
                    {
                        var state = fixture.NextState();
                        await route.FulfillAsync(new() { ContentType = "application/json", Body = state == "failed"
                            ? "{\"phase\":\"failed\",\"error\":\"no_start\",\"width\":800,\"height\":600}" : "{\"phase\":\"" + state + "\",\"error\":null,\"width\":800,\"height\":600}" });
                    }
                    else if (uri.AbsolutePath.EndsWith("/input", StringComparison.Ordinal))
                    {
                        fixture.Inputs.Enqueue(route.Request.PostData ?? "");
                        await route.FulfillAsync(new() { Status = fixture.InputStatus, ContentType = "application/json", Body = "{}" });
                    }
                    else
                    {
                        if (route.Request.Method == "POST")
                        {
                            Assert.AreEqual(fixture.ReopenAddress, uri.PathAndQuery);
                            Interlocked.Increment(ref fixture._reopens);
                            fixture.FrameStatus = 200;
                            active = true;
                        }
                        await route.FulfillAsync(new() { ContentType = "text/html", Body = fixture.Html(active) });
                    }
                });
                await page.GotoAsync("https://browser.test/Admin/Browser");
                return fixture;
            }

            private int _next;
            public string Shell { get; init; } = "";
            public ILocator Placeholder => Page.Locator("[data-browser-placeholder]");
            public ILocator Progress => Page.Locator("[data-browser-progress]");
            private string NextState() => states.Length == 0 ? "ready" : states[Math.Min(Interlocked.Increment(ref _next) - 1, states.Length - 1)];

            // The component as the server writes it (RemoteBrowser.cs), with the step texts of the opening view.
            private string Html(bool active)
            {
                var opening = phase == "starting";
                var failed = phase == "failed";
                var feedback = opening ? "Starting the browser. The first start takes a few seconds…" : failed ? "The browser did not start." : active ? "The page is loading, this can take up to 30 seconds" : "Disconnected";
                var shown = (opening ? "starting" : failed ? "failed" : "ready");
                return $$"""
                <!doctype html><html lang="en" data-skin="clean" data-mode="system"><head><meta charset="utf-8"><link rel="stylesheet" href="/build/{{Shell}}"><link rel="stylesheet" href="/build/{{style}}"></head><body>
                <main id="app-content" data-shell="app">
                <div class="remote-browser" data-remote-browser="/api/v1/admin/browser/assistance/fixture"
                     data-active="{{active.ToString().ToLowerInvariant()}}" data-phase="{{shown}}" data-width="800" data-height="600"
                     data-ready="Ready" data-loading="The page is loading, this can take up to 30 seconds" data-retrying="Reconnecting" data-failed="Disconnected" data-denied="Access ended"
                     data-phase-checking="Checking the source address…" data-phase-waiting="Waiting for the automatic search to let go of the browser…"
                     data-phase-starting="Starting the browser. The first start takes a few seconds…" data-error-no_start="The browser did not start." data-error-failed="The browser could not be opened.">
                  <p data-browser-feedback>{{feedback}}</p>
                  <div class="remote-browser__progress" data-browser-progress aria-hidden="true"><span></span></div>
                  <div data-browser-reconnect {{(opening ? "hidden" : "")}}><form method="post" action="{{System.Net.WebUtility.HtmlEncode(ReopenAddress)}}"><button>Reconnect</button></form></div>
                  <div class="remote-browser__placeholder" data-browser-placeholder aria-hidden="true"></div>
                  <img class="remote-browser__image" data-browser-image alt="Source browser" width="800" height="600">
                  <label for="input">Source input</label><textarea id="input" data-browser-text data-browser-input></textarea>
                  <button type="button" data-browser-send data-browser-input>Send text</button>
                  <button type="button" data-browser-key="Enter" data-browser-input>Enter</button>
                </div><script type="module" src="/build/{{script}}"></script></main></body></html>
                """;
            }

            public async ValueTask DisposeAsync()
            {
                await browser.DisposeAsync();
                playwright.Dispose();
            }
        }
    }
}
