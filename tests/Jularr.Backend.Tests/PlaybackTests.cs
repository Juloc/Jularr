using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Integrations.Logic.Storage;
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
        public void Tracks_SidecarNamesAndSubRip_BecomeLanguagesAndWebVtt()
        {
            Assert.AreEqual(("de", true), PlaybackTracks.Describe("Film (2001)/Film.de.forced.srt"));
            Assert.AreEqual(("pt-br", false), PlaybackTracks.Describe("Film.pt-br.sdh.srt"));
            Assert.AreEqual(((string?)null, false), PlaybackTracks.Describe("Film (2001)/Film.srt"));
            Assert.AreEqual(((string?)null, false), PlaybackTracks.Describe("Film.The.Cut.srt"));
            StringAssert.Contains(PlaybackTracks.SrtToVtt("1\n0:00:01,000 --> 0:00:02,000\nHi\n"), "00:00:01.000 --> 00:00:02.000");
            Assert.AreEqual("WEBVTT\n\n1\n00:00:01.000 --> 00:00:02.500\nHallo\n", PlaybackTracks.SrtToVtt("\uFEFF1\r\n00:00:01,000 --> 00:00:02,500\r\nHallo\r\n"));
            Assert.AreEqual(((int?)3, (long?)null), PlaybackTracks.ParseKey("s3"));
            Assert.AreEqual(((int?)null, (long?)12), PlaybackTracks.ParseKey("f12"));
            Assert.ThrowsExactly<KeyNotFoundException>(() => PlaybackTracks.ParseKey("x1"));
            Assert.AreEqual("Grüße", PlaybackTracks.Decode(System.Text.Encoding.Latin1.GetBytes("Grüße")));

            // Another audio track never plays directly; its conversion maps that track.
            var tracks = new[] { new PlaybackTrackData(0, MediaTrackType.Video, "h264", true), new PlaybackTrackData(1, MediaTrackType.Audio, "aac", true),
                new PlaybackTrackData(2, MediaTrackType.Audio, "aac", false) };
            Assert.IsTrue(PlaybackLogic.CanPlayDirectly("mp4", tracks, Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(), Hls.AudioCodecs.ToHashSet(), 1));
            Assert.IsFalse(PlaybackLogic.CanPlayDirectly("mp4", tracks, Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(), Hls.AudioCodecs.ToHashSet(), 2));
            var conversion = PlaybackLogic.PlanConversion(tracks, Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(), Hls.AudioCodecs.ToHashSet(), 2)!;
            Assert.AreEqual(2, conversion.AudioStreamIndex);
            Assert.AreEqual(PlaybackModeType.Remux, conversion.PlaybackModeTypeId);
        }

        [TestMethod]
        public async Task Tracks_ListAudioAndSubtitlesAndServeSubRipAsWebVtt()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: new Dictionary<string, string>
            {
                ["Media:FfmpegPath"] = Path.Combine(cache.Path, "missing-ffmpeg"),
                ["Media:TranscodeCachePath"] = cache.Path
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            var folder = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Movies", "Film (2001)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Film.mp4"), "jklmnopq");
            File.WriteAllText(Path.Combine(folder, "Film.de.forced.srt"), "1\r\n00:00:01,000 --> 00:00:02,000\r\nGrüße\r\n");
            File.WriteAllText(Path.Combine(folder, "Film.en.ass"), "[Script Info]");
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            await LibraryTests.ScanAsync(host, rootId);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\" WHERE \"RelativePath\" LIKE '%.mp4'"))!;
            using var assigned = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/library/files/" + fileId + "/assignment", new AssignLibraryFileDataV1(null, "Film", null, null, null));
            var assetId = (await assigned.Content.ReadFromJsonAsync<AssignLibraryFileResultV1>())!.MediaAssetId;
            await host.Fixture.RunSqlAsync($$"""
                UPDATE "StoredFiles" SET "ProbedAt" = now(), "DurationMs" = 13500 WHERE "Id" = {{fileId}};
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Title", "Channels", "IsDefault", "IsForced") VALUES
                    ({{fileId}}, 0, 1, 'h264', NULL, NULL, NULL, TRUE, FALSE),
                    ({{fileId}}, 1, 2, 'aac', 'eng', NULL, 6, TRUE, FALSE),
                    ({{fileId}}, 2, 2, 'aac', 'ger', 'Kommentar', 2, FALSE, FALSE),
                    ({{fileId}}, 3, 3, 'subrip', 'eng', NULL, NULL, FALSE, FALSE),
                    ({{fileId}}, 4, 3, 'hdmv_pgs_subtitle', 'ger', NULL, NULL, FALSE, FALSE);
                """);

            // Streams first, then the subtitle files beside the film; image subtitles are listed but unsupported.
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/tracks");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var tracks = (await listed.Content.ReadFromJsonAsync<BaseDataList<PlaybackTrackV1>>())!;
            CollectionAssert.AreEqual(new[] { "s1", "s2", "s3", "s4" }, tracks.Take(4).Select(track => track.Key).ToArray());
            Assert.IsFalse(tracks.Single(track => track.Key == "s4").IsSupported);
            var sidecar = tracks.Single(track => track.Language == "de" && track.Key.StartsWith('f'));
            Assert.IsTrue(sidecar.IsForced);
            Assert.IsTrue(sidecar.IsSupported);

            // SubRip arrives as WebVTT; an image stream is no text subtitle.
            using var subtitle = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/subtitles/" + sidecar.Key);
            Assert.AreEqual(HttpStatusCode.OK, subtitle.StatusCode, await subtitle.Content.ReadAsStringAsync());
            Assert.AreEqual("text/vtt", subtitle.Content.Headers.ContentType!.MediaType);
            var vtt = await subtitle.Content.ReadAsStringAsync();
            StringAssert.StartsWith(vtt, "WEBVTT");
            StringAssert.Contains(vtt, "00:00:01.000 --> 00:00:02.000");
            StringAssert.Contains(vtt, "Grüße");
            using var image = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/subtitles/s4");
            Assert.AreEqual(HttpStatusCode.NotFound, image.StatusCode);

            // The commentary is remuxed; unknown tracks are refused.
            using var commentary = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", new StartPlaybackDataV1(Hls.Containers, Hls.VideoCodecs, Hls.AudioCodecs, 2));
            Assert.AreEqual(HttpStatusCode.OK, commentary.StatusCode, await commentary.Content.ReadAsStringAsync());
            Assert.AreEqual(PlaybackModeType.Remux, (await commentary.Content.ReadFromJsonAsync<PlaybackPlanV1>())!.PlaybackModeTypeId);
            using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", new StartPlaybackDataV1(Hls.Containers, Hls.VideoCodecs, Hls.AudioCodecs, 0));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            // The player offers both choices with readable languages.
            using var page = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-action=\"settings\"");
            StringAssert.Contains(html, "data-panel=\"audio\"");
            StringAssert.Contains(html, "data-setting=\"audio\"");
            StringAssert.Contains(html, "<option value=\"1\"");
            StringAssert.Contains(html, "<option value=\"2\"");
            StringAssert.Contains(html, "data-action=\"minimize\" aria-label=\"Minimize\" hidden=\"hidden\"");
            StringAssert.Contains(html, "Deutsch &#xB7; Kommentar &#xB7; Stereo");
            StringAssert.Contains(html, "<option value=\"" + sidecar.Key + "\"");
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
        public void Music_DeviceQuality_ConvertsOnlyClearlyLargerAudio()
        {
            // A lossless album track on a device asking for small music becomes 128 kbit/s AAC; a small MP3, the original setting and video stay as they are.
            var flac = new[] { new PlaybackTrackData(0, MediaTrackType.Audio, "flac", true) };
            Assert.AreEqual(128, PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.Saver), flac, 900_000));
            Assert.IsNull(PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.Saver), [new PlaybackTrackData(0, MediaTrackType.Audio, "mp3", true)], 128_000));
            Assert.IsNull(PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.Original), flac, 900_000));
            Assert.IsNull(PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.Automatic), flac, 900_000));
            Assert.IsNull(PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.High), flac, null));
            Assert.IsNull(PlaybackLogic.AudioKilobits(PlaybackLogic.QualityTarget(PlaybackQuality.High), [new PlaybackTrackData(0, MediaTrackType.Video, "h264", true), .. flac], 9_000_000));
            var conversion = PlaybackLogic.PlanConversion([new PlaybackTrackData(0, MediaTrackType.Audio, "aac", true)], Hls.Containers.ToHashSet(), Hls.VideoCodecs.ToHashSet(),
                Hls.AudioCodecs.ToHashSet(), audioKilobits: 256)!;
            Assert.AreEqual((PlaybackModeType.Transcode, false, 256), (conversion.PlaybackModeTypeId, conversion.CopyAudio, conversion.AudioKilobits));
            var arguments = MediaTranscoder.BuildArguments(new TranscodeRequest("/music/a.flac", "/cache/s", 0, 6, null, 0, true, false, [], []) { AudioKilobits = 128 }).ToList();
            Assert.AreEqual("128k", arguments[arguments.IndexOf("-b:a") + 1]);
        }

        [TestMethod]
        public void Playlist_FixedGrid_CoversWholeDuration()
        {
            var playlist = PlaybackTranscodes.BuildPlaylist(new PlaybackTranscodeSource(Guid.NewGuid(), 1, new StorageInput("film.mkv", null), 0, 1, true, true, 13_500));
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

            // The Info pane asks what the playback is made of: the probed streams and what the server does with them.
            using var factsResponse = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl.Replace("playlist.m3u8", "facts", StringComparison.Ordinal)));
            Assert.AreEqual(HttpStatusCode.OK, factsResponse.StatusCode, await factsResponse.Content.ReadAsStringAsync());
            var facts = (await factsResponse.Content.ReadFromJsonAsync<PlaybackFactsV1>())!;
            Assert.AreEqual(PlaybackModeType.Remux, facts.PlaybackModeTypeId);
            Assert.AreEqual("h264", facts.VideoCodec);
            Assert.AreEqual("aac", facts.AudioCodec);
            Assert.AreEqual(true, facts.CopyVideo);
            Assert.AreEqual(true, facts.CopyAudio);

            // Moving the audio against the picture re-encodes it: the same file now needs a conversion of its own.
            using var movedStart = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", new StartPlaybackDataV1(["hls"], ["h264"], ["aac"], audioOffsetMs: 250));
            Assert.AreEqual(HttpStatusCode.OK, movedStart.StatusCode, await movedStart.Content.ReadAsStringAsync());
            var moved = (await movedStart.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
            Assert.AreEqual(PlaybackModeType.Transcode, moved.PlaybackModeTypeId);
            StringAssert.EndsWith(moved.StreamUrl, "/playlist.m3u8");
            using var movedFacts = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, moved.StreamUrl.Replace("playlist.m3u8", "facts", StringComparison.Ordinal)));
            var movedInfo = (await movedFacts.Content.ReadFromJsonAsync<PlaybackFactsV1>())!;
            Assert.AreEqual(true, movedInfo.CopyVideo);
            Assert.AreEqual(false, movedInfo.CopyAudio);
            Assert.AreEqual(192, movedInfo.AudioKilobits);
            using var outOfRange = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", new StartPlaybackDataV1(["hls"], ["h264"], ["aac"], audioOffsetMs: 20_000));
            Assert.AreEqual(HttpStatusCode.BadRequest, outOfRange.StatusCode);

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

            // A cast device cannot sign in: it plays from a signed address of this session, fetched from another origin, and from nowhere else.
            using var castAddress = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl.Replace("playlist.m3u8", "cast", StringComparison.Ordinal)));
            Assert.AreEqual(HttpStatusCode.OK, castAddress.StatusCode, await castAddress.Content.ReadAsStringAsync());
            var cast = (await castAddress.Content.ReadFromJsonAsync<PlaybackCastV1>())!;
            StringAssert.StartsWith(cast.Url, "/api/v1/cast/");
            StringAssert.EndsWith(cast.Url, "/playlist.m3u8");
            using var anonymous = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            using var castPlaylist = await anonymous.GetAsync(cast.Url);
            Assert.AreEqual(HttpStatusCode.OK, castPlaylist.StatusCode);
            StringAssert.Contains(await castPlaylist.Content.ReadAsStringAsync(), "segments/2.ts");
            Assert.AreEqual("*", castPlaylist.Headers.GetValues("Access-Control-Allow-Origin").Single());
            using var castSegment = await anonymous.GetAsync(cast.Url.Replace("playlist.m3u8", "segments/0.ts", StringComparison.Ordinal));
            Assert.AreEqual(HttpStatusCode.OK, castSegment.StatusCode);
            Assert.AreEqual("ts0", await castSegment.Content.ReadAsStringAsync());
            Assert.AreEqual("*", castSegment.Headers.GetValues("Access-Control-Allow-Origin").Single());
            using var withoutAddress = await anonymous.GetAsync(plan.StreamUrl);
            Assert.AreEqual(HttpStatusCode.Unauthorized, withoutAddress.StatusCode);
            var token = cast.Url["/api/v1/cast/".Length..^"/playlist.m3u8".Length];
            using var forged = await anonymous.GetAsync("/api/v1/cast/" + token[..^3] + "AAA/playlist.m3u8");
            Assert.AreEqual(HttpStatusCode.NotFound, forged.StatusCode);

            // A deferred commit failure must leave the stream and its cache available.
            await host.Fixture.RunSqlAsync(
                """
                CREATE FUNCTION "FailSessionEnd"() RETURNS trigger LANGUAGE plpgsql AS '
                BEGIN
                    IF NEW."EndedAt" IS NOT NULL THEN
                        RAISE EXCEPTION ''forced playback commit failure'';
                    END IF;
                    RETURN NEW;
                END;';
                CREATE CONSTRAINT TRIGGER "FailSessionEndTrigger" AFTER UPDATE ON "PlaybackSessions"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION "FailSessionEnd"();
                """);
            using var rejected = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId,
                new UpdatePlaybackDataV1(13_000, 13_500, true, true));
            Assert.AreEqual(HttpStatusCode.InternalServerError, rejected.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"EndedAt\" IS NULL FROM \"PlaybackSessions\" WHERE \"PublicId\" = '" + plan.PlaybackSessionId + "'"));
            Assert.IsTrue(Directory.Exists(directory));
            using var retained = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl));
            Assert.AreEqual(HttpStatusCode.OK, retained.StatusCode);
            await host.Fixture.RunSqlAsync("DROP TRIGGER \"FailSessionEndTrigger\" ON \"PlaybackSessions\"; DROP FUNCTION \"FailSessionEnd\"()");

            // Ending playback releases the conversion cache only after the update commits.
            using var ended = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + plan.PlaybackSessionId, new UpdatePlaybackDataV1(13_000, 13_500, true, true));
            Assert.AreEqual(HttpStatusCode.OK, ended.StatusCode, await ended.Content.ReadAsStringAsync());
            Assert.IsFalse(Directory.Exists(directory));
            using var gone = await SendAsync(host, new HttpRequestMessage(HttpMethod.Get, plan.StreamUrl));
            Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
            using var castGone = await anonymous.GetAsync(cast.Url);
            Assert.AreEqual(HttpStatusCode.NotFound, castGone.StatusCode);
        }

        [TestMethod]
        public async Task CacheSize_CountsEveryFileAndIgnoresFoldersRemovedWhileCounting()
        {
            using var cache = new LibraryTests.TemporaryDirectory();
            Directory.CreateDirectory(Path.Combine(cache.Path, "a"));
            File.WriteAllBytes(Path.Combine(cache.Path, "a", "segment0.ts"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(cache.Path, "b"));
            var hidden = Path.Combine(cache.Path, "b", "segment1.tmp");
            File.WriteAllBytes(hidden, new byte[50]);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            Assert.AreEqual(150, PlaybackTranscodes.CacheBytes(cache.Path));
            Assert.AreEqual(0, PlaybackTranscodes.CacheBytes(Path.Combine(cache.Path, "missing")));

            // Sessions end while the cache is counted for a new one: their folders vanish, and the count is no failure.
            using var stop = new CancellationTokenSource();
            var churn = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var folder = Path.Combine(cache.Path, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder, "segment0.ts"), new byte[10]);
                    Directory.Delete(folder, true);
                }
            });
            for (var index = 0; index < 300; index++)
            {
                Assert.IsTrue(PlaybackTranscodes.CacheBytes(cache.Path) >= 150);
            }

            await stop.CancelAsync();
            await churn;
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
            transcodes.Register(new PlaybackTranscodeSource(session, 7, new StorageInput(input, null), 0, 1, false, false, 300_000));

            // First segment, then a seek that restarts the job.
            var first = await transcodes.GetSegmentAsync(session, 7, 0, CancellationToken.None);
            Assert.IsTrue(new FileInfo(first).Length > 0);
            var seek = await transcodes.GetSegmentAsync(session, 7, 6, CancellationToken.None);
            Assert.IsTrue(new FileInfo(seek).Length > 0);

            // A second conversion exceeds the budget of one.
            var other = Guid.NewGuid();
            transcodes.Register(new PlaybackTranscodeSource(other, 7, new StorageInput(input, null), 0, 1, false, false, 300_000));
            var busy = await Assert.ThrowsExactlyAsync<Jularr.Backend.Data.Common.ServiceUnavailableException>(() => transcodes.GetSegmentAsync(other, 7, 0, CancellationToken.None));
            Assert.AreEqual("playback_capacity_exhausted", busy.Code);

            transcodes.Stop(session);
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(first)));
        }

        [TestMethod]
        public async Task Trickplay_RenderedSheets_ShowThePlayedFilesThumbnails()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var thumbnails = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: new Dictionary<string, string> { ["Media:TrickplayPath"] = thumbnails.Path });
            var (assetId, _) = await CreatePlayableAsync(host, storage.Path, wakeOnDemand: false);
            var fileId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"StoredFiles\""))!;
            await host.Fixture.RunSqlAsync($"""
                UPDATE "StoredFiles" SET "TrickplayFrames" = 50, "TrickplayCheckedAt" = now() WHERE "Id" = {fileId};
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Width", "Height", "IsDefault", "IsForced") VALUES ({fileId}, 0, 1, 1920, 800, TRUE, FALSE);
                """);
            Directory.CreateDirectory(Path.Combine(thumbnails.Path, fileId.ToString()));
            File.WriteAllBytes(Path.Combine(thumbnails.Path, fileId.ToString(), "001.jpg"), [0xFF, 0xD8, 0xFF]);
            var route = "/api/v1/playback/assets/" + assetId + "/trickplay";

            // Off by default; switched on, the layout follows the video's shape.
            using var off = await host.SendAsync(HttpMethod.Get, route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Trickplay'");
            using var layout = await host.SendAsync(HttpMethod.Get, route);
            var trickplay = (await layout.Content.ReadFromJsonAsync<TrickplayV1>())!;
            Assert.AreEqual(50, trickplay.Frames);
            Assert.AreEqual(66, trickplay.TileHeight);
            using var sheet = await host.SendAsync(HttpMethod.Get, route + "/1");
            Assert.AreEqual("image/jpeg", sheet.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(new byte[] { 0xFF, 0xD8, 0xFF }, await sheet.Content.ReadAsByteArrayAsync());
            using var missing = await host.SendAsync(HttpMethod.Get, route + "/2");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            StringAssert.Contains(await player.Content.ReadAsStringAsync(), "data-trickplay-url=\"" + route + "\"");

            // Without FFmpeg nothing is rendered and the file waits.
            await host.Fixture.RunSqlAsync($"UPDATE \"StoredFiles\" SET \"TrickplayFrames\" = NULL, \"TrickplayCheckedAt\" = NULL, \"ProbedAt\" = now(), \"DurationMs\" = 60000 WHERE \"Id\" = {fileId}");
            await using var scope = host.Services.CreateAsyncScope();
            Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<GenerateTrickplayV1>().ExecuteAsync<UpdateResult>(new TrickplayParametersV1(), default, CancellationToken.None)).Success);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"TrickplayCheckedAt\" IS NULL FROM \"StoredFiles\""));
        }

        internal static async Task<(long AssetId, long WorkId)> CreatePlayableAsync(Host host, string storagePath, bool wakeOnDemand, string fileName = "Film.mp4")
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

        internal static async Task<PlaybackPlanV1> StartAsync(Host host, long assetId)
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
