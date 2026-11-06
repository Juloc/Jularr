using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Homebrew from a games library folder starts in the browser: the DS with both screens and touch, also on a phone, and a file without its game.
    [TestClass]
    public sealed class GamePathTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task HomebrewGames_FromTheLibraryFolder_PlayInTheBrowser()
        {
            using var folders = new Folders("Games");
            var homebrew = Directory.CreateDirectory(Path.Combine(folders.Library, "Games", "Homebrew")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(homebrew, "Jularr Homebrew.nds"), HomebrewRoms.NintendoDs());
            await File.WriteAllBytesAsync(Path.Combine(homebrew, "Twin Game.gba"), HomebrewRoms.GameBoyAdvance());
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Libretro"] = fakes.Libretro.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // Two games of one title leave that file without its game.
                await jularr.RunSqlAsync("""
                    WITH work AS (INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (7, 'Twin Game'), (7, 'Twin Game') RETURNING "Id")
                    INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") SELECT "Id", 'Twin Game', TRUE FROM work
                    """);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Games", "Game"));
                await page.GotoAsync("/Admin/Storage");
                await Steps.FollowAsync(page, page.Locator(".feature-card", new() { HasText = "Library" }).GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Steps.SubmitAsync(page, page.Locator("tr", new() { HasText = "/Games" }).Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Scan" }) }));

                // The scan makes the DS file a game of its own; the library leads to every file.
                var ds = page.Locator("tr", new() { HasText = "Jularr Homebrew.nds" });
                await WaitForAsync(async () =>
                {
                    await page.GotoAsync("/Library?type=games");
                    await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "All game files", Exact = true }));
                    return await ds.GetByRole(AriaRole.Link, new() { Name = "Jularr Homebrew", Exact = true }).CountAsync() > 0;
                }, "The scan did not make the DS file a game.");
                await Assertions.Expect(ds).ToContainTextAsync("NDS");
                var twin = page.Locator("tr", new() { HasText = "Twin Game.gba" });
                await Assertions.Expect(twin).ToContainTextAsync("GBA");

                // The DS shows both screens; a press on the lower one is its touch.
                await ds.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Game/*");
                var game = page.Url;
                var screen = await StartAsync(page, tap: false);
                var top = DsScreen(screen, upper: true);
                var bottom = DsScreen(screen, upper: false);
                Assert.AreEqual(Colour.Purple, await ColourAsync(page, top));
                Assert.AreEqual(Colour.Green, await ColourAsync(page, bottom));
                await page.Mouse.MoveAsync(bottom.Item1, bottom.Item2);
                await page.Mouse.DownAsync();
                await WaitForAsync(async () => await ColourAsync(page, bottom) == Colour.Red, "A press on the lower screen was no touch.");
                await page.Mouse.UpAsync();
                await WaitForAsync(async () => await ColourAsync(page, bottom) == Colour.Green, "The touch did not end.");

                // A file without its game plays with the browser's saves.
                await page.GotoAsync("/Games/Files");
                await twin.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Game/Files/*");
                await Assertions.Expect(page.Locator(".game__note")).ToContainTextAsync("its saves stay in this browser");
                var gba = await StartAsync(page, tap: false);
                Assert.AreEqual(Colour.Purple, await ColourAsync(page, (gba.X + gba.Width / 2, gba.Y + gba.Height / 2)));

                // A phone gets the on-screen controls, and a finger on the lower screen touches it.
                await using var phone = await page.Context.Browser!.NewContextAsync(new BrowserNewContextOptions
                {
                    BaseURL = jularr.Address.ToString(),
                    ViewportSize = new ViewportSize { Width = 375, Height = 812 },
                    IsMobile = true,
                    HasTouch = true,
                    StorageState = await page.Context.StorageStateAsync()
                });
                await Steps.KeepLocalAsync(phone);
                var mobile = await phone.NewPageAsync();
                var problems = Steps.Watch(mobile);
                await mobile.GotoAsync(game);
                var small = await StartAsync(mobile, tap: true);
                await Assertions.Expect(mobile.Locator(".ejs_virtualGamepad_button").First).ToBeVisibleAsync();
                var lower = DsScreen(small, upper: false);
                Assert.AreEqual(Colour.Green, await ColourAsync(mobile, lower));
                var touch = await phone.NewCDPSessionAsync(mobile);
                var finger = new Dictionary<string, object> { ["x"] = lower.Item1, ["y"] = lower.Item2, ["id"] = 1 };
                await touch.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object> { ["type"] = "touchStart", ["touchPoints"] = new[] { finger } });
                await WaitForAsync(async () => await ColourAsync(mobile, lower) == Colour.Red, "A finger on the lower screen was no touch.");
                await touch.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object> { ["type"] = "touchEnd", ["touchPoints"] = Array.Empty<object>() });
                await WaitForAsync(async () => await ColourAsync(mobile, lower) == Colour.Green, "The touch did not end.");

                Assert.AreEqual("", string.Join("\n", session.Problems.Concat(problems).Where(problem => !Expected(problem))));
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "game-path");
                throw;
            }
        }

        // EmulatorJS asks its CDN for a newer version only on a loopback host, which the page's policy refuses; headless Chromium refuses the screen wake lock;
        // a profile's first start finds no save (404).
        private static bool Expected(string problem) => problem.Contains("cdn.emulatorjs.org", StringComparison.Ordinal)
            || problem.Contains("EmulatorJS.checkForUpdates", StringComparison.Ordinal)
            || problem.Contains("Wake Lock permission request denied", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(problem, @"/Game/\d+: Failed to load resource: the server responded with a status of 404");

        private enum Colour
        {
            Other,
            Purple,
            Green,
            Red
        }

        // Starts the emulator as a person does and gives the place of its picture.
        private static async Task<LocatorBoundingBoxResult> StartAsync(IPage page, bool tap)
        {
            var start = page.Locator(".ejs_start_button");
            if (tap) await start.TapAsync(new LocatorTapOptions { Timeout = 60_000 });
            else await start.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await page.WaitForFunctionAsync("() => window.EJS_emulator?.started === true", null, new PageWaitForFunctionOptions { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(1500);
            return (await page.Locator("canvas.ejs_canvas").BoundingBoxAsync())!;
        }

        // The middle of the DS's upper or lower screen: both stand 2:3 at the top of the picture, as wide or as high as it allows.
        private static (float X, float Y) DsScreen(LocatorBoundingBoxResult picture, bool upper)
        {
            var height = Math.Min(picture.Height, picture.Width * 1.5f);
            return (picture.X + picture.Width / 2, picture.Y + height * (upper ? 0.25f : 0.75f));
        }

        // The colour of one point of the page, read from a picture of it.
        private static async Task<Colour> ColourAsync(IPage page, (float X, float Y) point)
        {
            var picture = await page.ScreenshotAsync(new PageScreenshotOptions { Clip = new Clip { X = point.X, Y = point.Y, Width = 1, Height = 1 } });
            var rgb = await page.EvaluateAsync<int[]>("""
                async data => {
                    const image = await createImageBitmap(await (await fetch("data:image/png;base64," + data)).blob());
                    const canvas = new OffscreenCanvas(1, 1);
                    const context = canvas.getContext("2d");
                    context.drawImage(image, 0, 0);
                    return [...context.getImageData(0, 0, 1, 1).data.slice(0, 3)];
                }
                """, Convert.ToBase64String(picture));
            return rgb switch
            {
                [> 100 and < 170, < 100, > 200] => Colour.Purple,
                [< 60, > 200, < 60] => Colour.Green,
                [> 200, < 60, < 60] => Colour.Red,
                _ => Colour.Other
            };
        }

        private static async Task WaitForAsync(Func<Task<bool>> done, string failure)
        {
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (!await done())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new AssertFailedException(failure);
                }

                await Task.Delay(500);
            }
        }
    }
}
