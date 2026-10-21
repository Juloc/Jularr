using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class PlaybackPreferenceTests
    {
        private const string Route = "/api/v1/playback/preferences";
        private static readonly StartPlaybackDataV1 Hls = new(["hls", "mp4", "webm"], ["h264", "vp9"], ["aac", "opus"]);
        private static readonly StartPlaybackDataV1 Browser = new(["mp4", "webm"], ["h264", "vp9"], ["aac", "opus"]);

        [TestMethod]
        public async Task Preferences_DefaultsSaveAndSpeed_BelongToTheProfile()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            // Without a choice the defaults apply, in the profile's language and with the device's automatic quality.
            var preferences = await ReadAsync(host);
            Assert.IsNull(preferences.AudioLanguage);
            Assert.IsNull(preferences.SubtitleLanguage);
            Assert.AreEqual(SubtitleMode.WhenForeign, preferences.SubtitleModeTypeId);
            Assert.AreEqual(1.00m, preferences.Speed);
            Assert.IsTrue(preferences.AutoplayNext);
            Assert.IsTrue(preferences.AutoplayPreviews);
            Assert.AreEqual(PlaybackQuality.Automatic, preferences.DeviceQuality);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ProfilePlaybackPreferences\""));

            Assert.AreEqual("de", preferences.ProfileLanguage);

            // The speed chosen in the player keeps the other choices at their defaults.
            using var speed = await host.SendAsync(HttpMethod.Put, Route + "/speed", new SavePlaybackSpeedDataV1(1.5m));
            Assert.AreEqual(HttpStatusCode.OK, speed.StatusCode, await speed.Content.ReadAsStringAsync());
            preferences = await ReadAsync(host);
            Assert.AreEqual((1.50m, SubtitleMode.WhenForeign, true, true), (preferences.Speed, preferences.SubtitleModeTypeId, preferences.AutoplayNext, preferences.AutoplayPreviews));

            // A full save replaces them; the speed alone then leaves the languages as they are. Languages are lower-cased codes.
            using var saved = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("DE", "fr", SubtitleMode.ForcedOnly, 0.75m, false, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var faster = await host.SendAsync(HttpMethod.Put, Route + "/speed", new SavePlaybackSpeedDataV1(2m));
            Assert.AreEqual(HttpStatusCode.OK, faster.StatusCode, await faster.Content.ReadAsStringAsync());
            preferences = await ReadAsync(host);
            Assert.AreEqual(("de", "fr", SubtitleMode.ForcedOnly, 2.00m, false, false), (preferences.AudioLanguage, preferences.SubtitleLanguage, preferences.SubtitleModeTypeId, preferences.Speed, preferences.AutoplayNext, preferences.AutoplayPreviews));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ProfilePlaybackPreferences\""));

            // Unknown languages and speeds outside the player's range are refused; no language is allowed.
            using var language = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("deu", null, SubtitleMode.Off, 1m, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, language.StatusCode);
            using var tooFast = await host.SendAsync(HttpMethod.Put, Route + "/speed", new SavePlaybackSpeedDataV1(3m));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooFast.StatusCode);
            using var cleared = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1(null, null, SubtitleMode.Always, 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"AudioLanguage\" IS NULL AND \"SubtitleLanguage\" IS NULL FROM \"ProfilePlaybackPreferences\""));

            // Another profile of the same account keeps its own row; the services only ever name the active one.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId") SELECT 77, "OwnerAccountId", 'Other', "UiLocaleId" FROM "Profiles" LIMIT 1;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") SELECT "OwnerAccountId", 77 FROM "Profiles" WHERE "Id" = 77;
                INSERT INTO "ProfilePlaybackPreferences" ("ProfileId", "AudioLanguage", "Speed") VALUES (77, 'ja', 1.75);
                """);
            using var again = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("it", null, SubtitleMode.Off, 1.25m, true));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            Assert.AreEqual("ja", await host.ScalarAsync("SELECT \"AudioLanguage\" FROM \"ProfilePlaybackPreferences\" WHERE \"ProfileId\" = 77"));

            // The playback module switches the services and the settings section off.
            using var page = await host.SendAsync(HttpMethod.Get, "/Settings");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "name=\"SubtitleModeTypeId\"");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Playback'");
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var plain = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.IsFalse((await plain.Content.ReadAsStringAsync()).Contains("name=\"SubtitleModeTypeId\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Settings_PlaybackSection_SavesThroughItsForm()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var page = await host.SendAsync(HttpMethod.Get, "/Settings");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "name=\"AudioLanguage\"");
            StringAssert.Contains(html, "name=\"AutoplayNext\"");
            StringAssert.Contains(html, "name=\"AutoplayPreviews\"");
            StringAssert.Contains(html, "data-autoplay-previews=\"true\"");

            using var saved = await host.SendAsync(HttpMethod.Post, "/Settings?handler=Playback", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html),
                ["audioLanguage"] = "ja",
                ["subtitleLanguage"] = "",
                ["subtitleModeTypeId"] = "3",
                ["__Invariant"] = "Speed",
                ["speed"] = "1.25",
                ["autoplayNext"] = "false",
                ["autoplayPreviews"] = "false"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var preferences = await ReadAsync(host);
            Assert.AreEqual(("ja", (string?)null, SubtitleMode.Always, 1.25m, false, false), (preferences.AudioLanguage, preferences.SubtitleLanguage, preferences.SubtitleModeTypeId, preferences.Speed, preferences.AutoplayNext, preferences.AutoplayPreviews));

            // The page shows what was saved.
            using var again = await host.SendAsync(HttpMethod.Get, "/Settings");
            var shown = await again.Content.ReadAsStringAsync();
            Assert.IsTrue(Regex.IsMatch(shown, "<option value=\"ja\" selected"), shown);
            Assert.IsTrue(Regex.IsMatch(shown, "<option value=\"1.25\" selected"), shown);
            StringAssert.Contains(shown, "data-autoplay-previews=\"false\"");
        }

        [TestMethod]
        [DataRow(null, "ger", "eng", "s1")]
        [DataRow("de", "ger", "eng", "s2")]
        [DataRow("de", "deu", "eng", "s2")]
        [DataRow("fr", "ger", "eng", "s1")]
        public void Audio_FollowsTheLanguage_AndFallsBackToTheFilesDefault(string? wanted, string second, string first, string expected)
        {
            var tracks = new PlaybackChoiceTrack[] { new("s1", MediaTrackType.Audio, first, true, false), new("s2", MediaTrackType.Audio, second, false, false) };
            Assert.AreEqual(expected, PlaybackTracks.ChooseAudio(tracks, wanted)?.Key);
            Assert.IsNull(PlaybackTracks.ChooseAudio([], wanted));
        }

        [TestMethod]
        // Always: a full track of the language, a forced one when it is all there is.
        [DataRow(SubtitleMode.Always, "eng", "de", "s5")]
        [DataRow(SubtitleMode.Always, "ger", "de", "s5")]
        [DataRow(SubtitleMode.Always, "ger", "ja", null)]
        [DataRow(SubtitleMode.Off, "eng", "de", null)]
        [DataRow(SubtitleMode.ForcedOnly, "ger", "de", "s4")]
        [DataRow(SubtitleMode.ForcedOnly, "eng", "en", null)]
        // Only while the audio is foreign: German audio needs none, English audio gets the German ones, unknown audio counts as understood.
        [DataRow(SubtitleMode.WhenForeign, "ger", "de", "s4")]
        [DataRow(SubtitleMode.WhenForeign, "eng", "de", "s5")]
        [DataRow(SubtitleMode.WhenForeign, null, "de", "s4")]
        [DataRow(SubtitleMode.WhenForeign, "eng", "en", null)]
        public void Subtitles_FollowTheMode_AndTheirLanguage(SubtitleMode mode, string? audioLanguage, string subtitleLanguage, string? expected)
        {
            var audio = new PlaybackChoiceTrack("s1", MediaTrackType.Audio, audioLanguage, true, false);
            var tracks = new PlaybackChoiceTrack[]
            {
                audio, new("s3", MediaTrackType.Subtitle, "eng", false, false), new("s4", MediaTrackType.Subtitle, "ger", false, true), new("s5", MediaTrackType.Subtitle, "ger", false, false)
            };
            var preference = new PlaybackChoiceData(null, subtitleLanguage, mode, "en");
            Assert.AreEqual(expected, PlaybackTracks.ChooseSubtitle(tracks, preference, audio)?.Key);
        }

        [TestMethod]
        public void Subtitles_WithoutALanguage_FollowTheProfilesLanguage()
        {
            var audio = new PlaybackChoiceTrack("s1", MediaTrackType.Audio, "jpn", true, false);
            var tracks = new PlaybackChoiceTrack[] { audio, new("s3", MediaTrackType.Subtitle, "eng", false, false), new("s4", MediaTrackType.Subtitle, "ger", false, false) };
            Assert.AreEqual("s4", PlaybackTracks.ChooseSubtitle(tracks, new PlaybackChoiceData(null, null, SubtitleMode.WhenForeign, "de"), audio)?.Key);
            Assert.AreEqual("s3", PlaybackTracks.ChooseSubtitle(tracks, new PlaybackChoiceData(null, "en", SubtitleMode.Always, "de"), audio)?.Key);
        }

        [TestMethod]
        public async Task TracksAndPlan_FollowThePreferences_AndTheExplicitChoiceWins()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateFilmHostAsync(storage, cache);
            var assetId = await CreateFilmAsync(host, storage.Path);

            // Without a choice: the file's default audio, and for English audio in a German profile the full German subtitles.
            var tracks = await ReadTracksAsync(host, assetId);
            CollectionAssert.AreEqual(new[] { "s1", "s5" }, Selected(tracks));
            var plan = await StartAsync(host, assetId, Browser);
            Assert.AreEqual((PlaybackModeType.DirectPlay, 1), (plan.PlaybackModeTypeId, plan.AudioStreamIndex));

            // German audio with full German subtitles: the audio plays through a conversion, the subtitle is picked for the player.
            using var german = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("de", "de", SubtitleMode.Always, 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, german.StatusCode, await german.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "s2", "s5" }, Selected(await ReadTracksAsync(host, assetId)));
            plan = await StartAsync(host, assetId, Hls);
            Assert.AreEqual((PlaybackModeType.Remux, 2), (plan.PlaybackModeTypeId, plan.AudioStreamIndex));

            // A browser without HLS cannot convert, so the default audio plays; an explicit choice is kept.
            plan = await StartAsync(host, assetId, Browser);
            Assert.AreEqual((PlaybackModeType.DirectPlay, 1), (plan.PlaybackModeTypeId, plan.AudioStreamIndex));
            plan = await StartAsync(host, assetId, new StartPlaybackDataV1(Hls.Containers, Hls.VideoCodecs, Hls.AudioCodecs, 1));
            Assert.AreEqual((PlaybackModeType.DirectPlay, 1), (plan.PlaybackModeTypeId, plan.AudioStreamIndex));

            // Only forced ones, a language the film lacks, and off.
            using var forced = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("de", "de", SubtitleMode.ForcedOnly, 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, forced.StatusCode);
            CollectionAssert.AreEqual(new[] { "s2", "s4" }, Selected(await ReadTracksAsync(host, assetId)));
            using var french = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("fr", "fr", SubtitleMode.Always, 1m, true));
            Assert.AreEqual(HttpStatusCode.OK, french.StatusCode);
            CollectionAssert.AreEqual(new[] { "s1" }, Selected(await ReadTracksAsync(host, assetId)));

            // English audio with German subtitles only while the audio is foreign.
            using var foreign = await host.SendAsync(HttpMethod.Put, Route, new SavePlaybackPreferencesDataV1("en", "de", SubtitleMode.WhenForeign, 1.5m, true));
            Assert.AreEqual(HttpStatusCode.OK, foreign.StatusCode);
            CollectionAssert.AreEqual(new[] { "s1", "s5" }, Selected(await ReadTracksAsync(host, assetId)));

            // The player starts with them: the chosen audio and subtitle pressed, the remembered speed and the device's quality.
            using var page = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            Assert.IsTrue(Regex.IsMatch(html, "data-audio=\"1\"[^>]*aria-pressed=\"true\""), html);
            Assert.IsTrue(Regex.IsMatch(html, "data-subtitle=\"s5\"[^>]*aria-pressed=\"true\""), html);
            StringAssert.Contains(html, "data-preferred-speed=\"1.5\"");
            StringAssert.Contains(html, "data-autoplay=\"true\"");
            Assert.IsTrue(Regex.IsMatch(html, "data-quality=\"1\"[^>]*aria-pressed=\"true\""), html);
            foreach (var quality in new[] { "2", "3", "4" })
            {
                Assert.IsTrue(Regex.IsMatch(html, "data-quality=\"" + quality + "\"[^>]*aria-pressed=\"false\""), html);
            }
        }

        [TestMethod]
        public async Task Plan_DeviceQualityCapsTheConversion_AndThePlaybacksOwnChoiceWins()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateFilmHostAsync(storage, cache);
            var assetId = await CreateFilmAsync(host, storage.Path);

            // A 4K film of 20 Mbit/s: Automatic and Original play it as it is.
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTracks\" SET \"Height\" = 2160 WHERE \"MediaTrackTypeId\" = 1; UPDATE \"StoredFiles\" SET \"BitRate\" = 20000000");
            var plan = await StartAsync(host, assetId, Hls);
            Assert.AreEqual((PlaybackModeType.DirectPlay, PlaybackQuality.Automatic), (plan.PlaybackModeTypeId, plan.Quality));

            // The device asks for High: the picture is converted down; a smaller picture within the rate stays as it is.
            using var sessions = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions");
            var session = (await sessions.Content.ReadFromJsonAsync<PageResult<SessionDetailsV1>>())!.Items.Single(item => item.IsCurrent).Id;
            using var high = await host.SendAsync(HttpMethod.Put, "/api/v1/sessions/" + session + "/playback-quality", new SessionPlaybackQualityDataV1(PlaybackQuality.High));
            Assert.AreEqual(HttpStatusCode.OK, high.StatusCode, await high.Content.ReadAsStringAsync());
            plan = await StartAsync(host, assetId, Hls);
            Assert.AreEqual((PlaybackModeType.Transcode, PlaybackQuality.High), (plan.PlaybackModeTypeId, plan.Quality));
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTracks\" SET \"Height\" = 1080 WHERE \"MediaTrackTypeId\" = 1; UPDATE \"StoredFiles\" SET \"BitRate\" = 6000000");
            Assert.AreEqual(PlaybackModeType.DirectPlay, (await StartAsync(host, assetId, Hls)).PlaybackModeTypeId);

            // Data saver caps lower still, and this playback's own choice beats the device's.
            using var saver = await host.SendAsync(HttpMethod.Put, "/api/v1/sessions/" + session + "/playback-quality", new SessionPlaybackQualityDataV1(PlaybackQuality.Saver));
            Assert.AreEqual(HttpStatusCode.OK, saver.StatusCode, await saver.Content.ReadAsStringAsync());
            plan = await StartAsync(host, assetId, Hls);
            Assert.AreEqual((PlaybackModeType.Transcode, PlaybackQuality.Saver), (plan.PlaybackModeTypeId, plan.Quality));
            plan = await StartAsync(host, assetId, new StartPlaybackDataV1(Hls.Containers, Hls.VideoCodecs, Hls.AudioCodecs, null, PlaybackQuality.Original));
            Assert.AreEqual((PlaybackModeType.DirectPlay, PlaybackQuality.Original), (plan.PlaybackModeTypeId, plan.Quality));
            Assert.AreEqual((short)4, await host.ScalarAsync("SELECT \"PlaybackQuality\" FROM \"AccountSessions\" WHERE \"PublicId\" = '" + session + "'"));

            // The player's menu offers all four.
            using var page = await host.SendAsync(HttpMethod.Get, "/Player/" + assetId);
            var html = await page.Content.ReadAsStringAsync();
            Assert.IsTrue(Regex.IsMatch(html, "data-quality=\"4\"[^>]*aria-pressed=\"true\""), html);
            Assert.AreEqual(4, Regex.Matches(html, "data-quality=\"").Count);

            // Without a converter nothing can be capped; the player says so instead of playing something too large.
            using var plain = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Browser);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, plain.StatusCode);
        }

        [TestMethod]
        public void Quality_Targets_CapAndALimitCanOnlyLowerThem()
        {
            Assert.AreEqual(PlaybackQualityTarget.Uncapped, PlaybackLogic.QualityTarget(PlaybackQuality.Automatic));
            Assert.AreEqual(PlaybackQualityTarget.Uncapped, PlaybackLogic.QualityTarget(PlaybackQuality.Original));
            Assert.AreEqual(new PlaybackQualityTarget(1080, 8000, 256), PlaybackLogic.QualityTarget(PlaybackQuality.High));
            Assert.AreEqual(new PlaybackQualityTarget(480, 1500, 128), PlaybackLogic.QualityTarget(PlaybackQuality.Saver));

            // A limit lowers what is capped and caps what is not, but never raises.
            Assert.AreEqual(new PlaybackQualityTarget(720, 4000, 256), PlaybackLogic.QualityTarget(PlaybackQuality.High, new PlaybackQualityTarget(720, 4000, null)));
            Assert.AreEqual(new PlaybackQualityTarget(480, 1500, 128), PlaybackLogic.QualityTarget(PlaybackQuality.Saver, new PlaybackQualityTarget(1080, 8000, null)));
            Assert.AreEqual(new PlaybackQualityTarget(720, null, null), PlaybackLogic.QualityTarget(PlaybackQuality.Original, new PlaybackQualityTarget(720, null, null)));
        }

        [TestMethod]
        [DataRow(2160, 20_000_000, PlaybackQuality.Original, false)]
        [DataRow(2160, 20_000_000, PlaybackQuality.High, true)]
        [DataRow(1080, 6_000_000, PlaybackQuality.High, false)]
        [DataRow(1080, 12_000_000, PlaybackQuality.High, true)]
        [DataRow(1080, 6_000_000, PlaybackQuality.Saver, true)]
        [DataRow(480, 1_200_000, PlaybackQuality.Saver, false)]
        public void Plan_ExceedingTheTarget_IsConvertedWithTheCap(int height, int rate, PlaybackQuality quality, bool converted)
        {
            var tracks = new[] { new PlaybackTrackData(0, MediaTrackType.Video, "h264", true, null, height), new PlaybackTrackData(1, MediaTrackType.Audio, "aac", true) };
            var target = PlaybackLogic.QualityTarget(quality);
            long bitRate = rate;
            var containers = Hls.Containers.ToHashSet();
            var videoCodecs = Hls.VideoCodecs.ToHashSet();
            var audioCodecs = Hls.AudioCodecs.ToHashSet();
            Assert.AreEqual(!converted, PlaybackLogic.CanPlayDirectly("mp4", tracks, containers, videoCodecs, audioCodecs, null, target, bitRate));
            var conversion = PlaybackLogic.PlanConversion(tracks, containers, videoCodecs, audioCodecs, null, null, target, bitRate)!;
            Assert.AreEqual(!converted, conversion.CopyVideo);
            Assert.AreEqual(converted ? target.MaxHeight : null, conversion.MaxHeight);
            Assert.AreEqual(converted ? target.VideoKilobits : null, conversion.VideoKilobits);
        }

        [TestMethod]
        public void Transcoder_Cap_ScalesDownAndLimitsTheRateForSoftwareEncoding()
        {
            var software = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, true, ["-c:v", "libx264"], []) { MaxHeight = 720, VideoKilobits = 4000 }).ToList();
            Assert.AreEqual("scale=-2:min(ih\\,720)", software[software.IndexOf("-vf") + 1]);
            Assert.AreEqual("4000k", software[software.IndexOf("-maxrate") + 1]);
            Assert.AreEqual("8000k", software[software.IndexOf("-bufsize") + 1]);

            // A hardware pipeline brings its own scaling; the rate still holds. Copying video takes no cap at all, and no cap adds nothing.
            var hardware = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, true, ["-c:v", "h264_vaapi"], ["-hwaccel", "vaapi"]) { MaxHeight = 720, VideoKilobits = 4000 }).ToList();
            CollectionAssert.DoesNotContain(hardware, "-vf");
            CollectionAssert.Contains(hardware, "-maxrate");
            var copy = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, true, true, [], []) { MaxHeight = 720, VideoKilobits = 4000 }).ToList();
            CollectionAssert.DoesNotContain(copy, "-maxrate");
            var uncapped = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, true, ["-c:v", "libx264"], [])).ToList();
            CollectionAssert.DoesNotContain(uncapped, "-vf");
            CollectionAssert.DoesNotContain(uncapped, "-maxrate");
        }

        private static async Task<Host> CreateFilmHostAsync(LibraryTests.TemporaryDirectory storage, LibraryTests.TemporaryDirectory cache) =>
            await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: new Dictionary<string, string>
            {
                ["Media:FfmpegPath"] = Path.Combine(cache.Path, "missing-ffmpeg"),
                ["Media:TranscodeCachePath"] = cache.Path
            });

        // One film with a 1080p picture, English audio (the default) and German commentary, and English and German subtitles (one German one forced).
        private static async Task<long> CreateFilmAsync(Host host, string storagePath)
        {
            var (assetId, _) = await PlaybackTests.CreatePlayableAsync(host, storagePath, wakeOnDemand: false);
            await host.Fixture.RunSqlAsync("""
                UPDATE "StoredFiles" SET "ProbedAt" = now(), "DurationMs" = 13500, "BitRate" = 5000000;
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Height", "Channels", "IsDefault", "IsForced")
                SELECT file."Id", track."StreamIndex", track."MediaTrackTypeId", track."Codec", track."Language", track."Height", track."Channels", track."IsDefault", track."IsForced"
                FROM "StoredFiles" AS file CROSS JOIN (VALUES
                    (0, 1, 'h264', NULL, 1080, NULL, TRUE, FALSE),
                    (1, 2, 'aac', 'eng', NULL, 2, TRUE, FALSE),
                    (2, 2, 'aac', 'ger', NULL, 2, FALSE, FALSE),
                    (3, 3, 'subrip', 'eng', NULL, NULL, FALSE, FALSE),
                    (4, 3, 'subrip', 'ger', NULL, NULL, FALSE, TRUE),
                    (5, 3, 'subrip', 'ger', NULL, NULL, FALSE, FALSE)
                ) AS track ("StreamIndex", "MediaTrackTypeId", "Codec", "Language", "Height", "Channels", "IsDefault", "IsForced");
                """);
            return assetId;
        }

        private static async Task<PlaybackPreferencesV1> ReadAsync(Host host)
        {
            using var read = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            return (await read.Content.ReadFromJsonAsync<PlaybackPreferencesV1>())!;
        }

        private static async Task<BaseDataList<PlaybackTrackV1>> ReadTracksAsync(Host host, long assetId)
        {
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/assets/" + assetId + "/tracks");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            return (await read.Content.ReadFromJsonAsync<BaseDataList<PlaybackTrackV1>>())!;
        }

        // The keys of the chosen tracks, audio before subtitles.
        private static string[] Selected(BaseDataList<PlaybackTrackV1> tracks) => [.. tracks.Where(track => track.IsSelected).Select(track => track.Key)];

        private static async Task<PlaybackPlanV1> StartAsync(Host host, long assetId, StartPlaybackDataV1 data)
        {
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", data);
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            return (await started.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
        }
    }
}
