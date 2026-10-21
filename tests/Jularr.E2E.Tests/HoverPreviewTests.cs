using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The preview of a cover: after a rest of 550 ms of the mouse the card opens a panel over the row with the description or the muted trailer; a touch device
    // starts it by itself when a card lingers, unless the profile switched that off.
    [TestClass]
    public sealed class HoverPreviewTests
    {
        private const string Seed = """
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "RuntimeMinutes") VALUES (9101, 1, 'Trailer Film', 2024, 106), (9102, 3, 'Plain Book', 2019, NULL);
            INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES
                (9101, 'Poster', 'https://127.0.0.1/poster.jpg', TRUE), (9101, 'Backdrop', 'https://127.0.0.1/backdrop.jpg', TRUE),
                (9101, 'Overview', 'A film that has a trailer.', TRUE), (9101, 'Trailer', 'Way9Dexny3w', TRUE),
                (9102, 'Poster', 'https://127.0.0.1/book.jpg', TRUE),
                (9102, 'Overview', 'A long description of a book that has no trailer at all, told in many words so that it fills the panel.', TRUE);
            INSERT INTO "WantedItems" ("WorkId") VALUES (9101), (9102);
            """;

        // The provider's frame is a stand-in: it keeps the commands it receives and, once a listener registered, reports a time as the real player does.
        private const string Stub = "<!doctype html><title>trailer</title><script>window.commands = []; addEventListener('message', event => { try { const message = JSON.parse(event.data); if (message.event === 'listening') parent.postMessage(JSON.stringify({ event: 'infoDelivery', info: { currentTime: 12.4, duration: 144, playerState: 1 } }), '*'); else window.commands.push(message.func); } catch { } });</script><body>trailer</body>";

        private static async Task<List<Box>> BoxesAsync(ILocator cards)
        {
            var boxes = new List<Box>();
            foreach (var card in await cards.AllAsync())
            {
                var box = (await card.BoundingBoxAsync())!;
                boxes.Add(new Box(box.X, box.Y, box.Width, box.Height));
            }

            return boxes;
        }

        private sealed record Box(float X, float Y, float Width, float Height);

        [TestMethod]
        [Timeout(300_000)]
        public async Task Cover_AfterAShortRest_OpensTheMutedTrailerOrTheDescription_OverTheRow_AndNothingElse()
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

                // The page settles first: the strip of the shell that loads late moves the row by a few pixels.
                await page.WaitForTimeoutAsync(1200);
                var cards = page.Locator(".media-card");
                var film = page.Locator(".media-card", new() { HasText = "Trailer Film" });
                var book = page.Locator(".media-card", new() { HasText = "Plain Book" });
                var panel = page.Locator(".media-card__preview");
                var before = await BoxesAsync(cards);

                // The mouse on a button of the card, however long, starts nothing.
                await film.Locator(".media-card__art").HoverAsync();
                await film.Locator(".media-card__bookmark button, .media-card__bookmark a").First.HoverAsync();
                await page.WaitForTimeoutAsync(1500);
                Assert.AreEqual(0, asked.Count, "a control of the card starts no preview");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // Resting on the cover: nothing before the delay of 550 ms, then the panel with the trailer, as high as the card and 1.84 times as wide.
                await page.Mouse.MoveAsync(5, 5);
                await film.Locator(".media-card__art").HoverAsync();
                await page.WaitForTimeoutAsync(300);
                Assert.AreEqual(0, asked.Count, "nothing is asked before the mouse rested");
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 8000 });
                await Assertions.Expect(panel).ToHaveAttributeAsync("data-preview-mode", "trailer");
                await page.WaitForTimeoutAsync(300); // the panel fades in over 170 ms; its box counts when it has settled
                await Assertions.Expect(panel.Locator("h3")).ToContainTextAsync("Trailer Film");
                var card = (await film.BoundingBoxAsync())!;
                var box = (await panel.BoundingBoxAsync())!;
                Assert.AreEqual(card.Height, box.Height, 2f);
                Assert.AreEqual(card.Y, box.Y, 2f);
                Assert.AreEqual(card.Width * 1.84f, box.Width, 3f);
                Assert.IsTrue(box.X >= 0 && box.X + box.Width <= page.ViewportSize!.Width, "the panel stays in the viewport");

                // The cards of the row keep their width and position; the cover under the panel is hidden, not removed.
                CollectionAssert.AreEqual(before, await BoxesAsync(cards), "no card moves or changes its width while the preview is open");
                await Assertions.Expect(film.Locator(".media-card__surface")).ToHaveCSSAsync("opacity", "0");

                // The trailer frame is 16:9 inside the panel's padding; the facts and the description follow.
                var media = panel.Locator(".media-card__preview-media");
                var crop = (await media.BoundingBoxAsync())!;
                Assert.AreEqual(crop.Width * 9f / 16f, crop.Height, 1.5f);
                Assert.AreEqual(box.Width - 14f, crop.Width, 1.5f);
                await Assertions.Expect(panel.Locator(".media-card__preview-meta")).ToContainTextAsync("2024");
                await Assertions.Expect(panel.Locator(".media-card__preview-meta")).ToContainTextAsync("1h 46m");
                await Assertions.Expect(panel.Locator(".media-card__description")).ToContainTextAsync("A film that has a trailer.");

                // One muted frame, very wide and cropped; none of the provider's controls are reachable.
                var frame = panel.Locator("iframe");
                await Assertions.Expect(frame).ToHaveCountAsync(1);
                var source = await frame.GetAttributeAsync("src");
                StringAssert.Contains(source, "youtube-nocookie.com/embed/Way9Dexny3w");
                StringAssert.Contains(source, "mute=1");
                StringAssert.Contains(source, "controls=0");
                StringAssert.Contains(source, "enablejsapi=1");
                var frameBox = (await frame.BoundingBoxAsync())!;
                Assert.IsTrue(frameBox.Width > crop.Width * 2 && frameBox.Height > crop.Height, "the frame is larger than its window");
                await Assertions.Expect(media).ToHaveCSSAsync("overflow", "hidden");
                await Assertions.Expect(frame).ToHaveCSSAsync("pointer-events", "none");

                // Jularr's own controls: pause top left, sound top right (25 px discs); they drive the embed.
                var toggle = panel.Locator("[data-trailer-action=toggle]");
                var mute = panel.Locator("[data-trailer-action=mute]");
                var toggleBox = (await toggle.BoundingBoxAsync())!;
                var muteBox = (await mute.BoundingBoxAsync())!;
                Assert.AreEqual(25f, toggleBox.Width, 0.6f);
                Assert.AreEqual(25f, muteBox.Height, 0.6f);
                Assert.AreEqual(crop.X + 7f, toggleBox.X, 1.5f);
                Assert.AreEqual(crop.Y + 7f, toggleBox.Y, 1.5f);
                Assert.AreEqual(crop.X + crop.Width - 7f, muteBox.X + muteBox.Width, 1.5f);
                await Assertions.Expect(mute).ToHaveAttributeAsync("aria-label", "Unmute trailer");
                await mute.ClickAsync();
                await Assertions.Expect(mute).ToHaveAttributeAsync("aria-label", "Mute trailer");
                await toggle.ClickAsync();
                await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-label", "Play trailer");
                var provider = page.Frames.Single(item => item.Url.Contains("youtube-nocookie", StringComparison.Ordinal));
                await provider.WaitForFunctionAsync("() => window.commands.length >= 2");
                var commands = await provider.EvaluateAsync<string[]>("() => window.commands");
                Assert.AreEqual("unMute,pauseVideo", string.Join(",", commands));

                // The progress is the player's own: line and time from what it reports.
                await Assertions.Expect(panel.Locator("[data-trailer-time]")).ToHaveTextAsync("0:12 / 2:24");
                await Assertions.Expect(panel).ToHaveAttributeAsync("data-progress", "live");

                // The foot: the card's heart (and its action, where it has one) and Details, 31 px discs.
                var details = panel.Locator(".media-card__preview-details");
                await Assertions.Expect(details).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex("/Details/9101$"));
                var detailsBox = (await details.BoundingBoxAsync())!;
                Assert.AreEqual(31f, detailsBox.Width, 0.6f);
                Assert.AreEqual(31f, detailsBox.Height, 0.6f);
                var save = panel.Locator(".media-card__preview-save");
                await Assertions.Expect(save).ToBeVisibleAsync();
                Assert.AreEqual(31f, (await save.BoundingBoxAsync())!.Width, 0.6f);
                if (await panel.Locator(".media-card__preview-primary").CountAsync() > 0) Assert.IsTrue((await panel.Locator(".media-card__preview-primary").BoundingBoxAsync())!.Width >= 104.5f);
                await Evidence.SaveAsync(page, "hover-preview-trailer");

                // Leaving closes it, the embed goes with it and the cover shows again.
                await page.Mouse.MoveAsync(5, 5);
                await Assertions.Expect(panel).ToHaveCountAsync(0);
                Assert.AreEqual(0, page.Frames.Count(item => item.Url.Contains("youtube-nocookie", StringComparison.Ordinal)));
                await Assertions.Expect(film.Locator(".media-card__surface")).ToHaveCSSAsync("opacity", "1");
                CollectAssert(before, await BoxesAsync(cards));

                // A title without a trailer gets its description in the same panel and loads no frame; Escape closes it.
                await book.Locator(".media-card__art").HoverAsync();
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 8000 });
                await Assertions.Expect(panel).ToHaveAttributeAsync("data-preview-mode", "description");
                await Assertions.Expect(panel.Locator(".media-card__description")).ToContainTextAsync("A long description of a book");
                await Assertions.Expect(panel.Locator("iframe")).ToHaveCountAsync(0);
                await page.WaitForTimeoutAsync(300);
                var bookCard = (await book.BoundingBoxAsync())!;
                var descriptionBox = (await panel.BoundingBoxAsync())!;
                Assert.AreEqual(bookCard.Height, descriptionBox.Height, 2f);
                await Evidence.SaveAsync(page, "hover-preview-description");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // The keyboard focus opens it, too.
                await page.Mouse.MoveAsync(5, 5);
                await page.Keyboard.PressAsync("Tab");
                await film.Locator(".media-card__surface").FocusAsync();
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 8000 });
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // A device without hover (touch) never starts it by hovering: nothing is requested.
                var touch = await browser.Context.NewPageAsync();
                await touch.AddInitScriptAsync("const query = window.matchMedia.bind(window); window.matchMedia = text => text.includes('hover: hover') ? query('not all') : query(text);");
                var touchAsked = 0;
                touch.Request += (_, request) => { if (request.Url.Contains("/MediaPreview", StringComparison.Ordinal)) touchAsked++; };
                await touch.GotoAsync("/Library");
                await touch.EvaluateAsync("() => document.querySelector('[data-autoplay-previews]').setAttribute('data-autoplay-previews', 'false')");
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

        private static void CollectAssert(List<Box> expected, List<Box> actual) => CollectionAssert.AreEqual(expected, actual, "the cards are where they were");

        [TestMethod]
        [Timeout(300_000)]
        public async Task Touch_ACardThatLingers_StartsItsPreviewAfterTwoSeconds_UnlessTheProfileSwitchedItOff()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync(Seed);
            await page.RouteAsync("https://www.youtube-nocookie.com/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = Stub }));

            try
            {
                // A phone: no hover, a narrow screen.
                await page.SetViewportSizeAsync(390, 800);
                await page.AddInitScriptAsync("const query = window.matchMedia.bind(window); window.matchMedia = text => text.includes('hover: hover') ? query('not all') : text.includes('hover: none') || text.includes('pointer: coarse') ? query('all') : query(text);");
                var asked = new List<(string Url, DateTime At)>();
                page.Request += (_, request) => { if (request.Url.Contains("/MediaPreview", StringComparison.Ordinal)) asked.Add((request.Url, DateTime.UtcNow)); };
                var loaded = DateTime.UtcNow;
                await page.GotoAsync("/Library");
                await Assertions.Expect(page.Locator("[data-autoplay-previews]")).ToHaveAttributeAsync("data-autoplay-previews", "true");
                var panel = page.Locator(".media-card__preview");

                // Nothing is loaded while the card has not lingered yet; then the panel opens by itself, muted, one at a time.
                await page.WaitForTimeoutAsync(1200);
                Assert.AreEqual(0, asked.Count, "no data before the dwell is over");
                await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 10000 });
                Assert.IsTrue((asked[0].At - loaded).TotalMilliseconds >= 1800, "the preview waited for about two seconds");
                await Assertions.Expect(panel).ToHaveCountAsync(1);
                if (await panel.GetAttributeAsync("data-preview-mode") == "trailer") await Assertions.Expect(panel.Locator("iframe")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("mute=1"));
                else await Assertions.Expect(panel.Locator("iframe")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "hover-preview-touch");

                // Scrolling away ends it.
                await page.EvaluateAsync("() => document.dispatchEvent(new Event('scroll'))");
                await Assertions.Expect(panel).ToHaveCountAsync(0);

                // The profile's switch: off means no automatic preview.
                await jularr.RunSqlAsync("""INSERT INTO "ProfilePlaybackPreferences" ("ProfileId", "AutoplayPreviews") SELECT "Id", FALSE FROM "Profiles" ON CONFLICT ("ProfileId") DO UPDATE SET "AutoplayPreviews" = FALSE;""");
                asked.Clear();
                await page.GotoAsync("/Library");
                await Assertions.Expect(page.Locator("[data-autoplay-previews]")).ToHaveAttributeAsync("data-autoplay-previews", "false");
                await page.WaitForTimeoutAsync(4000);
                Assert.AreEqual(0, asked.Count, "the switch is off: nothing starts by itself");
                await Assertions.Expect(panel).ToHaveCountAsync(0);
            }
            catch
            {
                await Evidence.SaveAsync(page, "hover-preview-touch-failed");
                throw;
            }
        }
    }
}
