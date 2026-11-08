using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Casting a season: the device gets the episode and the next three as a queue with a session each, the player's controls follow the episode on the device,
    // the progress of every episode is saved on its own session, and when the device lets go the page goes on with the episode and place it had reached.
    // Google's sender library is replaced by a fake that records what the page asks of it and plays the part of the device.
    [TestClass]
    public sealed class CastQueueTests
    {
        private const string Host = "cast.localhost";

        private static IPage? _tv;

        // The device: a queue it plays by itself, which the test moves along (finish, next) as the receiver would.
        private const string FakeSender = """
            (() => {
                const listeners = {};
                const remote = { isConnected: false, isPaused: true, currentTime: 0, duration: 0, volumeLevel: 0.5, isMuted: false, mediaInfo: null };
                const fake = window.__fakeCast = { calls: [], queue: [], index: -1, remote, active: [], listeners };
                const emit = () => { listeners.any?.(); listeners.state?.(); };
                fake.emit = emit;
                fake.show = (index, time) => {
                    const item = fake.queue[index];
                    fake.index = index;
                    remote.mediaInfo = item ? item.media : null;
                    remote.currentTime = time ?? (item ? item.startTime : 0);
                    remote.duration = item ? 600 : 0;
                    remote.isPaused = !item;
                    fake.active = item ? [...item.activeTrackIds] : [];
                    emit();
                };
                fake.tick = seconds => { remote.currentTime += seconds; emit(); };
                // The item runs out: the receiver goes on with the next one by itself, or has nothing left.
                fake.finish = () => { remote.currentTime = remote.duration; emit(); fake.show(fake.index + 1 < fake.queue.length ? fake.index + 1 : -1); };
                fake.disconnect = () => { remote.isConnected = false; emit(); };
                const media = {
                    queueAppendItem: (item, ok) => { fake.calls.push(['append']); fake.queue.push(item); ok(); },
                    queueNext: ok => { fake.calls.push(['next']); fake.show(Math.min(fake.queue.length - 1, fake.index + 1)); ok(); },
                    queuePrev: ok => { fake.calls.push(['prev']); fake.show(Math.max(0, fake.index - 1)); ok(); },
                    editTracksInfo: (request, ok) => { fake.calls.push(['tracks', [...request.activeTrackIds]]); fake.active = [...request.activeTrackIds]; ok(); }
                };
                const session = {
                    getSessionObj: () => ({ queueLoad: (request, ok) => { fake.calls.push(['load', request.items.length]); fake.queue = [...request.items]; fake.show(request.startIndex); ok(); } }),
                    getMediaSession: () => media,
                    getCastDevice: () => ({ friendlyName: 'Living room TV' })
                };
                class Plain { constructor(...args) { this.args = args; } }
                window.chrome ??= {};
                window.chrome.cast = {
                    AutoJoinPolicy: { ORIGIN_SCOPED: 'origin_scoped' },
                    media: {
                        DEFAULT_MEDIA_RECEIVER_APP_ID: 'CC1AD845', StreamType: { BUFFERED: 'BUFFERED' }, HlsSegmentFormat: { TS: 'ts' }, TrackType: { TEXT: 'TEXT' },
                        TextTrackType: { SUBTITLES: 'SUBTITLES' }, RepeatMode: { OFF: 'REPEAT_OFF' },
                        MediaInfo: class { constructor(contentId, contentType) { this.contentId = contentId; this.contentType = contentType; } },
                        GenericMediaMetadata: Plain,
                        Track: class { constructor(trackId, type) { this.trackId = trackId; this.type = type; } },
                        QueueItem: class { constructor(info) { this.media = info; } },
                        QueueLoadRequest: class { constructor(items) { this.items = items; } },
                        EditTracksInfoRequest: class { constructor(ids) { this.activeTrackIds = ids; } }
                    }
                };
                window.cast = {
                    framework: {
                        CastContext: { getInstance: () => ({
                            setOptions: () => undefined,
                            addEventListener: (type, handler) => { listeners.state = handler; },
                            getCurrentSession: () => remote.isConnected ? session : null,
                            requestSession: async () => { remote.isConnected = true; emit(); },
                            endCurrentSession: () => { fake.calls.push(['end']); fake.disconnect(); }
                        }) },
                        RemotePlayer: class { constructor() { return remote; } },
                        RemotePlayerController: class {
                            addEventListener(type, handler) { listeners.any = handler; }
                            playOrPause() { remote.isPaused = !remote.isPaused; fake.calls.push(['toggle']); emit(); }
                            seek() { fake.calls.push(['seek', remote.currentTime]); emit(); }
                            setVolumeLevel() { fake.calls.push(['volume', remote.volumeLevel]); emit(); }
                            muteOrUnmute() { remote.isMuted = !remote.isMuted; fake.calls.push(['mute']); emit(); }
                        },
                        RemotePlayerEventType: { ANY_CHANGE: 'any' },
                        CastContextEventType: { SESSION_STATE_CHANGED: 'state' }
                    }
                };
                window.__onGCastApiAvailable(true);
            })();
            """;

        [TestMethod]
        [Timeout(900_000)]
        public async Task Cast_QueuesTheSeason_FollowsTheDeviceAndGoesOnLocallyWhereItLeft()
        {
            using var folders = new Folders("Series");
            var bytes = Enumerable.Range(0, 64 * 1024).Select(index => (byte)(index * 31 % 251)).ToArray();
            var first = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR", "s01e01", 5040, Path.Combine(folders.Fixtures, "first.mkv"));
            var second = new FakeRelease("Jularr.Test.Series.S01E02.1080p.WEB-DL.H264-JLR", "s01e02", 5040, Path.Combine(folders.Fixtures, "second.mkv"));
            await File.WriteAllBytesAsync(first.File, bytes);
            await File.WriteAllBytesAsync(second.File, bytes);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:Images:artworks.thetvdb.com"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                // The conversions are only planned, a device that is faked never asks for a segment; the files are bytes whose streams are written into the database below.
                ["Media:FfmpegPath"] = "/bin/true",
                ["Media:FfprobePath"] = ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            CastBrowser? cast = null;

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

                await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                await WaitForAsync(async () => (long)(await jularr.ScalarAsync("SELECT count(*) FROM \"StoredFiles\" WHERE NOT \"IsSubtitle\" AND \"MediaAssetId\" IS NOT NULL"))! == 2);

                // Three more episodes of the season, a German and an English subtitle file for each, and the streams of every file.
                var extra = Directory.CreateDirectory(Path.Combine(folders.Library, "Series", "cast-extra")).FullName;
                // Register the fixture tracks before placing their files in the watched library.
                // Otherwise a concurrent library scan can insert the same relative paths first.
                await jularr.RunSqlAsync("""
                    DO $$
                    DECLARE source record; unit bigint; asset bigint; n int;
                    BEGIN
                        SELECT asset."WorkId", asset."WorkVersionId", asset."MediaAssetTypeId", unit."ParentWorkUnitId", unit."WorkUnitTypeId", file."LibraryRootId" INTO source
                        FROM "MediaAssets" AS asset
                            JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND NOT file."IsSubtitle"
                        WHERE unit."Number" = 2;
                        FOR n IN 3..5 LOOP
                            INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title")
                            VALUES (source."WorkId", source."ParentWorkUnitId", source."WorkUnitTypeId", n, 'Part ' || n) RETURNING "Id" INTO unit;
                            INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                            VALUES (source."WorkId", source."WorkVersionId", unit, source."MediaAssetTypeId") RETURNING "Id" INTO asset;
                            INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                            VALUES (source."LibraryRootId", 'cast-extra/Episode ' || n || '.mkv', 65536, now(), now(), asset);
                        END LOOP;
                    END $$;
                    UPDATE "StoredFiles" SET "ProbedAt" = now(), "DurationMs" = 600000 WHERE NOT "IsSubtitle";
                    INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                    SELECT file."LibraryRootId", 'cast-extra/asset-' || file."MediaAssetId" || '.' || language || '.srt', 100, now(), now(), file."MediaAssetId"
                    FROM "StoredFiles" AS file CROSS JOIN (VALUES ('en'), ('de')) AS languages (language)
                    WHERE NOT file."IsSubtitle";
                    INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Channels", "IsDefault", "IsForced")
                    SELECT file."Id", 0, 1, 'h264', NULL, NULL, TRUE, FALSE FROM "StoredFiles" AS file WHERE NOT file."IsSubtitle"
                    UNION ALL SELECT file."Id", 1, 2, 'aac', 'eng', 2, TRUE, FALSE FROM "StoredFiles" AS file WHERE NOT file."IsSubtitle"
                    UNION ALL SELECT file."Id", 2, 2, 'aac', 'ger', 2, FALSE, FALSE FROM "StoredFiles" AS file WHERE NOT file."IsSubtitle";
                    """);
                // Now the scanner sees already-registered tracks instead of racing their insert.
                foreach (var number in new[] { 3, 4, 5 })
                    await File.WriteAllBytesAsync(Path.Combine(extra, "Episode " + number + ".mkv"), bytes);
                var assets = new List<long>();
                for (var number = 1; number <= 5; number++)
                {
                    assets.Add((long)(await jularr.ScalarAsync("SELECT asset.\"Id\" FROM \"MediaAssets\" AS asset JOIN \"WorkUnits\" AS unit ON unit.\"Id\" = asset.\"WorkUnitId\" WHERE unit.\"Number\" = " + number))!);
                }

                // The device reaches the page by a name that is not loopback, and Chrome offers Cast on a secure page.
                var origin = "http://" + Host + ":" + jularr.Address.Port;
                cast = await CastBrowser.StartAsync(origin, jularr);
                var tv = _tv = cast.Page;
                tv.Console += (_, message) => Evidence.Log("[tv] " + message.Type + " " + message.Text);
                tv.PageError += (_, error) => Evidence.Log("[tv] error " + error);
                tv.Response += (_, response) => { if (response.Status >= 400) Evidence.Log("[tv] " + response.Status + " " + response.Url); };

                // Casting from the first episode: the device gets it and the next three, each on a session of its own, subtitles included.
                await tv.GotoAsync("/Player/" + assets[0]);
                await tv.WaitForFunctionAsync("() => document.body.classList.contains('player-active')", null, new() { Timeout = Steps.MediaTimeout });
                await tv.Locator("[data-action=cast]").DispatchEventAsync("click");
                await tv.WaitForFunctionAsync("() => window.__fakeCast?.queue.length === 4 && document.querySelector('.player--casting')", null, new() { Timeout = Steps.MediaTimeout });
                CollectionAssert.AreEqual(new[] { "load" }, (await Calls(tv)).Select(call => call[0]).Distinct().ToArray());
                var queue = await tv.EvaluateAsync<string[][]>("() => window.__fakeCast.queue.map(item => [item.media.contentId, item.media.metadata.subtitle, item.autoplay.toString(), item.media.tracks.map(track => track.language).join(',')])");
                Assert.AreEqual(4, queue.Select(item => item[0]).Distinct().Count());
                Assert.IsTrue(queue.All(item => item[0].StartsWith(origin + "/api/v1/cast/", StringComparison.Ordinal) && item[0].EndsWith("/playlist.m3u8", StringComparison.Ordinal)));
                CollectionAssert.AreEqual(new[] { "true", "true", "true", "true" }, queue.Select(item => item[2]).ToArray());
                Assert.IsTrue(queue.All(item => item[3] == "de,en" || item[3] == "en,de"), string.Join(" | ", queue.Select(item => item[3])));
                await WaitForAsync(async () => (long)(await jularr.ScalarAsync("SELECT count(*) FROM \"PlaybackSessions\" WHERE \"EndedAt\" IS NULL"))! == 4);
                await Assertions.Expect(tv.Locator("[data-role=casting-text]")).ToContainTextAsync("Living room TV");
                await Shot(tv, "casting-episode-1");

                // The first episode runs out: the device goes on, the countdown-less advance counts the episode as watched, and the queue is filled again with a fifth.
                await tv.EvaluateAsync("() => { window.__fakeCast.tick(300); window.__fakeCast.finish(); }");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.queue.length === 5", null, new() { Timeout = Steps.MediaTimeout });
                await Assertions.Expect(tv.Locator(".player__context")).ToContainTextAsync("Episode 2");
                await Assertions.Expect(tv.Locator(".player__episodes [aria-current=true]")).ToContainTextAsync("Episode 2");
                await WaitForAsync(async () => (bool?)await jularr.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\" AS progress JOIN \"WorkUnits\" AS unit ON unit.\"Id\" = progress.\"WorkUnitId\" WHERE unit.\"Number\" = 1") == true);
                await WaitForAsync(async () => (long)(await jularr.ScalarAsync("SELECT count(*) FROM \"PlaybackSessions\" WHERE \"EndedAt\" IS NULL"))! == 4);

                // Next and Previous of the page drive the device's queue.
                await tv.Locator("[data-action=next]").DispatchEventAsync("click");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.index === 2", null, new() { Timeout = Steps.MediaTimeout });
                await Assertions.Expect(tv.Locator(".player__context")).ToContainTextAsync("Episode 3");
                await tv.Locator("[data-action=previous]").DispatchEventAsync("click");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.index === 1", null, new() { Timeout = Steps.MediaTimeout });
                await tv.Locator("[data-action=next]").DispatchEventAsync("click");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.index === 2", null, new() { Timeout = Steps.MediaTimeout });

                // The volume, the subtitle and the audio track follow to the device.
                await tv.Locator("[data-role=volume]").EvaluateAsync("input => { input.value = '0.8'; input.dispatchEvent(new Event('input', { bubbles: true })); }");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.calls.some(call => call[0] === 'volume' && call[1] === 0.8)");
                await tv.Locator("[data-action=settings]").DispatchEventAsync("click");
                var german = tv.Locator("select[data-setting=subtitle] option[data-language=de]").First;
                await tv.Locator("select[data-setting=subtitle]").First.SelectOptionAsync(await german.GetAttributeAsync("value") ?? "");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.calls.some(call => call[0] === 'tracks' && call[1].length === 1)");
                await Shot(tv, "casting-settings");
                await tv.Keyboard.PressAsync("Escape");
                await tv.EvaluateAsync("() => { window.__fakeCast.tick(125); }");
                await tv.WaitForFunctionAsync("() => window.__fakeCast.index === 2 && window.__fakeCast.remote.currentTime > 120");

                // The device lets go in the third episode at two minutes: its page opens there, every session of the queue has ended.
                await tv.Locator("[data-action=cast]").DispatchEventAsync("click");
                await tv.WaitForURLAsync("**/Player/" + assets[2], new() { Timeout = Steps.MediaTimeout });
                await WaitForAsync(async () => (long)(await jularr.ScalarAsync("SELECT count(*) FROM \"PlaybackSessions\" WHERE \"EndedAt\" IS NULL"))! <= 1);
                var saved = (long)(await jularr.ScalarAsync("SELECT progress.\"PositionMs\" FROM \"MediaProgress\" AS progress JOIN \"WorkUnits\" AS unit ON unit.\"Id\" = progress.\"WorkUnitId\" WHERE unit.\"Number\" = 3"))!;
                Assert.IsTrue(saved >= 120_000 && saved < 130_000, "The third episode's place was " + saved + " ms.");
                Assert.AreEqual(false, await jularr.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\" AS progress JOIN \"WorkUnits\" AS unit ON unit.\"Id\" = progress.\"WorkUnitId\" WHERE unit.\"Number\" = 3"));

                // The player while casting on a tablet and a phone.
                await tv.GotoAsync("/Player/" + assets[0]);
                await tv.WaitForFunctionAsync("() => document.body.classList.contains('player-active')", null, new() { Timeout = Steps.MediaTimeout });
                await tv.Locator("[data-action=cast]").DispatchEventAsync("click");
                await tv.WaitForFunctionAsync("() => window.__fakeCast?.queue.length >= 2 && document.querySelector('.player--casting')", null, new() { Timeout = Steps.MediaTimeout });
                await tv.EvaluateAsync("() => { window.__fakeCast.tick(95); }");
                foreach (var (name, width, height) in new[] { ("tablet", 768, 1024), ("phone", 375, 812), ("desktop", 1280, 800) })
                {
                    await tv.SetViewportSizeAsync(width, height);
                    await tv.WaitForTimeoutAsync(400);
                    await Shot(tv, "casting-" + name);
                    var overflow = await tv.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
                    Assert.IsFalse(overflow, "The player scrolls sideways at " + width + " px.");
                }
            }
            catch
            {
                if (_tv is not null)
                {
                    Evidence.Log("[tv] state " + await _tv.EvaluateAsync<string>("() => JSON.stringify({ secure: window.isSecureContext, fake: !!window.__fakeCast, queue: window.__fakeCast?.queue.length, connected: window.__fakeCast?.remote.isConnected, status: document.querySelector('[data-role=status]')?.textContent, hidden: document.querySelector('[data-action=cast]')?.hidden, url: location.href, casting: !!document.querySelector('.player--casting') })"));
                    await Evidence.SaveAsync(_tv, "cast-tv");
                }
                await Evidence.SaveAsync(page, nameof(Cast_QueuesTheSeason_FollowsTheDeviceAndGoesOnLocallyWhereItLeft));
                throw;
            }
            finally
            {
                if (cast is not null) await cast.DisposeAsync();
            }
        }

        private static async Task<string[][]> Calls(IPage page) => await page.EvaluateAsync<string[][]>("() => window.__fakeCast.calls.map(call => call.map(String))");

        // Pictures of the player while casting, kept where JULARR_CAST_SCREENSHOTS points.
        private static async Task Shot(IPage page, string name)
        {
            if (Environment.GetEnvironmentVariable("JULARR_CAST_SCREENSHOTS") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                // The controls fade after a few seconds of playing.
                await page.Mouse.MoveAsync(200, 200);
                await page.WaitForTimeoutAsync(300);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, name + ".png") });
            }
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!await condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within a minute.");
                await Task.Delay(500);
            }
        }

        // A browser that reaches the instance under another name and treats it as secure, as a phone does on the home network; Google's library is the fake.
        private sealed class CastBrowser : IAsyncDisposable
        {
            private readonly IPlaywright _playwright;
            private readonly IBrowser _browser;

            private CastBrowser(IPlaywright playwright, IBrowser browser, IPage page)
            {
                _playwright = playwright;
                _browser = browser;
                Page = page;
            }

            public IPage Page { get; }

            public static async Task<CastBrowser> StartAsync(string origin, JularrInstance jularr)
            {
                var playwright = await Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync(new() { Args = [$"--host-resolver-rules=MAP {Host} 127.0.0.1"] });
                var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = origin, ViewportSize = new() { Width = 1280, Height = 800 } });
                await context.RouteAsync(url => new Uri(url) is { IsLoopback: false, Scheme: "http" or "https" } uri && uri.Host != Host, route => route.AbortAsync());
                await context.RouteAsync("https://www.gstatic.com/cv/js/sender/v1/cast_sender.js*", route => route.FulfillAsync(new() { ContentType = "text/javascript", Body = FakeSender }));
                var page = await context.NewPageAsync();
                await page.GotoAsync("/Account/Login");
                await page.FillAsync("[name=Username]", Owner.Username);
                await page.FillAsync("[name=Password]", Owner.Password);
                await page.Locator("form button[type=submit]").First.ClickAsync();
                await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
                return new CastBrowser(playwright, browser, page);
            }

            public async ValueTask DisposeAsync()
            {
                await _browser.DisposeAsync();
                _playwright.Dispose();
            }
        }
    }
}
