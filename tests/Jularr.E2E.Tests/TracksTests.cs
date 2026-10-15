using System.Diagnostics;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // An episode with two audio tracks and a subtitle file plays in either language with subtitles; a broken episode stops with a visible failure.
    [TestClass]
    public sealed class TracksTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task AudioAndSubtitleChoice_AndABrokenFile_AreHandledInThePlayer()
        {
            // Two audio tracks and a conversion need FFmpeg.
            if (Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is not { } ffmpeg)
            {
                JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; tracks and conversions were not checked.");
                return;
            }

            using var folders = new Folders("Series");
            var episode = Path.Combine(folders.Fixtures, "episode");
            await WriteEpisodeAsync(ffmpeg, episode, "Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR");
            var broken = Path.Combine(folders.Fixtures, "broken.mkv");
            await File.WriteAllBytesAsync(broken, Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index * 31 % 251)).ToArray());
            var first = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR", "s01e01", 5040, episode);
            var second = new FakeRelease("Jularr.Test.Series.S01E02.1080p.WEB-DL.H264-JLR", "s01e02", 5040, broken);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg,
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg)!
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "5000,5040");
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);
                foreach (var (release, number) in new[] { (first, "1"), (second, "2") })
                {
                    fakes.Offer(release);
                    await Steps.OpenSearchAsync(page, FakeServices.SeriesTitle);
                    var search = Steps.FormWith(page, "Start search");
                    await search.Locator("[name=ParentUnitNumber]").FillAsync("1");
                    await search.Locator("[name=UnitNumber]").FillAsync(number);
                    await Steps.SubmitAsync(page, search);
                    await Steps.GrabAsync(page, release);
                }

                var details = await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                var pilot = page.Locator("tr", new() { HasText = "Pilot" }).GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true });
                var brokenEpisode = page.Locator("tr", new() { HasText = "Second" }).GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true });
                await WaitForPlayableAsync(page, details, brokenEpisode);

                // Both audio tracks are offered in their languages; choosing the second asks for a new plan with that stream, and the new source plays.
                await pilot.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = Steps.MediaTimeout });
                await page.Locator("[data-action=audio]").ClickAsync();
                var audio = page.Locator("[data-panel=audio] [data-audio]");
                Assert.AreEqual(2, await audio.CountAsync());
                Assert.AreEqual("true", await audio.Nth(0).GetAttributeAsync("aria-checked"));
                var languages = new[] { await audio.Nth(0).GetAttributeAsync("data-language"), await audio.Nth(1).GetAttributeAsync("data-language") };
                Assert.IsTrue(languages.All(language => !string.IsNullOrEmpty(language)) && languages[0] != languages[1], string.Join(", ", languages));
                var stream = await audio.Nth(1).GetAttributeAsync("data-audio");
                var source = await page.Locator("video").EvaluateAsync<string>("video => video.currentSrc");
                var plan = await page.RunAndWaitForResponseAsync(() => audio.Nth(1).ClickAsync(),
                    response => response.Request.Method == "POST" && response.Request.PostData?.Contains("\"audioStreamIndex\":" + stream, StringComparison.Ordinal) == true,
                    new() { Timeout = Steps.MediaTimeout });
                Assert.IsTrue(plan.Ok, "The plan for the second track answered " + plan.Status);
                await page.WaitForFunctionAsync("source => document.querySelector('video').currentSrc !== source", source, new() { Timeout = Steps.MediaTimeout });
                var switchedAt = await page.Locator("video").EvaluateAsync<double>("video => video.currentTime");
                await page.WaitForFunctionAsync("at => document.querySelector('video').currentTime > at + 1", switchedAt, new() { Timeout = Steps.MediaTimeout });

                // The subtitle file beside it is the only subtitle, in English, with its own text.
                await page.Locator("[data-action=subtitles]").ClickAsync();
                var subtitles = page.Locator("[data-panel=subtitles] [data-subtitle]:not([data-subtitle=''])");
                Assert.AreEqual(1, await subtitles.CountAsync());
                StringAssert.StartsWith(await subtitles.GetAttributeAsync("data-language") ?? "", "en");
                await subtitles.ClickAsync();
                await page.WaitForFunctionAsync("() => [...document.querySelector('video').textTracks].some(track => track.mode === 'showing' && track.cues && track.cues.length > 0)",
                    null, new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual("Hello from the subtitle file.", await page.Locator("video").EvaluateAsync<string>(
                    "video => [...video.textTracks].find(track => track.mode === 'showing').cues[0].text"));
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());

                // The broken file stops with the player's general failure, not silently.
                await page.GotoAsync(details);
                await brokenEpisode.ClickAsync();
                var status = page.Locator("[data-role=status]");
                await Assertions.Expect(status).ToBeVisibleAsync(new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual(await page.Locator("[data-status=failed]").TextContentAsync(), await status.TextContentAsync());
                Assert.IsTrue(await page.Locator("video").EvaluateAsync<bool>("video => video.error !== null"), "The video reported no error.");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "tracks");
                throw;
            }
        }

        // The download monitor imports one episode after the other.
        private static async Task WaitForPlayableAsync(IPage page, string details, ILocator play)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                await page.GotoAsync(details);
                if (await play.CountAsync() > 0)
                {
                    return;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new AssertFailedException("Not both episodes became playable within two minutes.");
                }

                await Task.Delay(3000);
            }
        }

        // A WebM with an English and a German tone track and an English subtitle file beside it.
        private static async Task WriteEpisodeAsync(string ffmpeg, string folder, string name)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".en.srt"), "1\n00:00:00,000 --> 00:00:09,000\nHello from the subtitle file.\n");
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=10",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-f", "lavfi", "-i", "sine=frequency=660:duration=10",
                "-map", "0", "-map", "1", "-map", "2", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "200k", "-c:a", "libopus",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=ger", "-shortest", "-y", Path.Combine(folder, name + ".webm")])
            { RedirectStandardError = true })!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, errors);
        }
    }
}
