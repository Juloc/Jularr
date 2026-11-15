using System.Diagnostics;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A search shows at once; each provider's answer joins it in its own place, and a provider that does not answer never holds the page or the others.
    [TestClass]
    public sealed class DiscoverSearchTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Search_AnswersAtOnce_FillsEachTypeWhenItsProviderAnswers_AndGivesUpOnASlowOneQuietly()
        {
            using var folders = new Folders();
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString()
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);

                // The film provider never answers: its place shows grey cards, the series place fills at once, then the film place gives up with a quiet note.
                var timer = Stopwatch.StartNew();
                var response = await page.GotoAsync("/?q=Slowpoke+Test+Series");
                Assert.AreEqual(200, response?.Status);
                var films = page.Locator("[data-search-group=movies]");
                await Assertions.Expect(films).ToHaveCountAsync(1);
                Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(4), "the page waited for a provider: " + timer.Elapsed);
                await Assertions.Expect(page.Locator(".media-card__link", new() { HasText = FakeServices.SeriesTitle })).ToBeVisibleAsync(new() { Timeout = 4_000 });
                await Assertions.Expect(films).ToHaveCountAsync(1);
                await Evidence.SaveAsync(page, "discover-search-waiting");

                var note = page.Locator(".discover-search-note");
                await Assertions.Expect(note).ToHaveCountAsync(1, new() { Timeout = 12_000 });
                await Assertions.Expect(page.Locator("[data-search-group], .media-skeleton")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator(".media-card__link", new() { HasText = FakeServices.SeriesTitle })).ToHaveCountAsync(1);
                await Evidence.SaveAsync(page, "discover-search-gave-up");

                // Nothing matches: every place leaves, nothing stays grey, and the page says so.
                await page.GotoAsync("/?q=Zzqxnothing");
                await Assertions.Expect(page.Locator(".discover-search-none")).ToBeVisibleAsync(new() { Timeout = 8_000 });
                await Assertions.Expect(page.Locator("[data-search-group], .media-skeleton, .discover-search-note, .media-shelf")).ToHaveCountAsync(0);
            }
            catch
            {
                await Evidence.SaveAsync(page, "discover-search-failed");
                throw;
            }
        }
    }
}
