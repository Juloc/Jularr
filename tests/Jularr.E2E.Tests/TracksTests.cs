using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // An episode with two audio tracks and a subtitle file plays in either language with subtitles; once the server's conversion limit is reached the player says it is busy; a broken episode stops with a visible failure.
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
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg,
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg)!,
                ["Media:MaxTranscodes"] = "1"
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
                    await Steps.SearchEpisodeAsync(page, FakeServices.SeriesTitle, number);
                    await Steps.GrabAsync(page, release);
                }

                var details = await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                var pilot = page.Locator(".episode-card", new() { HasText = "Pilot" }).GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true });
                var brokenEpisode = page.Locator(".episode-card", new() { HasText = "Second" }).GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true });
                await WaitForPlayableAsync(page, details, brokenEpisode);

                // Both audio tracks are offered in their languages; choosing the second asks for a new plan with that stream, and the new source plays.
                await OpenWithTracksAsync(page, details, pilot);
                await page.Locator("[data-action=settings]").ClickAsync();
                await page.Locator("#player-settings").GetByRole(AriaRole.Tab, new() { Name = "Audio", Exact = true }).ClickAsync();
                var audio = page.Locator("[data-panel=audio] select[data-setting=audio]");
                var tracks = audio.Locator("option");
                Assert.AreEqual(2, await tracks.CountAsync());
                await Assertions.Expect(audio).ToHaveValueAsync(await tracks.Nth(0).GetAttributeAsync("value") ?? "");
                var languages = new[] { await tracks.Nth(0).GetAttributeAsync("data-language"), await tracks.Nth(1).GetAttributeAsync("data-language") };
                Assert.IsTrue(languages.All(language => !string.IsNullOrEmpty(language)) && languages[0] != languages[1], string.Join(", ", languages));
                // A source swap must not save the new session at zero when the old video pauses.
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime > 2", null, new() { Timeout = Steps.MediaTimeout });
                var progressRequests = new ConcurrentQueue<(string Url, string Body)>();
                page.Request += (_, request) =>
                {
                    if (request.Method == "PUT" && request.Url.Contains("/api/v1/playback/sessions/", StringComparison.Ordinal))
                        progressRequests.Enqueue((request.Url, request.PostData ?? ""));
                };
                var stream = await tracks.Nth(1).GetAttributeAsync("value");
                var source = await page.Locator("video").EvaluateAsync<string>("video => video.currentSrc");
                var plan = await page.RunAndWaitForResponseAsync(() => audio.SelectOptionAsync(stream!),
                    response => response.Request.Method == "POST" && response.Request.PostData?.Contains("\"audioStreamIndex\":" + stream, StringComparison.Ordinal) == true,
                    new() { Timeout = Steps.MediaTimeout });
                Assert.IsTrue(plan.Ok, "The plan for the second track answered " + plan.Status);
                await page.WaitForFunctionAsync("source => document.querySelector('video').currentSrc !== source", source, new() { Timeout = Steps.MediaTimeout });
                var switchedAt = await page.Locator("video").EvaluateAsync<double>("video => video.currentTime");
                await page.WaitForFunctionAsync("at => document.querySelector('video').currentTime > at + 1", switchedAt, new() { Timeout = Steps.MediaTimeout });
                using (var planData = JsonDocument.Parse(await plan.TextAsync()))
                {
                    var newSession = planData.RootElement.GetProperty("playbackSessionId").GetString()!;
                    Assert.IsFalse(progressRequests.Any(update => update.Url.Contains(newSession, StringComparison.OrdinalIgnoreCase)
                        && JsonDocument.Parse(update.Body).RootElement.GetProperty("positionMs").GetInt64() < 1000),
                        "Changing the source must not overwrite the new session with zero progress before metadata loads.");
                }

                // The subtitle file beside it is the only subtitle, in English, with its own text.
                await page.Locator("#player-settings").GetByRole(AriaRole.Tab, new() { Name = "Subtitles", Exact = true }).ClickAsync();
                var subtitleField = page.Locator("[data-panel=subtitles] select[data-setting=subtitle]");
                var subtitles = subtitleField.Locator("option:not([value=''])");
                Assert.AreEqual(1, await subtitles.CountAsync());
                StringAssert.StartsWith(await subtitles.GetAttributeAsync("data-language") ?? "", "en");
                await subtitleField.SelectOptionAsync(await subtitles.GetAttributeAsync("value") ?? "");
                await page.WaitForFunctionAsync("() => [...document.querySelector('video').textTracks].some(track => track.mode === 'showing' && track.cues && track.cues.length > 0)",
                    null, new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual("Hello from the subtitle file.", await page.Locator("video").EvaluateAsync<string>(
                    "video => [...video.textTracks].find(track => track.mode === 'showing').cues[0].text"));
                await page.Keyboard.PressAsync("Escape");
                await page.Locator("[data-action=subtitle-toggle]").ClickAsync();
                Assert.AreEqual(0, await page.Locator("video track").CountAsync());
                await page.Locator("[data-action=subtitle-toggle]").ClickAsync();
                await page.WaitForFunctionAsync("() => [...document.querySelector('video').textTracks].some(track => track.mode === 'showing' && track.cues?.length > 0)");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());

                // The file's chapters name its intro and credits: a Skip button while the intro plays (the new plan of the audio choice above kept the markers), then the chapter menu.
                await page.EvaluateAsync("() => { const video = document.querySelector('video'); video.pause(); video.currentTime = 2.5; }");
                var skip = page.Locator("[data-action=skip]");
                await Assertions.Expect(skip).ToBeVisibleAsync();
                await Assertions.Expect(skip).ToHaveTextAsync("Skip intro");
                await Evidence.SaveAsync(page, "player-skip-button");
                await AssertSkipInEveryModeAsync(page, skip);
                await skip.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime >= 5");
                await Assertions.Expect(skip).ToBeHiddenAsync();
                await page.Locator("[data-action=chapters]").ClickAsync();
                var chapters = page.Locator("#player-chapters [data-chapter]");
                Assert.AreEqual(4, await chapters.CountAsync());
                CollectionAssert.AreEqual(new[] { "Cold open", "Opening", "Part A", "End Credits" }, (await chapters.Locator("span:first-child").AllTextContentsAsync()).ToArray());
                await Assertions.Expect(chapters.Nth(2)).ToHaveAttributeAsync("aria-pressed", "true");
                await Evidence.SaveAsync(page, "player-chapters-menu");
                await chapters.Nth(1).ClickAsync();
                await page.WaitForFunctionAsync("() => { const time = document.querySelector('video').currentTime; return time >= 2 && time < 3; }");
                await Assertions.Expect(page.Locator("#player-chapters")).ToBeHiddenAsync();

                // The ticks on the timeline stand at the chapters' starts (not at 0) and seek there.
                var ticks = page.Locator(".player__mark");
                Assert.AreEqual(3, await ticks.CountAsync());
                await page.WaitForFunctionAsync("() => document.querySelector('.player__marks')?.hasAttribute('data-ready')");
                await ticks.Nth(1).ClickAsync();
                await page.WaitForFunctionAsync("() => { const time = document.querySelector('video').currentTime; return time >= 5 && time < 6; }");

                // Every pane of the settings fits without a scrollbar: normal, wide and full screen, light and dark, on a desktop and a phone.
                foreach (var mode in new[] { "light", "dark" })
                {
                    await page.EvaluateAsync("mode => { document.documentElement.dataset.mode = mode; }", mode);
                    await AssertSettingsFitAsync(page, "normal " + mode);
                }
                await page.Locator("[data-action=theater]").ClickAsync();
                await AssertEpisodesFirstAsync(page);
                await AssertSettingsFitAsync(page, "wide");
                await page.Locator("[data-action=fullscreen]").ClickAsync();
                await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('.player')");
                await AssertEpisodesFirstAsync(page);
                await AssertSettingsFitAsync(page, "fullscreen");
                await page.Locator("[data-action=fullscreen]").ClickAsync();
                await page.WaitForFunctionAsync("() => !document.fullscreenElement");
                await page.Locator("[data-action=theater]").ClickAsync();
                await page.SetViewportSizeAsync(390, 844);
                await AssertSettingsFitAsync(page, "phone");
                await page.SetViewportSizeAsync(844, 390);
                await AssertSettingsFitAsync(page, "phone landscape");
                await page.SetViewportSizeAsync(1280, 720);
                await page.EvaluateAsync("() => { document.documentElement.dataset.mode = 'light'; }");

                // Automatic skipping jumps by itself and says so; credits that run to the end open the next episode after a pause that Undo cancels.
                var antiforgery = await page.Locator(".player").GetAttributeAsync("data-antiforgery");
                var saved = await page.EvaluateAsync<bool>("""
                    async token => (await fetch('/api/v1/playback/preferences', { method: 'PUT', credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
                        body: JSON.stringify({ audioLanguage: null, subtitleLanguage: null, subtitleModeTypeId: 4, speed: 1, autoplayNext: true, introSkipModeTypeId: 3, outroSkipModeTypeId: 3 }) })).ok
                    """, antiforgery);
                Assert.IsTrue(saved, "The automatic skip choice was not saved.");
                await page.ReloadAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = Steps.MediaTimeout });
                await page.EvaluateAsync("() => { const video = document.querySelector('video'); video.pause(); video.currentTime = 2.2; }");
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime >= 5 && !document.querySelector('[data-role=skipped]').hidden");
                await Assertions.Expect(page.Locator("[data-role=skipped-text]")).ToHaveTextAsync("Intro skipped");
                await Evidence.SaveAsync(page, "player-skipped-toast");
                await page.Locator("[data-action=skip-undo]").ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime < 3");
                await Assertions.Expect(page.Locator("[data-role=skipped]")).ToBeHiddenAsync();
                await Assertions.Expect(skip).ToBeVisibleAsync();
                var playerUrl = page.Url;
                await page.EvaluateAsync("() => { document.querySelector('video').currentTime = 8.2; }");
                await Assertions.Expect(page.Locator("[data-role=skipped-text]")).ToHaveTextAsync("Next episode starts");
                await page.Locator("[data-action=skip-undo]").ClickAsync();
                await page.WaitForTimeoutAsync(5500);
                Assert.AreEqual(playerUrl, page.Url, "The next episode opened although the skip was undone.");
                // The first track plays directly and needs no room, which also gives back the conversion of the second one.
                if (!await page.Locator("#player-settings").IsVisibleAsync()) await page.Locator("[data-action=settings]").ClickAsync();
                await page.Locator("#player-settings").GetByRole(AriaRole.Tab, new() { Name = "Audio", Exact = true }).ClickAsync();
                var firstTrack = await tracks.Nth(0).GetAttributeAsync("value");
                if (await audio.InputValueAsync() == firstTrack)
                {
                    // A select only answers a change: go to the second track first, which still fits.
                    var toSecond = await page.RunAndWaitForResponseAsync(() => audio.SelectOptionAsync(stream!),
                        response => response.Request.Method == "POST" && response.Url.Contains("/sessions", StringComparison.Ordinal), new() { Timeout = Steps.MediaTimeout });
                    Assert.AreEqual(200, toSecond.Status);
                }

                var direct = await page.RunAndWaitForResponseAsync(() => audio.SelectOptionAsync(firstTrack!),
                    response => response.Request.Method == "POST" && response.Url.Contains("/sessions", StringComparison.Ordinal), new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual(200, direct.Status);

                // The server allows one 1080p conversion, so only four of these small ones fit; the one after finds no room.
                var started = new List<string>();
                var refusal = "";
                for (var attempt = 0; attempt < 8 && refusal == ""; attempt++)
                {
                    var (code, body) = await StartConversionAsync(page);
                    if (code == 200) started.Add(JsonDocument.Parse(body).RootElement.GetProperty("playbackSessionId").GetString()!);
                    else refusal = body;
                }

                StringAssert.Contains(refusal, "playback_capacity_exhausted");
                Assert.AreEqual(4, started.Count, started.Count + " conversions started before the limit.");

                // The second track needs a conversion, which the server refuses, and the player says so.
                var refused = await page.RunAndWaitForResponseAsync(() => audio.SelectOptionAsync(stream!),
                    response => response.Request.Method == "POST" && response.Url.Contains("/sessions", StringComparison.Ordinal), new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual(503, refused.Status);
                var busy = page.Locator("[data-role=status]");
                await Assertions.Expect(busy).ToBeVisibleAsync(new() { Timeout = Steps.MediaTimeout });
                Assert.AreEqual(await page.Locator("[data-status=playback_capacity_exhausted]").TextContentAsync(), await busy.TextContentAsync());
                Assert.AreEqual("Server busy: transcoding is unavailable right now.", await busy.TextContentAsync());
                await Evidence.SaveAsync(page, "player-server-busy");

                // The refusals are the only server errors; ending the other sessions gives the room back.
                Assert.IsTrue(session.Problems.All(problem => problem.Contains("503", StringComparison.Ordinal)), string.Join("\n", session.Problems));
                session.Problems.Clear();
                foreach (var id in started) await EndSessionAsync(page, id);

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


        // Every pane of the settings fits its fixed size: nothing scrolls inside it, and it stays inside the screen, above the timeline's right edge where there is room.
        private static async Task AssertSettingsFitAsync(IPage page, string label)
        {
            await page.Locator("[data-action=settings]").ClickAsync();
            var settings = page.Locator("#player-settings");
            await Assertions.Expect(settings).ToBeVisibleAsync();
            var viewport = page.ViewportSize!;
            var box = (await settings.BoundingBoxAsync())!;
            Assert.IsTrue(box.X >= 0 && box.Y >= 0 && box.X + box.Width <= viewport.Width + 1 && box.Y + box.Height <= viewport.Height + 1, $"{label}: the settings leave the screen.");
            var floating = viewport.Width > 620 && viewport.Height > 600;
            Assert.IsTrue(!floating || box.Width <= 400.5 && box.Height <= 460.5, $"{label}: the settings are larger than 400x460.");
            foreach (var name in new[] { "Basic", "Audio", "Subtitles", "Playback", "Info" })
            {
                await settings.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true }).ClickAsync();
                var overflowing = await settings.EvaluateAsync<string[]>("""
                    popover => [popover, popover.querySelector('.player__settings-body'), popover.querySelector('.player__settings-pane:not([hidden])'), popover.querySelector('.navigation-tabs')]
                        .filter(element => element.scrollHeight > element.clientHeight || element.scrollWidth > element.clientWidth)
                        .map(element => element.className + ' ' + element.scrollWidth + 'x' + element.scrollHeight + ' in ' + element.clientWidth + 'x' + element.clientHeight)
                    """);
                CollectionAssert.AreEqual(Array.Empty<string>(), overflowing, $"{label}, {name}: something scrolls inside the settings: " + string.Join("; ", overflowing));
            }

            await settings.GetByRole(AriaRole.Tab, new() { Name = "Basic", Exact = true }).ClickAsync();
            if (floating)
            {
                var timeline = (await page.Locator("[data-role=timeline]").BoundingBoxAsync())!;
                Assert.AreEqual(timeline.X + timeline.Width, box.X + box.Width, 1.5, $"{label}: the settings end at the timeline's right edge.");
                Assert.AreEqual(timeline.Y - 8, box.Y + box.Height, 1.5, $"{label}: the settings sit 8px above the timeline.");
            }

            await Evidence.SaveAsync(page, "player-settings-" + label.Replace(' ', '-'));
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(settings).ToBeHiddenAsync();
        }

        // Where the list is not beside the picture, Episodes is the first button of the bottom bar's right group.
        private static async Task AssertEpisodesFirstAsync(IPage page)
        {
            var episodes = page.Locator("[data-action=episodes]");
            await Assertions.Expect(episodes).ToBeVisibleAsync();
            var first = (await episodes.BoundingBoxAsync())!;
            var timeline = (await page.Locator("[data-role=timeline]").BoundingBoxAsync())!;
            Assert.IsTrue(first.Y > timeline.Y, "Episodes belongs to the bottom bar, below the timeline.");
            foreach (var other in await page.Locator(".player__options > .player__button:visible").AllAsync())
            {
                if (await other.GetAttributeAsync("data-action") == "episodes") continue;
                Assert.IsTrue((await other.BoundingBoxAsync())!.X > first.X, "Episodes comes before the other buttons.");
            }
            Assert.AreEqual("episodes", await page.Locator(".player__options > .player__button:visible").First.GetAttributeAsync("data-action"));
        }

        // The skip button stays visible, inside the picture and above the timeline, whichever view is open.
        private static async Task AssertSkipInEveryModeAsync(IPage page, ILocator skip)
        {
            await page.EvaluateAsync("() => { const video = document.querySelector('video'); video.pause(); video.currentTime = 2.5; }");
            foreach (var mode in new[] { "normal", "wide", "fullscreen" })
            {
                if (mode == "wide") await page.Locator("[data-action=theater]").ClickAsync();
                if (mode == "fullscreen")
                {
                    await page.Locator("[data-action=fullscreen]").ClickAsync();
                    await page.WaitForFunctionAsync("() => document.fullscreenElement === document.querySelector('.player')");
                }

                await Assertions.Expect(skip).ToBeVisibleAsync();
                var button = (await skip.BoundingBoxAsync())!;
                var timeline = (await page.Locator("[data-role=timeline]").BoundingBoxAsync())!;
                var viewport = page.ViewportSize!;
                Assert.IsTrue(button.X >= 0 && button.X + button.Width <= viewport.Width && button.Y >= 0, $"{mode}: the skip button leaves the screen.");
                Assert.IsTrue(button.Y + button.Height <= timeline.Y, $"{mode}: the skip button covers the timeline.");
                await Evidence.SaveAsync(page, "player-skip-" + mode);
            }

            await page.Locator("[data-action=fullscreen]").ClickAsync();
            await page.WaitForFunctionAsync("() => !document.fullscreenElement");
            await page.Locator("[data-action=theater]").ClickAsync();
        }

        // Asks for a conversion the way the player does, for a browser that plays H.264 and AAC from HLS only.
        private static async Task<(int Status, string Body)> StartConversionAsync(IPage page)
        {
            var answer = await page.EvaluateAsync<JsonElement>("""
                async () => {
                    const player = document.querySelector('.player');
                    const response = await fetch(player.dataset.startUrl, { method: 'POST', credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json', RequestVerificationToken: player.dataset.antiforgery },
                        body: JSON.stringify({ containers: ['hls'], videoCodecs: ['h264'], audioCodecs: ['aac'], audioStreamIndex: null, quality: null }) });
                    return { status: response.status, body: await response.text() };
                }
                """);
            return (answer.GetProperty("status").GetInt32(), answer.GetProperty("body").GetString()!);
        }

        private static async Task EndSessionAsync(IPage page, string sessionId)
        {
            var status = await page.EvaluateAsync<int>("""
                async id => {
                    const player = document.querySelector('.player');
                    const response = await fetch(player.dataset.sessionUrl + id, { method: 'PUT', credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json', RequestVerificationToken: player.dataset.antiforgery },
                        body: JSON.stringify({ positionMs: 0, durationMs: null, isEnded: true, isCompleted: false }) });
                    return response.status;
                }
                """, sessionId);
            Assert.AreEqual(200, status);
        }

        // The download monitor imports one episode after the other.
        // The library scan after the import reads the tracks; until then the player offers no choice of them. Once played, the episode offers to resume, so the player itself is reloaded.
        private static async Task OpenWithTracksAsync(IPage page, string details, ILocator play)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            await page.GotoAsync(details);
            await play.ClickAsync();
            var player = page.Url;
            while (true)
            {
                await page.WaitForFunctionAsync("() => document.querySelector('video')?.readyState >= 1", null, new() { Timeout = Steps.MediaTimeout });
                if (await page.Locator("select[data-setting=audio] option").CountAsync() > 2)
                {
                    return;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The episode's tracks were not read within two minutes.");
                }

                await Task.Delay(2000);
                await page.GotoAsync(player);
            }
        }

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

            // Four chapters; the second is the opening and the last the credits.
            var chapters = Path.Combine(Path.GetDirectoryName(folder)!, name + ".chapters.txt");
            await File.WriteAllTextAsync(chapters, ";FFMETADATA1\n" + string.Concat(new[] { (0, 2000, "Cold open"), (2000, 5000, "Opening"), (5000, 8000, "Part A"), (8000, 10000, "End Credits") }
                .Select(chapter => $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={chapter.Item1}\nEND={chapter.Item2}\ntitle={chapter.Item3}\n")));
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=10",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-f", "lavfi", "-i", "sine=frequency=660:duration=10", "-i", chapters, "-map_metadata", "3", "-map_chapters", "3",
                "-map", "0", "-map", "1", "-map", "2", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "200k", "-c:a", "libopus",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=ger", "-shortest", "-y", Path.Combine(folder, name + ".webm")])
            { RedirectStandardError = true })!;
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, errors);
        }
    }
}
