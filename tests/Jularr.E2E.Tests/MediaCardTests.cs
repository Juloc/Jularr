using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class MediaCardTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task Cover_ResponsiveLayout_KeepsImagesProgressAndEqualActionsInside()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // No external trailer needed.
            await page.GotoAsync("/Design?pane=cards");
            var cards = page.Locator(".media-card");
            await Assertions.Expect(cards).ToHaveCountAsync(8);
            foreach (var width in new[] { 1280, 390 })
            {
                await page.SetViewportSizeAsync(width, 844);
                var card = cards.First;
                await card.Locator(".media-card__surface").FocusAsync();
                var cover = await card.BoundingBoxAsync();
                var image = await card.Locator(".media-image").BoundingBoxAsync();
                var progress = await card.Locator("progress").BoundingBoxAsync();
                var actions = card.Locator(".media-card__actions > .button");
                var primary = await actions.Nth(0).BoundingBoxAsync();
                var details = await actions.Nth(1).BoundingBoxAsync();
                Assert.IsNotNull(cover);
                Assert.IsNotNull(image);
                Assert.IsNotNull(progress);
                Assert.IsNotNull(primary);
                Assert.IsNotNull(details);
                Assert.AreEqual(cover.Width, image.Width, .5f);
                Assert.AreEqual(12f, progress.Height, .5f);
                Assert.AreEqual(cover.Y + cover.Height, progress.Y + progress.Height, .5f);
                Assert.AreEqual(primary.Width, details.Width, .5f);
                Assert.AreEqual(primary.Height, details.Height, .5f);
            }

            // Shared preview and local bookmark.
            var bookmark = cards.First.Locator("[data-design-bookmark]");
            await bookmark.ClickAsync();
            await Assertions.Expect(bookmark).ToHaveAttributeAsync("aria-pressed", "true");
            await cards.First.GetByRole(AriaRole.Button, new() { Name = "Details", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("dialog[open]")).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
        }
    }
}
