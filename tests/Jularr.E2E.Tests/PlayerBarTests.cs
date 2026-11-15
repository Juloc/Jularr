using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A song starts in the bar at the bottom and keeps playing while the app is browsed; the bar moves to the corner the viewer drags it to and remembers it; Close is a clear X;
    // the album's cover stands where a picture would be; a long album lists every track.
    [TestClass]
    public sealed class PlayerBarTests
    {
        // Two albums with files (4 tracks, and 60 for the list) and the files of a fake library; the playback plan is a stand-in below, so no FFmpeg is needed.
        private const string Seed = """
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9201, 6, 'Example Album', 2019), (9202, 6, 'Long Album', 2020), (9203, 1, 'Example Film', 2021);
            INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (9201, 'Poster', 'https://127.0.0.1/cover.jpg', TRUE);
            INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "Title", "DiscNumber")
                SELECT 9201, 5, number, 'Song ' || number, 1 FROM generate_series(1, 4) AS number;
            INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "Title", "DiscNumber")
                SELECT 9202, 5, number, 'Long ' || number, 1 + number / 31 FROM generate_series(1, 60) AS number;
            INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM "Works";
            INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") SELECT "WorkId", "Id" FROM "WorkEditions";
            INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT unit."WorkId", version."Id", unit."Id", 2 FROM "WorkUnits" AS unit JOIN "WorkVersions" AS version ON version."WorkId" = unit."WorkId";
            INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") SELECT "WorkId", "Id", 1 FROM "WorkVersions" WHERE "WorkId" = 9203;
            DO $$ DECLARE mount bigint;
            BEGIN
                INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Page fixtures', 3, '/page-test-library', FALSE) RETURNING "Id" INTO mount;
                INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Music', 6, mount, 'music');
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId")
                    SELECT (SELECT "Id" FROM "LibraryRoots" LIMIT 1), asset."Id" || '.flac', 100, now(), now(), asset."Id" FROM "MediaAssets" AS asset;
            END $$;
            """;

        private const string Cover = "<svg xmlns='http://www.w3.org/2000/svg' width='500' height='500'><rect width='500' height='500' fill='#c0392b'/><circle cx='250' cy='250' r='150' fill='#f1c40f'/></svg>";

        // A minute of a quiet tone: a real file the browser plays.
        private static byte[] Tone()
        {
            const int rate = 8000;
            const int samples = rate * 60;
            var data = new byte[44 + samples];
            void Text(int at, string text) { for (var index = 0; index < text.Length; index++) data[at + index] = (byte)text[index]; }
            void Number(int at, int value) => BitConverter.GetBytes(value).CopyTo(data, at);
            void Short(int at, short value) => BitConverter.GetBytes(value).CopyTo(data, at);
            Text(0, "RIFF"); Number(4, 36 + samples); Text(8, "WAVEfmt "); Number(16, 16); Short(20, 1); Short(22, 1); Number(24, rate); Number(28, rate); Short(32, 1); Short(34, 8); Text(36, "data"); Number(40, samples);
            for (var index = 0; index < samples; index++) data[44 + index] = (byte)(128 + 40 * Math.Sin(index / 8.0));
            return data;
        }

        private static async Task<JsonElement> StateAsync(IPage page) => await page.EvaluateAsync<JsonElement>("""
            () => {
                const main = document.querySelector('.player-main');
                const box = main?.getBoundingClientRect();
                const video = document.querySelector('video');
                return { path: location.pathname, mini: !!document.querySelector('.player--mini'), corner: main?.dataset.corner ?? null, address: document.querySelector('.player')?.dataset.address ?? null,
                    paused: video?.paused ?? null, time: video?.currentTime ?? null, same: video?.dataset.mark ?? null,
                    left: box?.left ?? null, top: box?.top ?? null, right: box?.right ?? null, bottom: box?.bottom ?? null, width: box?.width ?? null, height: box?.height ?? null,
                    width_: innerWidth, height_: innerHeight };
            }
            """);

        [TestMethod]
        [Timeout(600_000)]
        public async Task Song_PlaysInTheBar_StaysWhileBrowsing_MovesToACorner_AndClosesWithTheX()
        {
            var tone = Tone();
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:AniList"] = "http://127.0.0.1:9/", ["Providers:OpenLibrary"] = "http://127.0.0.1:9/", ["Providers:MusicBrainz"] = "http://127.0.0.1:9/"
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync(Seed);
            await page.RouteAsync("https://127.0.0.1/cover.jpg", route => route.FulfillAsync(new() { ContentType = "image/svg+xml", Body = Cover }));
            await page.RouteAsync("**/api/v1/playback/assets/*/sessions", route => route.FulfillAsync(new()
            {
                ContentType = "application/json", Body = "{\"playbackSessionId\":\"test\",\"playbackModeTypeId\":1,\"streamUrl\":\"/test/tone.wav\",\"resumePositionMs\":0}"
            }));
            await page.RouteAsync("**/api/v1/playback/sessions/**", route => route.FulfillAsync(new() { ContentType = "application/json", Body = "{}" }));
            await page.RouteAsync("**/test/tone.wav", route =>
            {
                if (!route.Request.Headers.TryGetValue("range", out var range))
                    return route.FulfillAsync(new() { ContentType = "audio/wav", BodyBytes = tone, Headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" } });
                var parts = range.Replace("bytes=", "").Split('-');
                var from = int.Parse(parts[0]);
                var to = Math.Min(parts[1].Length > 0 ? int.Parse(parts[1]) : tone.Length - 1, tone.Length - 1);
                return route.FulfillAsync(new() { Status = 206, ContentType = "audio/wav", BodyBytes = tone[from..(to + 1)], Headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes", ["Content-Range"] = $"bytes {from}-{to}/{tone.Length}" } });
            });

            try
            {
                await page.SetViewportSizeAsync(1400, 900);

                // Every track of a long album is listed, not the first page of them.
                await page.GotoAsync("/Details/9202?tab=tracks");
                await Assertions.Expect(page.Locator(".details-tracks tbody tr")).ToHaveCountAsync(60);

                // A track clicked in the album starts in the bar at the bottom right; the album page stays where it is.
                await page.GotoAsync("/Details/9201?tab=tracks");
                await page.Locator("a[aria-label='Play']").Nth(1).ClickAsync();
                await Assertions.Expect(page.Locator(".player--mini")).ToBeVisibleAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('video').currentTime > 0.5");
                var bar = await StateAsync(page);
                Assert.AreEqual("/Details/9201", bar.GetProperty("path").GetString());
                Assert.IsTrue(bar.GetProperty("mini").GetBoolean());
                Assert.AreEqual("br", bar.GetProperty("corner").GetString());
                Assert.IsTrue(bar.GetProperty("right").GetDouble() >= bar.GetProperty("width_").GetDouble() - 20 && bar.GetProperty("bottom").GetDouble() >= bar.GetProperty("height_").GetDouble() - 20, bar.ToString());

                // The cover stands where the picture would be: the album's, loaded, never a black square.
                var cover = page.Locator(".player--mini .player__cover img");
                await Assertions.Expect(cover).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("cover\\.jpg"));
                Assert.IsTrue(await cover.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"));
                await Assertions.Expect(page.Locator(".player--mini .player__title")).ToHaveTextAsync("Song 2");

                // It keeps playing, in the same media element, while the app swaps its pages.
                await page.EvaluateAsync("() => { document.querySelector('video').dataset.mark = 'same'; document.querySelector('a[href=\"/Library\"]').click(); }");
                await page.WaitForURLAsync("**/Library");
                await page.WaitForTimeoutAsync(1200);
                var browsing = await StateAsync(page);
                Assert.IsTrue(browsing.GetProperty("mini").GetBoolean());
                Assert.AreEqual("same", browsing.GetProperty("same").GetString());
                Assert.IsFalse(browsing.GetProperty("paused").GetBoolean());
                Assert.IsTrue(browsing.GetProperty("time").GetDouble() > bar.GetProperty("time").GetDouble());

                // It ends where a song ends: the next track follows at once, still in the bar.
                await page.EvaluateAsync("() => { const video = document.querySelector('video'); video.currentTime = video.duration - 0.3; }");
                await Assertions.Expect(page.Locator(".player--mini .player__title")).ToHaveTextAsync("Song 3", new() { Timeout = 20_000 });
                Assert.IsTrue((await StateAsync(page)).GetProperty("mini").GetBoolean());

                // Tapping the cover opens the whole player over the page; Close is an X, 44 px, labelled, and the way back.
                await page.Locator(".player--mini .player__cover").ClickAsync();
                await Assertions.Expect(page.Locator(".player:not(.player--mini)")).ToBeVisibleAsync();
                var close = page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
                var closeBox = (await close.BoundingBoxAsync())!;
                Assert.IsTrue(closeBox.Width >= 44 && closeBox.Height >= 44, closeBox.Width + "x" + closeBox.Height);
                Assert.IsTrue(closeBox.X + closeBox.Width > 1300 && closeBox.Y < 80, "Close is at the top right.");
                await page.Locator("[data-action=minimize]").ClickAsync();
                await Assertions.Expect(page.Locator(".player--mini")).ToBeVisibleAsync();
                Assert.AreEqual("/Library", (await StateAsync(page)).GetProperty("path").GetString());

                // Dragged to the top left it snaps there and stays there after a reload.
                var from = (await page.Locator(".player-main").BoundingBoxAsync())!;
                await page.Mouse.MoveAsync(from.X + 150, from.Y + 40);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(from.X + 100, from.Y - 100, new() { Steps = 6 });
                await page.Mouse.MoveAsync(80, 220, new() { Steps = 10 });
                await page.Mouse.UpAsync();
                await Assertions.Expect(page.Locator(".player-main")).ToHaveAttributeAsync("data-corner", "tl");
                Assert.AreEqual("tl", await page.EvaluateAsync<string>("() => localStorage.getItem('jularr.player.corner')"));
                await page.ReloadAsync();
                await page.Locator("a[href^='/Details/']").First.WaitForAsync(new() { State = WaitForSelectorState.Attached });

                // A phone's bar is as wide as the screen at the bottom.
                await page.SetViewportSizeAsync(375, 812);
                await page.GotoAsync("/Details/9201?tab=tracks");
                await page.Locator("a[aria-label='Play']").Nth(0).ClickAsync();
                await Assertions.Expect(page.Locator(".player--mini")).ToBeVisibleAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.player-main').getAnimations().length === 0");
                var phone = await StateAsync(page);
                Assert.AreEqual(0, phone.GetProperty("left").GetDouble(), 1);
                Assert.AreEqual(375, phone.GetProperty("width").GetDouble(), 1);
                Assert.AreEqual(812, phone.GetProperty("bottom").GetDouble(), 1);

                // Its X ends the playback and the bar.
                await page.Locator(".player--mini .player__close").ClickAsync();
                await Assertions.Expect(page.Locator(".player-main")).ToHaveCountAsync(0);

                // A film takes the whole screen over its page; Back shrinks it into the bar, the bar opens it again, and Close returns to the page.
                await page.GotoAsync("/Details/9203");
                await page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First.ClickAsync();
                await page.WaitForURLAsync("**/Player/*");
                await Assertions.Expect(page.Locator(".player:not(.player--mini)")).ToBeVisibleAsync();
                await page.GoBackAsync();
                await Assertions.Expect(page.Locator(".player--mini")).ToBeVisibleAsync();
                Assert.AreEqual("/Details/9203", (await StateAsync(page)).GetProperty("path").GetString());
                await page.Locator(".player--mini [data-action=expand]").ClickAsync();
                await page.WaitForURLAsync("**/Player/*");
                await Assertions.Expect(page.Locator(".player:not(.player--mini)")).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Details/9203");
                await Assertions.Expect(page.Locator(".player-main")).ToHaveCountAsync(0);

                // The player's own page: Close takes the viewer back to where they came from.
                await page.SetViewportSizeAsync(1400, 900);
                var asset = await jularr.ScalarAsync("SELECT min(\"Id\") FROM \"MediaAssets\" WHERE \"WorkId\" = 9201");
                await page.GotoAsync("/Details/9201");
                await page.GotoAsync("/Player/" + asset);
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Details/9201");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.Where(problem => !problem.Contains("127.0.0.1", StringComparison.Ordinal)).ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "player-bar");
                throw;
            }
        }
    }
}
