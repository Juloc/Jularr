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
                await Steps.SearchEpisodeAsync(page, FakeServices.SeriesTitle, "1");
                StringAssert.Contains(await page.Locator("tr", new() { HasText = episode.Title }).InnerTextAsync(), "Eligible");
                await Steps.GrabAsync(page, episode);
                CollectionAssert.AreEquivalent(new[] { fakes.NzbAddress(film).ToString(), fakes.NzbAddress(episode).ToString() }, fakes.Grabbed.ToArray());

                // The download monitor imports both into their library paths.
                var filmDetails = await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                var seriesDetails = await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Movies"), "*.webm", SearchOption.AllDirectories).Count(), "The film was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Series"), "*.webm", SearchOption.AllDirectories).Count(), "The episode was not imported once.");

                // The second episode is switched off on the title's page: nothing monitored is missing, the request completes and the Missing list drops the series.
                await Steps.OpenWantedAsync(page, FakeServices.SeriesTitle);
                StringAssert.Contains(await EpisodeRow(page, "1").InnerTextAsync(), "On disk");
                await Steps.SubmitAsync(page, EpisodeRow(page, "2").Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Switch, new() { Name = "Stop monitoring" }) }));
                StringAssert.Contains(await EpisodeRow(page, "2").InnerTextAsync(), "Not monitored");
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

                await AssertPlayerDesignAsync(page);
                await page.GotoAsync(seriesDetails);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                var selectedEpisode = page.Locator(".episode-card[aria-current=true]");
                await Assertions.Expect(selectedEpisode).ToBeVisibleAsync();
                Assert.IsTrue(await selectedEpisode.EvaluateAsync<bool>("card => { const probe = document.createElement('span'); probe.style.background = 'var(--accent)'; card.append(probe); const matches = getComputedStyle(card).backgroundColor === getComputedStyle(probe).backgroundColor; probe.remove(); return matches; }"));
                await page.Locator("[data-action=settings]").ClickAsync();
                await Evidence.SaveAsync(page, "player-series-settings-light");
                await page.EvaluateAsync("document.documentElement.dataset.mode = 'dark'");
                await Assertions.Expect(page.Locator("[data-setting-pane=audio]")).ToHaveCSSAsync("background-color",
                    await page.Locator(".player__sidebar").EvaluateAsync<string>("sidebar => getComputedStyle(sidebar).backgroundColor"));
                await Evidence.SaveAsync(page, "player-series-settings-dark");
                await page.EvaluateAsync("document.documentElement.dataset.mode = 'light'");
                await page.Keyboard.PressAsync("Escape");

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

        private static async Task AssertPlayerDesignAsync(IPage page)
        {
            var video = page.Locator("video");
            await video.EvaluateAsync("video => { window.__originalPlayerVideo = video; }");
            await page.Locator("[data-action=settings]").ClickAsync();
            var settings = page.Locator("#player-settings");
            await Assertions.Expect(settings).ToBeVisibleAsync();
            var size = (await settings.BoundingBoxAsync())!;
            foreach (var name in new[] { "Basic", "Audio", "Subtitles", "Playback", "Info" })
            {
                await settings.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true }).ClickAsync();
                var bounds = (await settings.BoundingBoxAsync())!;
                Assert.AreEqual(size.Width, bounds.Width, 1);
                Assert.AreEqual(size.Height, bounds.Height, 1);
                Assert.IsTrue(await video.EvaluateAsync<bool>("video => video.paused"));
            }
            await settings.GetByRole(AriaRole.Tab, new() { Name = "Playback", Exact = true }).ClickAsync();
            await settings.Locator("[data-speed='1.25']").ClickAsync();
            Assert.AreEqual(1.25, await video.EvaluateAsync<double>("video => video.playbackRate"), .01);
            await settings.Locator("[data-speed='1']").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(settings).ToBeHiddenAsync();

            // The native range moves while the current playback continues; only release seeks.
            await video.EvaluateAsync("video => { video.currentTime = 1; return video.play(); }");
            var timeline = page.Locator("[data-role=timeline]");
            var track = (await timeline.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(track.X + track.Width * .1f, track.Y + track.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(track.X + track.Width * .75f, track.Y + track.Height / 2, new() { Steps = 8 });
            await Task.Delay(3200);
            Assert.IsTrue(await video.EvaluateAsync<bool>("video => !video.paused && video.currentTime > 2 && video.currentTime < 6"), "Holding the range must continue playback without seeking or pausing.");
            Assert.IsFalse(await page.Locator(".player").EvaluateAsync<bool>("player => player.classList.contains('player--idle')"), "Holding the seek control must keep the chrome visible.");
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime > 6.5");
            await video.EvaluateAsync("video => video.pause()");
            Assert.IsTrue(await timeline.EvaluateAsync<bool>("input => input.style.getPropertyValue('--played').length > 0"));

            await page.Locator("[data-action=mute]").ClickAsync();
            Assert.IsTrue(await video.EvaluateAsync<bool>("video => video.muted"));
            await page.Locator("[data-action=mute]").ClickAsync();
            await page.Locator("[data-action=theater]").ClickAsync();
            await Assertions.Expect(page.Locator(".player")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("player--theater"));
            if (await page.Locator("#player-sidebar").CountAsync() > 0)
            {
                await page.Locator("[data-action=episodes]").ClickAsync();
                await Assertions.Expect(page.Locator("#player-sidebar")).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
            }
            await page.Locator("[data-action=theater]").ClickAsync();

            await page.Locator("[data-action=fullscreen]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('.player')");
            await page.Locator("[data-action=settings]").ClickAsync();
            await Assertions.Expect(settings).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await page.Locator("[data-action=fullscreen]").ClickAsync();
            await page.WaitForFunctionAsync("() => !document.fullscreenElement");

            await page.SetViewportSizeAsync(390, 844);
            await page.Locator("[data-action=settings]").ClickAsync();
            var mobile = (await settings.BoundingBoxAsync())!;
            Assert.IsTrue(mobile.X >= 0 && mobile.Y >= 0 && mobile.X + mobile.Width <= 391 && mobile.Y + mobile.Height <= 845);
            await Evidence.SaveAsync(page, "player-settings-mobile");
            await settings.GetByRole(AriaRole.Tab, new() { Name = "Info", Exact = true }).ClickAsync();
            await page.Keyboard.PressAsync("Home");
            await Assertions.Expect(settings.GetByRole(AriaRole.Tab, new() { Name = "Basic", Exact = true })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Escape");
            await page.SetViewportSizeAsync(1280, 720);

            await page.Locator(".player__sidebar a[data-action=close]").ClickAsync();
            await Assertions.Expect(page.Locator(".app-layout")).ToBeVisibleAsync();
            Assert.IsTrue(await video.EvaluateAsync<bool>("video => video === window.__originalPlayerVideo"));
            Assert.AreEqual(1, await page.Locator("video").CountAsync());
            // Saving a form of the page beside the mini player keeps the same playback.
            await Steps.SubmitAsync(page, page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Add to watchlist", Exact = true }) }));
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Remove from watchlist", Exact = true })).ToBeVisibleAsync();
            Assert.IsTrue(await video.EvaluateAsync<bool>("video => video === window.__originalPlayerVideo"));
            await page.Locator("[data-action=expand]").ClickAsync();
            Assert.IsTrue(await video.EvaluateAsync<bool>("video => video === window.__originalPlayerVideo"));
            // Keep the resume contract of the surrounding end-to-end path unchanged.
            await page.RunAndWaitForResponseAsync(() => video.EvaluateAsync("video => { video.currentTime = 5; return video.play().then(() => video.pause()); }"),
                response => response.Request.Method == "PUT" && response.Ok);
            await Evidence.SaveAsync(page, "player-design-desktop");
        }

        // The row of an episode in the unit table of a title's page.
        private static ILocator EpisodeRow(IPage page, string number) =>
            page.Locator("tr.work-row").Filter(new() { Has = page.Locator("td[data-col=number]").GetByText(number, new() { Exact = true }) });
    }
}
