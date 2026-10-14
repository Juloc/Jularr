using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A film and a series episode from an empty instance to playing them again after signing in anew, only through the browser and fake services.
    [TestClass]
    public sealed class FilmPathTests
    {
        [ClassInitialize]
        public static void InstallBrowser(TestContext _)
        {
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                JularrInstance.Unavailable("Chromium for Playwright could not be installed.");
            }
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task FilmAndEpisode_FromSetupToRequestDownloadImportPlayAndResume()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Movies", "Series");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, video);
            var otherFilm = new FakeRelease("Other.Movie.2008.1080p.WEB-DL.H264-JLR", "other", 2040, video);
            var episode = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR", "s01e01", 5040, video);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var page = await context.NewPageAsync();
            var problems = Steps.Watch(page);

            try
            {
                // Setup, TMDB, the library with a film and a series path, indexer and download client.
                await Owner.SetUpAndSignInAsync(context, page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5040");

                // Both are requested; their releases appear only afterwards.
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);
                fakes.Offer(film, otherFilm, episode);

                // Wanted shows every candidate with its decision; the chosen one is downloaded.
                await Steps.OpenWantedAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = otherFilm.Title }).InnerTextAsync(), "Different title");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = film.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, film);

                // A single episode is searched by its season and number.
                await Steps.OpenWantedAsync(page, FakeServices.SeriesTitle);
                var search = Steps.FormWith(page, "Start search");
                await search.Locator("[name=ParentUnitNumber]").FillAsync("1");
                await search.Locator("[name=UnitNumber]").FillAsync("1");
                await Steps.SubmitAsync(page, search);
                StringAssert.Contains(await page.Locator("tr", new() { HasText = episode.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, episode);
                CollectionAssert.AreEquivalent(new[] { fakes.NzbAddress(film).ToString(), fakes.NzbAddress(episode).ToString() }, fakes.Grabbed.ToArray());

                // The download monitor imports both into their library paths.
                var filmDetails = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                var seriesDetails = await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count(), "The film was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Series"), "*.webm", SearchOption.AllDirectories).Count(), "The episode was not imported once.");

                // Playing needs a real file; FFmpeg makes one.
                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await page.GotoAsync(filmDetails);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                await page.GotoAsync(seriesDetails);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);

                // Signed out and in again, both continue where they stopped.
                await Steps.SignOutAndInAsync(page);
                await page.GotoAsync(filmDetails);
                var resumed = await Steps.ResumeAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Continue at" }).First);
                Assert.IsTrue(resumed >= 4.5, "The film started at " + resumed + " s instead of continuing.");
                await page.GotoAsync(seriesDetails);
                resumed = await Steps.ResumeAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Continue at" }).First);
                Assert.IsTrue(resumed >= 4.5, "The episode started at " + resumed + " s instead of continuing.");

                CollectionAssert.AreEqual(Array.Empty<string>(), problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "film-path");
                throw;
            }
        }
    }
}
