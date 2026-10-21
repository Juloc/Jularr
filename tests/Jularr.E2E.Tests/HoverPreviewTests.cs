using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The delayed hover over a cover: after a short rest of the mouse the card opens a panel with the description or the muted trailer.
    [TestClass]
    public sealed class HoverPreviewTests
    {
        private const string Seed = """
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9101, 1, 'Trailer Film', 2024), (9102, 3, 'Plain Book', 2019);
            INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES
                (9101, 'Poster', 'https://127.0.0.1/poster.jpg', TRUE), (9101, 'Backdrop', 'https://127.0.0.1/backdrop.jpg', TRUE),
                (9101, 'Overview', 'A film that has a trailer.', TRUE), (9101, 'Trailer', 'Way9Dexny3w', TRUE),
                (9102, 'Poster', 'https://127.0.0.1/book.jpg', TRUE),
                (9102, 'Overview', 'A long description of a book that has no trailer at all, told in many words so that it fills the panel.', TRUE);
            INSERT INTO "WantedItems" ("WorkId") VALUES (9101), (9102);
            """;

        // The provider's frame is a stand-in that keeps the commands it receives.
        private const string Stub = "<!doctype html><title>trailer</title><script>window.commands = []; addEventListener('message', event => { try { window.commands.push(JSON.parse(event.data).func); } catch { } });</script><body>trailer</body>";

        [TestMethod]
        [Timeout(300_000)]
        public async Task Cover_AfterAShortRest_OpensTheMutedTrailerOrTheDescription_AndNothingElse()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync(Seed);
            await page.RouteAsync("https://www.youtube-nocookie.com/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = Stub }));
            var asked = new List<string>();
            page.Request += (_, request) => { if (request.Url.Contains("/MediaPreview", StringComparison.Ordinal)) asked.Add(request.Url); };

            try
            {
                await page.GotoAsync("/Library");
                var film = page.Locator(".media-card", new() { HasText = "Trailer Film" });
                var book = page.Locator(".media-card", new() { HasText = "Plain Book" });
                var panel = page.Locator(".media-card__preview");

                // The mouse on a button of the card, however long, starts nothing.
                await film.Locator(".media-card__art").HoverAsync();
                await film.Locator(".media-card__bookmark button, .media-card__bookmark a").First.HoverAsync();
                await page.WaitForTimeoutAsync(2500);
                Assert.AreEqual(0, asked.Count, "a control of the card starts no preview");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // Resting on the cover: nothing before the delay, then the panel with the trailer, as high as the cover.
                await page.Mouse.MoveAsync(5, 5);
                await film.Locator(".media-card__art").HoverAsync();
                await page.WaitForTimeoutAsync(1000);
                Assert.AreEqual(0, asked.Count, "nothing is asked before the mouse rested");
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 8000 });
                await Assertions.Expect(panel).ToHaveAttributeAsync("data-preview-mode", "trailer");
                await Assertions.Expect(panel.Locator("h3")).ToContainTextAsync("Trailer Film");
                var cover = (await film.Locator(".media-card__surface").BoundingBoxAsync())!;
                var box = (await panel.BoundingBoxAsync())!;
                Assert.AreEqual(cover.Height, box.Height, 2f);
                Assert.IsTrue(box.Width > cover.Width * 2, "the panel is wider than the cover");

                // One muted frame, very wide and cropped; none of the provider's controls are reachable.
                var frame = panel.Locator("iframe");
                await Assertions.Expect(frame).ToHaveCountAsync(1);
                var source = await frame.GetAttributeAsync("src");
                StringAssert.Contains(source, "youtube-nocookie.com/embed/Way9Dexny3w");
                StringAssert.Contains(source, "mute=1");
                StringAssert.Contains(source, "controls=0");
                var crop = (await panel.Locator(".media-card__preview-media").BoundingBoxAsync())!;
                var frameBox = (await frame.BoundingBoxAsync())!;
                Assert.IsTrue(frameBox.Width > crop.Width * 2 && frameBox.Height > crop.Height, "the frame is larger than its window");
                await Assertions.Expect(panel.Locator(".media-card__preview-media")).ToHaveCSSAsync("overflow", "hidden");
                await Assertions.Expect(frame).ToHaveCSSAsync("pointer-events", "none");

                // Jularr's own controls drive the embed, and the card's own action and heart sit in the foot beside Details.
                var toggle = panel.Locator("[data-trailer-action=toggle]");
                var mute = panel.Locator("[data-trailer-action=mute]");
                await Assertions.Expect(toggle).ToBeVisibleAsync();
                await Assertions.Expect(mute).ToHaveAttributeAsync("aria-label", "Unmute trailer");
                await mute.ClickAsync();
                await Assertions.Expect(mute).ToHaveAttributeAsync("aria-label", "Mute trailer");
                await toggle.ClickAsync();
                await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-label", "Play trailer");
                var provider = page.Frames.Single(item => item.Url.Contains("youtube-nocookie", StringComparison.Ordinal));
                await provider.WaitForFunctionAsync("() => window.commands.length >= 2");
                var commands = await provider.EvaluateAsync<string[]>("() => window.commands");
                Assert.AreEqual("unMute,pauseVideo", string.Join(",", commands));
                await Assertions.Expect(panel.Locator(".media-card__preview-details")).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex("/Details/9101$"));
                await Assertions.Expect(panel.Locator(".media-card__preview-extra form, .media-card__preview-extra a, .media-card__preview-extra button").First).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "hover-preview-trailer");

                // Leaving closes it and the embed goes with it.
                await page.Mouse.MoveAsync(5, 5);
                await Assertions.Expect(panel).ToHaveCountAsync(0);
                Assert.AreEqual(0, page.Frames.Count(item => item.Url.Contains("youtube-nocookie", StringComparison.Ordinal)));

                // A title without a trailer gets its description in the same panel and loads no frame; Escape closes it.
                await book.Locator(".media-card__art").HoverAsync();
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 8000 });
                await Assertions.Expect(panel).ToHaveAttributeAsync("data-preview-mode", "description");
                await Assertions.Expect(panel.Locator(".media-card__description")).ToContainTextAsync("A long description of a book");
                await Assertions.Expect(panel.Locator("iframe")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "hover-preview-description");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // A device without hover (touch) never starts it: nothing is requested.
                var touch = await browser.Context.NewPageAsync();
                await touch.AddInitScriptAsync("const query = window.matchMedia.bind(window); window.matchMedia = text => text.includes('hover: hover') ? query('not all') : query(text);");
                var touchAsked = 0;
                touch.Request += (_, request) => { if (request.Url.Contains("/MediaPreview", StringComparison.Ordinal)) touchAsked++; };
                await touch.GotoAsync("/Library");
                await touch.Locator(".media-card", new() { HasText = "Trailer Film" }).Locator(".media-card__art").HoverAsync();
                await touch.WaitForTimeoutAsync(2800);
                Assert.AreEqual(0, touchAsked);
                await Assertions.Expect(touch.Locator(".media-card__preview")).ToHaveCountAsync(0);
            }
            catch
            {
                await Evidence.SaveAsync(page, "hover-preview-failed");
                throw;
            }
        }
    }
}
