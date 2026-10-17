using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class MediaStabilityTests
    {
        [TestMethod]
        [DataRow(1440, 900, false)]
        [DataRow(390, 844, true)]
        [Timeout(180_000)]
        public async Task Discover_StoredRowsAndDelayedRealArtwork_KeepLayoutAcrossRestart(int width, int height, bool dark)
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads, artwork: true);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:AniList"] = fakes.AniList.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString()
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light, ReducedMotion = ReducedMotion.Reduce });
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/?type=movies");
            var fallback = page.Locator("#discover-featured .media-hero[aria-hidden=true]");
            await Assertions.Expect(fallback).ToBeVisibleAsync();
            var fallbackHeight = (await fallback.BoundingBoxAsync())!.Height;
            await Steps.SetTmdbKeyAsync(page);

            // Delay actual JPEG files independently of the initial SSR data.
            var images = Path.Combine(JularrInstance.Repository, "src", "Jularr.Frontend", "Pages", "Development", "Design", "Images");
            var releaseImages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("https://image.tmdb.org/**", async route =>
            {
                pendingImage.TrySetResult();
                await releaseImages.Task;
                await route.FulfillAsync(new() { ContentType = "image/jpeg", Path = Path.Combine(images,
                    route.Request.Url.Contains("backdrop", StringComparison.Ordinal) ? "your-name-backdrop.jpg" : "your-name-poster.jpg") });
            });
            var recommendationRequests = 0;
            page.Request += (_, request) => { if (request.Url.Contains("handler=Recommendations", StringComparison.Ordinal)) ++recommendationRequests; };
            await page.AddInitScriptAsync("""
                window.mediaLayoutShifts = [];
                new PerformanceObserver(list => {
                    for (const entry of list.getEntries()) {
                        if (!entry.hadRecentInput) window.mediaLayoutShifts.push(entry.value);
                    }
                }).observe({type: 'layout-shift', buffered: true});
                """);

            try
            {
                var metadataCalls = 0;
                for (var cachePass = 0; cachePass < 2; cachePass++)
                {
                    releaseImages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (cachePass > 0)
                    {
                        // Data and sign-in survive the host restart; the provider now fails if contacted.
                        fakes.FailMetadata = true;
                        await jularr.RestartAsync();
                    }
                    await page.GotoAsync("/?type=movies", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await pendingImage.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var featured = page.Locator("#discover-featured");
                    var recommendations = page.Locator("#discover-recommendations");
                    await Assertions.Expect(recommendations.Locator(".media-card").First).ToBeVisibleAsync();
                    await Assertions.Expect(featured.Locator("h2")).ToHaveTextAsync(FakeServices.MovieTitle);
                    var before = (await featured.BoundingBoxAsync())!;
                    Assert.AreEqual(fallbackHeight, before.Height, 0.1, "Fallback and populated Hero must reserve the same height.");
                    var rowsBefore = (await recommendations.BoundingBoxAsync())!;
                    await Task.Delay(650); // Do not hide a late shift in the recent-input exemption.
                    releaseImages.TrySetResult();
                    Assert.IsTrue(await featured.Locator("img").EvaluateAsync<bool>("async image => { await image.decode(); return image.naturalWidth > 0; }"));
                    Assert.IsTrue(await recommendations.Locator("img").First.EvaluateAsync<bool>("async image => { await image.decode(); return image.naturalWidth > 0; }"));
                    var after = (await featured.BoundingBoxAsync())!;
                    var rowsAfter = (await recommendations.BoundingBoxAsync())!;
                    Assert.AreEqual(before.Height, after.Height, 0.1, "Late artwork changed the Hero slot height.");
                    Assert.AreEqual(rowsBefore.Y, rowsAfter.Y, 0.1, "Late artwork moved the existing rows.");
                    Assert.AreEqual(0d, await page.EvaluateAsync<double>("() => window.mediaLayoutShifts.reduce((sum, value) => sum + value, 0)"), 0.00001);
                    Assert.AreEqual(0, recommendationRequests, "Initial rows must arrive together with the page.");
                    Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                    if (cachePass == 0) metadataCalls = fakes.MetadataRequests.Count;
                    else Assert.AreEqual(metadataCalls, fakes.MetadataRequests.Count, "A warm page after restart must not fetch the provider again.");
                    await Evidence.SaveAsync(page, "hero-stable-" + width + "-" + cachePass);
                    CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join("\n", browser.Problems));
                }
            }
            catch
            {
                await Evidence.SaveAsync(page, "hero-stability-failed-" + width);
                throw;
            }
            finally
            {
                releaseImages.TrySetResult();
            }
        }
    }
}
