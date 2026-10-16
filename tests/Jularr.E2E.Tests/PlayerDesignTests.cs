using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class PlayerDesignTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task DesktopPreview_ScopesAndExpandedSettings_KeepOneLocalPlayer()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            var writes = new List<string>();
            page.Request += (_, request) => { if (request.Method is not ("GET" or "HEAD")) writes.Add(request.Url); };
            await page.SetViewportSizeAsync(1600, 1000);
            await page.GotoAsync("/Design?pane=player#player-preview");
            var video = (await page.Locator("video").ElementHandleAsync())!;
            Assert.IsNull(await video.GetAttributeAsync("src"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Play", Exact = true }).ClickAsync();
            await page.Mouse.MoveAsync(0, 0);
            await Assertions.Expect(page.Locator(".player")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(".*player--idle.*"), new() { Timeout = 5000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            var panel = page.Locator("#player-settings");
            var basic = panel.Locator("#settings-basic");
            var scope = panel.Locator("[data-setting-scope]");
            await Assertions.Expect(basic).ToBeVisibleAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Link)).ToHaveCountAsync(5);
            await Assertions.Expect(panel.Locator("h2, [data-action=save-defaults], [data-copy]")).ToHaveCountAsync(0);
            var initialDialog = (await panel.BoundingBoxAsync())!;

            // Inherit, override, reset, never copy on scope change.
            await scope.SelectOptionAsync("profile");
            var speed = basic.GetByLabel("Speed (×)", new() { Exact = true });
            await speed.FillAsync("1.5");
            Assert.IsTrue((await speed.BoundingBoxAsync())!.Width >= 70, "The speed input must remain readable beside Reset.");
            Assert.IsTrue((await basic.Locator(".player-settings__speed").BoundingBoxAsync())!.Height < 100, "Speed controls must not wrap into a tall column.");
            await scope.SelectOptionAsync("current");
            await Assertions.Expect(speed).ToHaveValueAsync("1.5");
            await speed.FillAsync("2");
            await basic.Locator("[data-reset-setting=speed]").First.ClickAsync();
            await Assertions.Expect(speed).ToHaveValueAsync("1.5");
            await scope.SelectOptionAsync("series");
            await speed.FillAsync("3");
            await scope.SelectOptionAsync("profile");
            await Assertions.Expect(speed).ToHaveValueAsync("1.5");
            await scope.SelectOptionAsync("series");

            // Stable dialog, shared compact controls.
            await panel.GetByRole(AriaRole.Link, new() { Name = "Audio", Exact = true }).ClickAsync();
            var audio = panel.GetByRole(AriaRole.Combobox, new() { Name = "Audio", Exact = true });
            await Assertions.Expect(audio).ToHaveCSSAsync("appearance", "base-select");
            await audio.SelectOptionAsync("1");
            await panel.GetByRole(AriaRole.Link, new() { Name = "Info", Exact = true }).ClickAsync();
            var infoDialog = (await panel.BoundingBoxAsync())!;
            Assert.AreEqual(initialDialog.Width, infoDialog.Width, 1);
            Assert.AreEqual(initialDialog.Height, infoDialog.Height, 1);
            await panel.GetByRole(AriaRole.Button, new() { Name = "All Player settings", Exact = true }).ClickAsync();

            // Move the original media element into the expanded preview.
            var full = page.Locator("[data-settings-page]");
            var controls = full.Locator(".player-preferences__controls");
            await Assertions.Expect(full).ToBeVisibleAsync();
            await Assertions.Expect(full.Locator("video")).ToHaveCountAsync(1);
            Assert.IsTrue(await video.EvaluateAsync<bool>("element => element === document.querySelector('video')"));
            await Assertions.Expect(full.GetByRole(AriaRole.Combobox, new() { Name = "Audio", Exact = true })).ToHaveValueAsync("1");
            await Assertions.Expect(full.Locator(":scope > header [data-setting-scope]")).ToHaveValueAsync("series");
            var fullScope = full.Locator(":scope > header [data-setting-scope]");
            var advanced = controls.Locator(".player-settings__details").First;
            Assert.IsTrue((await advanced.BoundingBoxAsync())!.Height < 50, "Closed groups must stay compact.");
            await fullScope.SelectOptionAsync("admin");
            await Assertions.Expect(controls.GetByRole(AriaRole.Combobox, new() { Name = "Audio", Exact = true })).ToBeDisabledAsync();
            await controls.GetByLabel("Audio offset (ms)", new() { Exact = true }).FillAsync("150");
            await fullScope.SelectOptionAsync("series");
            await Assertions.Expect(controls.GetByLabel("Audio offset (ms)", new() { Exact = true })).ToHaveValueAsync("0");
            await full.GetByText("Take values from…", new() { Exact = true }).ClickAsync();
            await full.Locator("[data-copy-source]").SelectOptionAsync("profile");
            await full.GetByRole(AriaRole.Button, new() { Name = "Current tab", Exact = true }).ClickAsync();
            await Assertions.Expect(controls.GetByRole(AriaRole.Combobox, new() { Name = "Audio", Exact = true })).ToHaveValueAsync("1");
            await full.GetByRole(AriaRole.Link, new() { Name = "Subtitles", Exact = true }).ClickAsync();
            await controls.GetByLabel("Second subtitle track", new() { Exact = true }).SelectOptionAsync("german");
            await Assertions.Expect(page.Locator("[data-caption=secondary]")).ToBeVisibleAsync();
            await full.GetByRole(AriaRole.Link, new() { Name = "Picture", Exact = true }).ClickAsync();
            await full.GetByRole(AriaRole.Button, new() { Name = "Original size", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("video")).ToHaveCSSAsync("object-fit", "scale-down");

            await full.GetByRole(AriaRole.Button, new() { Name = "Back to Player", Exact = true }).ClickAsync();
            await Assertions.Expect(full).ToBeHiddenAsync();
            Assert.IsTrue(await video.EvaluateAsync<bool>("element => element === document.querySelector('video')"));
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            await Assertions.Expect(scope).ToHaveValueAsync("series");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Forward 30 seconds", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=time]")).ToHaveTextAsync("13:04");
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            await Assertions.Expect(scope).ToHaveValueAsync("current");
            Assert.AreEqual(0, writes.Count, string.Join(Environment.NewLine, writes));
            Assert.AreEqual(0, session.Problems.Count, string.Join(Environment.NewLine, session.Problems));
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task NarrowPreview_SettingsFillScreen_KeepOneContentScrollArea()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync("/Design?pane=player#player-preview");
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            var panel = page.Locator("#player-settings");
            await Assertions.Expect(panel).ToHaveCSSAsync("overflow", "hidden");
            await Assertions.Expect(panel).ToHaveCSSAsync("width", "390px");
            await Assertions.Expect(panel).ToHaveCSSAsync("height", "844px");
            await Assertions.Expect(panel.Locator(".player-settings__body")).ToHaveCSSAsync("overflow", "auto");
            await Assertions.Expect(page.Locator("html")).ToHaveCSSAsync("overflow", "hidden");
            var speed = panel.Locator("#settings-basic").GetByLabel("Speed (×)", new() { Exact = true });
            await speed.FillAsync("1.25");
            Assert.IsTrue((await speed.BoundingBoxAsync())!.Width >= 70);
            await panel.GetByRole(AriaRole.Link, new() { Name = "Audio", Exact = true }).ClickAsync();
            var fits = await panel.Locator(".player-settings__body").EvaluateAsync<bool>("element => element.scrollHeight <= element.clientHeight + 1");
            Assert.IsTrue(fits, "Everyday Audio controls must fit without scrolling.");
            await panel.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await Assertions.Expect(panel).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("html")).Not.ToHaveCSSAsync("overflow", "hidden");
            await Assertions.Expect(page.Locator("video")).ToHaveCountAsync(1);

            // Expanded fields must not widen the document.
            await page.GetByRole(AriaRole.Button, new() { Name = "Player settings", Exact = true }).ClickAsync();
            await panel.GetByRole(AriaRole.Button, new() { Name = "All Player settings", Exact = true }).ClickAsync();
            Assert.IsTrue(await page.Locator("html").EvaluateAsync<bool>("element => element.scrollWidth <= innerWidth"), "Expanded settings must fit a narrow viewport.");
            var back = page.GetByRole(AriaRole.Button, new() { Name = "Back to Player", Exact = true });
            Assert.IsTrue((await back.BoundingBoxAsync())!.Height < 60, "The Back label must not wrap into a tall button.");
        }
    }
}
