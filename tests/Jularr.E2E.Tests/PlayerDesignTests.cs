using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class PlayerDesignTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task DesktopPreview_SettingsAndSeeking_KeepStateLocal()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            var writes = new List<string>();
            page.Request += (_, request) => { if (request.Method is not ("GET" or "HEAD")) writes.Add(request.Url); };
            await page.SetViewportSizeAsync(1440, 1000);
            await page.GotoAsync("/Design?pane=player#player-preview");

            // No playback client or stream.
            await Assertions.Expect(page.Locator(".player")).ToHaveAttributeAsync("data-start-url", "");
            Assert.IsNull(await page.Locator("video").GetAttributeAsync("src"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Play", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            var settings = page.Locator("#player-settings");
            await Assertions.Expect(settings).ToBeVisibleAsync();
            var initialDialog = (await settings.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(0, 0);
            foreach (var button in await page.Locator(".player__options > button").AllAsync())
            {
                var bounds = (await button.BoundingBoxAsync())!;
                Assert.AreEqual(bounds.Width, bounds.Height, 1);
                await Assertions.Expect(button).ToHaveCSSAsync("border-radius", "50%");
                await Assertions.Expect(button).ToHaveCSSAsync("background-color", "rgb(255, 255, 255)");
            }
            await Assertions.Expect(settings.Locator("h2")).ToHaveCountAsync(0);
            var tabs = (await settings.Locator("header .navigation-tabs").BoundingBoxAsync())!;
            var close = (await settings.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).BoundingBoxAsync())!;
            Assert.AreEqual(tabs.Y, close.Y, 1);
            Assert.AreEqual(tabs.Height, close.Height, 1);
            Assert.AreEqual(close.Width, close.Height, 1);

            // Native picker keeps keyboard selection.
            var audio = settings.GetByRole(AriaRole.Combobox, new() { Name = "Audio", Exact = true });
            await Assertions.Expect(audio).ToHaveCSSAsync("appearance", "base-select");
            await Assertions.Expect(audio).ToHaveCSSAsync("min-height", "36px");
            await audio.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(audio).ToHaveJSPropertyAsync("value", "0");
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(audio).ToHaveValueAsync("1");
            await audio.SelectOptionAsync("0");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true })).ToBeVisibleAsync();

            // Only changed sample defaults.
            var save = settings.Locator("[data-action=save-defaults]");
            await Assertions.Expect(save).ToBeDisabledAsync();
            await settings.GetByRole(AriaRole.Link, new() { Name = "Subtitles", Exact = true }).ClickAsync();
            await settings.GetByLabel("Subtitle behavior", new() { Exact = true }).SelectOptionAsync("full");
            await Assertions.Expect(save).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("[data-action=subtitle-toggle]")).ToHaveAttributeAsync("aria-pressed", "true");
            await settings.GetByLabel("Second subtitle track", new() { Exact = true }).SelectOptionAsync("german");
            await Assertions.Expect(page.Locator("[data-caption=secondary]")).ToBeVisibleAsync();
            await save.ClickAsync();
            await Assertions.Expect(save).ToBeDisabledAsync();
            await Assertions.Expect(settings.Locator("[data-settings-feedback]")).ToContainTextAsync("no profile was updated");

            // Settings and seek preserve Play/Pause.
            await settings.GetByRole(AriaRole.Link, new() { Name = "Playback", Exact = true }).ClickAsync();
            await settings.GetByLabel("Next episode", new() { Exact = true }).SelectOptionAsync("instant");
            await settings.GetByRole(AriaRole.Link, new() { Name = "Info", Exact = true }).ClickAsync();
            await Assertions.Expect(settings.Locator("#settings-info")).ToBeVisibleAsync();
            var infoDialog = (await settings.BoundingBoxAsync())!;
            Assert.AreEqual(initialDialog.Width, infoDialog.Width, 1);
            Assert.AreEqual(initialDialog.Height, infoDialog.Height, 1);
            await settings.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Forward 30 seconds", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=time]")).ToHaveTextAsync("13:04");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true })).ToBeVisibleAsync();
            Assert.AreEqual(0, writes.Count, string.Join(Environment.NewLine, writes));
        }
    }
}
