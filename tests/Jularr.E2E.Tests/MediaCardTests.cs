using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class MediaCardTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task Cover_ResponsiveLayout_KeepsTitleActionStatusAndProgressStable()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            await page.GotoAsync("/Design?pane=cards");
            var cards = page.Locator(".media-card");
            await Assertions.Expect(cards).ToHaveCountAsync(8);
            foreach (var width in new[] { 1280, 390 })
            {
                await page.SetViewportSizeAsync(width, 844);
                var card = cards.First;
                var poster = card.Locator(".media-card__surface");
                var title = card.Locator(".media-card__title a");
                var action = card.Locator(".media-card__actions > .button");
                var badge = card.Locator(".media-availability-badge");

                await Assertions.Expect(title).ToBeVisibleAsync();
                await Assertions.Expect(title).ToHaveAttributeAsync("href", "/Design?pane=cards");
                await Assertions.Expect(action).ToHaveCountAsync(1);
                await Assertions.Expect(badge).ToBeVisibleAsync();
                await Assertions.Expect(card.GetByRole(AriaRole.Button, new() { Name = "Details", Exact = true })).ToHaveCountAsync(0);

                var before = await poster.BoundingBoxAsync();
                var image = await card.Locator(".media-image").BoundingBoxAsync();
                var progress = await card.Locator("progress").BoundingBoxAsync();
                var textBox = await card.Locator(".media-card__details").BoundingBoxAsync();
                Assert.IsNotNull(before);
                Assert.IsNotNull(image);
                Assert.IsNotNull(progress);
                Assert.IsNotNull(textBox);
                Assert.AreEqual(before.Width, image.Width, .5f);
                Assert.AreEqual(before.Height, image.Height, .5f);
                Assert.AreEqual(before.Y + before.Height, progress.Y + progress.Height, .5f);
                Assert.IsTrue(textBox.Y >= before.Y + before.Height - .5f);

                await poster.FocusAsync();
                await Assertions.Expect(action).ToBeVisibleAsync();
                await Assertions.Expect(badge.Locator(".media-availability-badge__body")).ToHaveCSSAsync("max-width", "152px");
                var after = await poster.BoundingBoxAsync();
                Assert.IsNotNull(after);
                Assert.AreEqual(before.Width, after.Width, .5f);
                Assert.AreEqual(before.Height, after.Height, .5f);
            }

            var bookmark = cards.First.Locator("[data-design-bookmark]");
            await bookmark.ClickAsync();
            await Assertions.Expect(bookmark).ToHaveAttributeAsync("aria-pressed", "true");

            // The existing status badge retains its preview link and keyboard behavior.
            await cards.First.Locator(".media-availability-badge").ClickAsync();
            await Assertions.Expect(page.Locator("dialog[open]")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
        }
    }
}
