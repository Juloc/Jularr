using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class CoverDeliveryTests
    {
        [TestMethod]
        [Timeout(240_000)]
        public async Task Cards_SelectTheirRenderedSize_AndReuseFreshExternalCovers()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            fakes.ImagePayload = await File.ReadAllBytesAsync(Path.Combine(root, "src/Jularr.Frontend/Assets/Shared/icon-512.png"));
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString()
            });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            // Request routing disables Chromium's HTTP cache. This test uses only loopback endpoints without routing.
            await using var context = await browser.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), DeviceScaleFactor = 1 });
            var page = await context.NewPageAsync();
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (8001, 1, 'Cover delivery');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual")
                VALUES (8001, 'Poster', 'https://image.tmdb.org/t/p/original/card.png', TRUE);
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT "Id", 8001 FROM "Profiles";
                """);

            await page.GotoAsync("/Favorites");
            var image = page.Locator(".media-card img.media-image").First;
            await Assertions.Expect(image).ToBeVisibleAsync();
            await Assertions.Expect(image).ToHaveAttributeAsync("sizes", "auto, 176px");
            await Assertions.Expect(image).ToHaveJSPropertyAsync("complete", true);
            StringAssert.Contains(await image.EvaluateAsync<string>("i => i.currentSrc"), "w=250");
            Assert.IsTrue(await image.EvaluateAsync<double>("i => i.getBoundingClientRect().width") <= 176);
            Assert.IsTrue(await image.EvaluateAsync<int>("i => i.naturalWidth") > 0);

            await page.GotoAsync("/Settings");
            await page.GotoAsync("/Favorites");
            image = page.Locator(".media-card img.media-image").First;
            await Assertions.Expect(image).ToHaveJSPropertyAsync("complete", true);
            var cached = await page.EvaluateAsync<bool>("""
                () => performance.getEntriesByType('resource').some(r =>
                    r.name.includes('/covers/external') && r.name.includes('w=250') && r.transferSize === 0 && r.decodedBodySize > 0)
                """);
            Assert.IsTrue(cached, "A normal second visit must reuse the fresh browser-cached cover.");

            await using var retina = await browser.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), DeviceScaleFactor = 2, StorageState = await context.StorageStateAsync() });
            var sharp = await retina.NewPageAsync();
            await sharp.GotoAsync("/Favorites");
            var sharpImage = sharp.Locator(".media-card img.media-image").First;
            await Assertions.Expect(sharpImage).ToHaveJSPropertyAsync("complete", true);
            StringAssert.Contains(await sharpImage.EvaluateAsync<string>("i => i.currentSrc"), "w=500");
            Assert.AreEqual(1, fakes.ImageRequests, "Both widths reuse one stored provider original.");
        }
    }
}
