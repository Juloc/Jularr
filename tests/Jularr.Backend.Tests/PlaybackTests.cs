using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.User.Playback;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class PlaybackTests
    {
        private static readonly StartPlaybackDataV1 Browser = new(["mp4", "webm"], ["h264", "vp9"], ["aac", "opus"]);
        private static readonly StartPlaybackDataV1 Hls = new(["hls", "mp4", "webm"], ["h264", "vp9"], ["aac", "opus"]);

        [TestMethod]
        public async Task DirectPlay_StreamsRangesAndResumesPerProfile()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (assetId, workId) = await CreatePlayableAsync(host, storage.Path, wakeOnDemand: false);

            using var item = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId);
            Assert.AreEqual(HttpStatusCode.OK, item.StatusCode, await item.Content.ReadAsStringAsync());
            Assert.AreEqual("Film", (await item.Content.ReadFromJsonAsync<PlaybackItemV1>())!.CanonicalTitle);

            // A plan creates the session; opening never completes.
            var plan = await StartAsync(host, assetId);
            Assert.AreEqual(PlaybackModeType.DirectPlay, plan.PlaybackModeTypeId);
            Assert.AreEqual(0L, plan.ResumePositionMs);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaProgress\""));

            // Authorized range responses.
            using var range = new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl);
            range.Headers.Range = new RangeHeaderValue(2, 5);
            using var partial = await SendAsync(host, range);
            Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.AreEqual("lmno", await partial.Content.ReadAsStringAsync());
            Assert.AreEqual("video/mp4", partial.Content.Headers.ContentType!.MediaType);

            using var progress = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId, new UpdatePlaybackDataV1(5_000, 60_000, false, false));
            Assert.AreEqual(HttpStatusCode.OK, progress.StatusCode, await progress.Content.ReadAsStringAsync());
            Assert.AreEqual(5_000L, (await StartAsync(host, assetId)).ResumePositionMs);
            using var availability = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/availability");
            var asset = (await availability.Content.ReadFromJsonAsync<PageResult<WorkAvailabilityV1>>())!.Items.Single();
            Assert.AreEqual(5_000L, asset.ResumePositionMs);
            Assert.IsFalse(asset.IsCompleted);

            // Explicit end completes and restarts at zero.
            using var ended = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId, new UpdatePlaybackDataV1(60_000, 60_000, true, true));
            Assert.AreEqual(HttpStatusCode.OK, ended.StatusCode, await ended.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsCompleted\" FROM \"MediaProgress\""));
            Assert.AreEqual(0L, (await StartAsync(host, assetId)).ResumePositionMs);
            using var closed = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId, new UpdatePlaybackDataV1(1, null, false, false));
            Assert.AreEqual(HttpStatusCode.NotFound, closed.StatusCode);
            using var stale = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl));
            Assert.AreEqual(HttpStatusCode.NotFound, stale.StatusCode);

            // Foreign sessions do not stream.
            var other = await StartAsync(host, assetId);
            await host.Fixture.RunSqlAsync("BEGIN; INSERT INTO \"Profiles\" (\"Id\", \"OwnerAccountId\", \"DisplayName\", \"UiLocaleId\") SELECT 900, 41, 'Other', \"UiLocaleId\" FROM \"Profiles\" LIMIT 1; "
                + "INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 900); COMMIT;");
            await host.Fixture.RunSqlAsync("UPDATE \"PlaybackSessions\" SET \"ProfileId\" = 900 WHERE \"PublicId\" = '" + other.PlaybackSessionId + "'");
            using var foreign = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, other.StreamUrl));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

            // The Playback module gates every playback service.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Playback'");
            using var disabled = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Browser);
            Assert.AreEqual(HttpStatusCode.Forbidden, disabled.StatusCode);
        }

        [TestMethod]
        public async Task Start_UnplayableOrSleepingStorage_CreatesNoSession()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (assetId, _) = await CreatePlayableAsync(host, storage.Path, wakeOnDemand: true);

            using var conversion = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", new StartPlaybackDataV1(["webm"], ["vp9"], ["opus"]));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, conversion.StatusCode);
            StringAssert.Contains(await conversion.Content.ReadAsStringAsync(), "playback_conversion_unavailable");

            // On-demand wake only where the Owner allowed it.
            Directory.Move(Path.Combine(storage.Path, "1", "Movies"), Path.Combine(storage.Path, "1", "Sleeping"));
            using var waking = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Browser);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, waking.StatusCode);
            StringAssert.Contains(await waking.Content.ReadAsStringAsync(), "storage_waking");
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"WakeOnDemand\" = FALSE");
            using var offline = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Browser);
            StringAssert.Contains(await offline.Content.ReadAsStringAsync(), "storage_unavailable");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"PlaybackSessions\""));
        }

        [TestMethod]
        public async Task Razor_DetailsAndPlayer_OfferCanonicalPlayback()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var (assetId, workId) = await CreatePlayableAsync(host, storage.Path, wakeOnDemand: false);

            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            StringAssert.Contains(html, "href=\"/Player/" + assetId + "\"");

            // The page carries identity, never a file path.
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            html = await player.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, player.StatusCode, html);
            StringAssert.Contains(html, "data-start-url=\"/api/v1/playback/assets/" + assetId + "/sessions\"");
            StringAssert.Contains(html, ">Film</h1>");
            Assert.IsFalse(html.Contains("Film.mp4", StringComparison.Ordinal));
        }

        [TestMethod]
        [DataRow("mp4", "h264", "aac", null)]
        [DataRow("mkv", "h264", "aac", PlaybackModeType.Remux)]
        [DataRow("mp4", "hevc", "aac", PlaybackModeType.Transcode)]
        [DataRow("mp4", "h264", "dts", PlaybackModeType.Transcode)]
        public void Plan_ClientCapabilities_ChooseLeastWork(string container, string video, string audio, PlaybackModeType? expected)
        {
            var tracks = new[] { new PlaybackTrackData(0, MediaTrackType.Video, video, true), new PlaybackTrackData(1, MediaTrackType.Audio, audio, true) };
            var direct = PlaybackLogic.CanPlayDirectly(container, tracks, Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(), Hls.AudioCodecs.ToHashSet());
            var conversion = PlaybackLogic.PlanConversion(tracks, Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(), Hls.AudioCodecs.ToHashSet());
            Assert.AreEqual(expected, direct ? null : conversion?.PlaybackModeTypeId);
            Assert.AreEqual(video == "h264", conversion?.CopyVideo ?? true);

            // Without HLS or H.264 there is nothing to convert to.
            Assert.IsNull(PlaybackLogic.PlanConversion(tracks, Browser.Containers.ToHashSet(), Browser.VideoCodecs.ToHashSet(), Browser.AudioCodecs.ToHashSet()));
            Assert.IsNull(PlaybackLogic.PlanConversion(tracks, Hls.Containers.ToHashSet(), new HashSet<string> { "vp9" }, Hls.AudioCodecs.ToHashSet()));
        }

        [TestMethod]
        public void Transcoder_Arguments_SeekPaceSegmentAndKeepHardwareForEncodingOnly()
        {
            var encode = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 5, 6, 0, 2, false, false, ["-c:v", "h264_vaapi"], ["-hwaccel", "vaapi"])).ToList();
            CollectionAssert.IsSubsetOf(new[] { "-hwaccel", "-readrate", "-readrate_initial_burst", "-force_key_frames", "h264_vaapi", "aac" }, encode);
            Assert.AreEqual("30", encode[encode.IndexOf("-ss") + 1]);
            Assert.AreEqual("5", encode[encode.IndexOf("-start_number") + 1]);
            Assert.AreEqual("0:2", encode[encode.IndexOf("-sn") - 1]);
            Assert.IsTrue(encode.IndexOf("-hwaccel") < encode.IndexOf("-i"));

            var copy = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, true, true, ["-c:v", "h264_vaapi"], ["-hwaccel", "vaapi"])).ToList();
            CollectionAssert.DoesNotContain(copy, "-hwaccel");
            CollectionAssert.DoesNotContain(copy, "-force_key_frames");
            Assert.AreEqual(2, copy.Count(argument => argument == "copy"));
        }

        [TestMethod]
        public void Playlist_FixedGrid_CoversWholeDuration()
        {
            var playlist = PlaybackTranscodes.BuildPlaylist(new PlaybackTranscodeSource(Guid.NewGuid(), 1, "film.mkv", 0, 1, true, true, 13_500));
            StringAssert.Contains(playlist, "#EXT-X-PLAYLIST-TYPE:VOD");
            StringAssert.Contains(playlist, "#EXTINF:1.500000,\nsegments/2.ts\n#EXT-X-ENDLIST");
            Assert.AreEqual(3, playlist.Split("#EXTINF").Length - 1);
        }

        [TestMethod]
        public async Task Hls_ConversionSessions_ServeOwnSegmentsAndReleaseCache()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, settings: new Dictionary<string, string>
            {
                ["Media:FfmpegPath"] = Path.Combine(cache.Path, "missing-ffmpeg"),
                ["Media:TranscodeCachePath"] = cache.Path
            });
            var (assetId, _) = await CreatePlayableAsync(host, storage.Path, wakeOnDemand: false, fileName: "Film.mkv");
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"ProbedAt\" = now(), \"DurationMs\" = 13500; "
                + "INSERT INTO \"MediaTracks\" (\"StoredFileId\", \"StreamIndex\", \"MediaTrackTypeId\", \"Codec\", \"IsDefault\", \"IsForced\") "
                + "SELECT \"Id\", 0, 1, 'h264', TRUE, FALSE FROM \"StoredFiles\" UNION ALL SELECT \"Id\", 1, 2, 'aac', TRUE, FALSE FROM \"StoredFiles\"");

            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Hls);
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var plan = (await started.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
            Assert.AreEqual(PlaybackModeType.Remux, plan.PlaybackModeTypeId);
            StringAssert.EndsWith(plan.StreamUrl, "/playlist.m3u8");
            using var playlist = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl));
            Assert.AreEqual(HttpStatusCode.OK, playlist.StatusCode);
            StringAssert.Contains(await playlist.Content.ReadAsStringAsync(), "segments/2.ts");

            // Finished segments stream; missing ones need FFmpeg.
            var directory = Path.Combine(cache.Path, plan.PlaybackSessionId.ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "segment0.ts"), "ts0");
            var segments = plan.StreamUrl.Replace("playlist.m3u8", "segments/", StringComparison.Ordinal);
            using var ready = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, segments + "0.ts"));
            Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
            Assert.AreEqual("ts0", await ready.Content.ReadAsStringAsync());
            Assert.AreEqual("video/mp2t", ready.Content.Headers.ContentType!.MediaType);
            using var unavailable = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, segments + "1.ts"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using var outside = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, segments + "9.ts"));
            Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);

            // Ending playback releases the conversion cache.
            using var ended = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId, new UpdatePlaybackDataV1(13_000, 13_500, true, true));
            Assert.AreEqual(HttpStatusCode.OK, ended.StatusCode, await ended.Content.ReadAsStringAsync());
            Assert.IsFalse(Directory.Exists(directory));
            using var gone = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl));
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        }

        [TestMethod]
        public async Task Hls_RealFfmpeg_TranscodesSeeksAndStaysWithinBudget()
        {
            // Opt-in: JULARR_TEST_FFMPEG names an ffmpeg executable (7.0 or newer).
            if (Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG") is not { Length: > 0 } ffmpeg)
            {
                Assert.Inconclusive("JULARR_TEST_FFMPEG is not configured.");
                return;
            }

            using var cache = new LibraryTests.TemporaryDirectory();
            using var media = new LibraryTests.TemporaryDirectory();
            var input = Path.Combine(media.Path, "clip.mkv");
            using (var generate = System.Diagnostics.Process.Start(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440", "-t", "300", "-c:v", "mpeg4", "-c:a", "ac3", "-shortest", input]))
            {
                await generate.WaitForExitAsync();
                Assert.AreEqual(0, generate.ExitCode);
            }

            using var transcodes = new PlaybackTranscodes(new MediaTranscoder { ExecutablePath = ffmpeg },
                Microsoft.Extensions.Options.Options.Create(new Jularr.Backend.Service.Core.MediaOptions { TranscodeCachePath = cache.Path, MaxTranscodes = 1 }));
            var session = Guid.NewGuid();
            transcodes.Register(new PlaybackTranscodeSource(session, 7, input, 0, 1, false, false, 300_000));

            // First segment, then a seek that restarts the job.
            var first = await transcodes.GetSegmentAsync(session, 7, 0, CancellationToken.None);
            Assert.IsTrue(new FileInfo(first).Length > 0);
            var seek = await transcodes.GetSegmentAsync(session, 7, 6, CancellationToken.None);
            Assert.IsTrue(new FileInfo(seek).Length > 0);

            // A second conversion exceeds the budget of one.
            var other = Guid.NewGuid();
            transcodes.Register(new PlaybackTranscodeSource(other, 7, input, 0, 1, false, false, 300_000));
            var busy = await Assert.ThrowsExactlyAsync<Jularr.Backend.Data.Common.ServiceUnavailableException>(() => transcodes.GetSegmentAsync(other, 7, 0, CancellationToken.None));
            Assert.AreEqual("playback_capacity_exhausted", busy.Code);

            transcodes.Stop(session);
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(first)));
        }

        private static async Task<(long AssetId, long WorkId)> CreatePlayableAsync(Host host, string storagePath, bool wakeOnDemand, string fileName = "Film.mp4")
        {
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"WakeOnDemand\" = " + (wakeOnDemand ? "TRUE" : "FALSE"));
            var folder = Path.Combine(storagePath, mountId.ToString(), "Movies", "Film (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, fileName), "jklmnopq");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);

            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Film", null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            return ((await assigned.Content.ReadFromJsonAsync<AssignLibraryFileResultV1>())!.MediaAssetId, (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!);
        }

        private static async Task<PlaybackPlanV1> StartAsync(Host host, long assetId)
        {
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Browser);
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            return (await started.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
        }

        private static async Task<HttpResponseMessage> SendAsync(Host host, HttpRequestMessage request)
        {
            // Media elements send cookies, not antiforgery headers.
            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            return await client.SendAsync(request);
        }
    }
}
