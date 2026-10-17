using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A film and a series episode from an empty instance to playing them again after signing in anew, only through the browser and fake services.
    [TestClass]
    public sealed class FilmPathTests
    {
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
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                // Setup, TMDB, the library with a film and a series path, indexer and download client.
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5040");

                // Both are requested; each title's releases appear just before its manual search.
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);

                // Wanted shows every candidate with its decision; the chosen one is downloaded.
                fakes.Offer(film, otherFilm);
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                StringAssert.Contains(await page.Locator("tr", new() { HasText = otherFilm.Title }).InnerTextAsync(), "Different title");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = film.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, film);

                // A single episode is searched by its season and number.
                fakes.Offer(episode);
                await Steps.OpenSearchAsync(page, FakeServices.SeriesTitle);
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

                // The second episode is switched off on the Monitoring tab: nothing monitored is missing, the request completes and the Missing list drops the series.
                await Steps.OpenWantedAsync(page, FakeServices.SeriesTitle);
                await page.GetByRole(AriaRole.Link, new() { Name = "Season 1", Exact = true }).ClickAsync();
                StringAssert.Contains(await page.Locator("tr", new() { HasText = "Episode 1" }).InnerTextAsync(), "Present");
                await Steps.SubmitAsync(page, page.Locator("tr", new() { HasText = "Episode 2" }).Locator("form")
                    .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Stop monitoring" }) }));
                var season = page.Locator("tr").Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = "Season 1", Exact = true }) });
                StringAssert.Contains(await season.InnerTextAsync(), "Partly monitored");
                await page.GotoAsync("/Admin/Wanted?PageNumber=1&View=Missing");
                Assert.AreEqual(0, await page.Locator("tr", new() { HasText = FakeServices.SeriesTitle }).CountAsync(), "The series is still listed as missing.");
                await page.GotoAsync("/Admin/Requests");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = FakeServices.SeriesTitle }).InnerTextAsync(), "Completed");

                // Playing needs a real file; FFmpeg makes one.
                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await page.GotoAsync(filmDetails);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);

                // The production settings dialog drives this video; it is not a workbench preview.
                await page.Locator("[data-role=timeline]").FillAsync("6");
                await page.WaitForFunctionAsync("() => Math.abs(document.querySelector('video').currentTime - 6) < .25");
                await page.WaitForFunctionAsync("() => parseFloat(document.querySelector('[data-role=played]').style.width) > 0");
                var beforeWidth = await page.Locator("video").EvaluateAsync<double>("video => video.getBoundingClientRect().width");
                await page.Locator("[data-action=speed]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=settings]")).ToBeVisibleAsync();
                await page.Locator("[data-speed='1.25']").ClickAsync();
                Assert.AreEqual(1.25, await page.Locator("video").EvaluateAsync<double>("video => video.playbackRate"));
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-settings-tab=speed]").ClickAsync();
                await page.Locator("[data-fit=cover]").ClickAsync();
                Assert.AreEqual("cover", await page.Locator("video").EvaluateAsync<string>("video => video.style.objectFit"));
                Assert.AreEqual(beforeWidth, await page.Locator("video").EvaluateAsync<double>("video => video.getBoundingClientRect().width"),
                    "The settings panel must overlay the video without shifting its layout.");
                await page.Locator("[data-action=settings-close]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-panel=settings]")).ToBeHiddenAsync();

                await page.GotoAsync(seriesDetails);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);

                // Signed out and in again, both continue where they stopped.
                await Steps.SignOutAndInAsync(page);
                await page.GotoAsync(filmDetails);
                var resumed = await Steps.ResumeAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Continue", Exact = true }));
                Assert.IsTrue(resumed >= 4.5, "The film started at " + resumed + " s instead of continuing.");
                await page.GotoAsync(seriesDetails);
                resumed = await Steps.ResumeAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Continue at" }).First);
                Assert.IsTrue(resumed >= 4.5, "The episode started at " + resumed + " s instead of continuing.");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "film-path");
                throw;
            }
        }
    }
}
