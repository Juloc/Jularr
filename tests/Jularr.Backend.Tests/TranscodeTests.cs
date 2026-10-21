using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Transcoding.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Transcoding.V1;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Integrations.Data.Media;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TranscodeTests
    {
        private static readonly string[] Containers = ["hls", "mp4", "webm"];
        private static readonly string[] VideoCodecs = ["h264", "vp9"];
        private static readonly string[] AudioCodecs = ["aac", "opus"];
        private static readonly StartPlaybackDataV1 Automatic = new(Containers, VideoCodecs, AudioCodecs);
        private static readonly StartPlaybackDataV1 Saver = new(Containers, VideoCodecs, AudioCodecs, null, PlaybackQuality.Saver);

        private const string FakeEncoders = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
            """;

        [TestMethod]
        [DataRow(null, 4)]
        [DataRow(360, 1)]
        [DataRow(480, 1)]
        [DataRow(720, 2)]
        [DataRow(1080, 4)]
        [DataRow(1440, 8)]
        [DataRow(2160, 16)]
        public void Weight_PictureHeight_TakesItsShareOfTheBudget(int? height, int expected) => Assert.AreEqual(expected, TranscodeAdmissionLogic.Weight(height));

        [TestMethod]
        public void Decide_FreeBudget_AdmitsTheFirstCandidateThatFits()
        {
            TranscodeLoad[] running = [new(Guid.NewGuid(), 4, false, 1), new(Guid.NewGuid(), 2, false, 2)];

            // A 1080p and a 720p conversion take 6 of 12 quarters; the wanted 1080p fits.
            Assert.AreEqual(0, TranscodeAdmissionLogic.Decide(running, 12, false, [4, 2, 1]).CandidateIndex);

            // Less room: the 720p fits, then the 480p, then nothing.
            Assert.AreEqual(1, TranscodeAdmissionLogic.Decide(running, 8, false, [4, 2, 1]).CandidateIndex);
            Assert.AreEqual(2, TranscodeAdmissionLogic.Decide(running, 7, false, [4, 2, 1]).CandidateIndex);
            Assert.IsNull(TranscodeAdmissionLogic.Decide(running, 6, false, [4, 2, 1]).CandidateIndex);

            // Copying the video takes no room, even when the budget is overdrawn.
            Assert.AreEqual(0, TranscodeAdmissionLogic.Decide(running, 2, false, [0]).CandidateIndex);
        }

        [TestMethod]
        public void Decide_PriorityAccount_StopsTheNewestConversionsOfAccountsWithoutPriorityOnly()
        {
            var oldest = new TranscodeLoad(Guid.NewGuid(), 4, false, 1);
            var privileged = new TranscodeLoad(Guid.NewGuid(), 4, true, 2);
            var newest = new TranscodeLoad(Guid.NewGuid(), 4, false, 3);
            TranscodeLoad[] running = [oldest, privileged, newest];

            // The newest normal conversion goes, never the priority one; a normal account stops nobody.
            var one = TranscodeAdmissionLogic.Decide(running, 12, true, [4]);
            Assert.AreEqual(0, one.CandidateIndex);
            CollectionAssert.AreEqual(new[] { newest.SessionId }, one.Evicted.ToArray());
            Assert.IsNull(TranscodeAdmissionLogic.Decide(running, 12, false, [4]).CandidateIndex);

            // A larger conversion takes the next newest too, but not beyond what the normal accounts hold.
            var two = TranscodeAdmissionLogic.Decide(running, 12, true, [8]);
            CollectionAssert.AreEqual(new[] { newest.SessionId, oldest.SessionId }, two.Evicted.ToArray());
            var impossible = TranscodeAdmissionLogic.Decide(running, 12, true, [12, 1]);
            Assert.AreEqual(1, impossible.CandidateIndex);
            CollectionAssert.AreEqual(new[] { newest.SessionId }, impossible.Evicted.ToArray());

            // Priority accounts never stop each other.
            TranscodeLoad[] held = [new(Guid.NewGuid(), 4, true, 1), new(Guid.NewGuid(), 4, true, 2)];
            var none = TranscodeAdmissionLogic.Decide(held, 8, true, [4, 2]);
            Assert.IsNull(none.CandidateIndex);
            Assert.AreEqual(0, none.Evicted.Count);
        }

        [TestMethod]
        public void Candidates_LadderBelowThePicture_OffersLighterConversionsHighestFirst()
        {
            var tracks = new[] { new PlaybackTrackData(0, MediaTrackType.Video, "hevc", true, null, 2160), new PlaybackTrackData(1, MediaTrackType.Audio, "aac", true) };
            var containers = Containers.ToHashSet();
            var wanted = PlaybackLogic.PlanConversion(tracks, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet())!;

            // 4K takes 16, then the ladder's 1080p, 720p and 480p; a height above the picture is not offered.
            var candidates = TranscodeLogic.Candidates(wanted, tracks, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet(), null, PlaybackQuality.Automatic, null, [480, 720, 1080, 2160]);
            CollectionAssert.AreEqual(new[] { 16, 4, 2, 1 }, candidates.Select(candidate => candidate.Weight).ToArray());
            CollectionAssert.AreEqual(new bool[] { false, true, true, true }, candidates.Select(candidate => candidate.IsReduced).ToArray());
            CollectionAssert.AreEqual(new int?[] { null, 1080, 720, 480 }, candidates.Select(candidate => candidate.Plan.MaxHeight).ToArray());
            Assert.AreEqual(4000, candidates[2].Plan.VideoKilobits);

            // A device's own cap lets steps that are no lighter drop out; no ladder offers nothing.
            var saver = TranscodeLogic.Candidates(wanted, tracks, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet(), null, PlaybackQuality.Saver, null, [1080, 720, 480]);
            Assert.AreEqual(1, saver.Count(candidate => candidate.Weight > 0 && candidate.Plan.MaxHeight == 480));
            Assert.AreEqual(1, TranscodeLogic.Candidates(wanted, tracks, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet(), null, PlaybackQuality.Automatic, null, []).Count);

            // Copied video takes nothing and has nothing lighter to offer.
            var copy = new[] { new PlaybackTrackData(0, MediaTrackType.Video, "h264", true, null, 1080), new PlaybackTrackData(1, MediaTrackType.Audio, "aac", true) };
            var remux = PlaybackLogic.PlanConversion(copy, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet())!;
            var single = TranscodeLogic.Candidates(remux, copy, containers, VideoCodecs.ToHashSet(), AudioCodecs.ToHashSet(), null, PlaybackQuality.Automatic, null, [720]);
            Assert.AreEqual(0, single.Single().Weight);
        }

        [TestMethod]
        public void Propose_Benchmark_PicksTheFastestEncoderAndAConservativeCount()
        {
            // A GPU four times faster than software wins; its count follows the headroom.
            var gpu = TranscodeLogic.Propose([Speed(TranscodePipeline.Software, 1080, 1.4m), Speed(TranscodePipeline.Vaapi, 1080, 6m), Speed(TranscodePipeline.Nvenc, 1080, null)], 8)!;
            Assert.AreEqual((TranscodeAcceleration.Vaapi, 4), (gpu.Acceleration, gpu.MaxTranscodes));

            // Software that is nearly as fast stays software, limited to half the cores.
            var software = TranscodeLogic.Propose([Speed(TranscodePipeline.Software, 1080, 12m), Speed(TranscodePipeline.Vaapi, 1080, 12.5m)], 8)!;
            Assert.AreEqual((TranscodeAcceleration.Software, 4), (software.Acceleration, software.MaxTranscodes));

            // Slow machines still get one; hardware never proposes more than eight.
            Assert.AreEqual(1, TranscodeLogic.Propose([Speed(TranscodePipeline.Software, 1080, 0.4m)], 4)!.MaxTranscodes);
            Assert.AreEqual(8, TranscodeLogic.Propose([Speed(TranscodePipeline.Nvenc, 1080, 90m), Speed(TranscodePipeline.Software, 1080, 2m)], 32)!.MaxTranscodes);

            // Nothing ran: nothing to propose.
            Assert.IsNull(TranscodeLogic.Propose([Speed(TranscodePipeline.Software, 1080, null), Speed(TranscodePipeline.Software, 2160, 3m)], 8));
        }

        [TestMethod]
        public void ResolveEncoding_ChoiceBenchmarkAndConfiguration_PickTheArguments()
        {
            var vaapi = new[] { new TranscodeHardwareData(TranscodePipeline.Vaapi, "h264_vaapi", "/dev/dri/renderD129", true) };

            // Automatic follows the benchmark, else the configuration, else software; a fixed choice wins.
            Assert.AreEqual("/dev/dri/renderD129", TranscodeLogic.ResolveEncoding(TranscodeAcceleration.Auto, TranscodeAcceleration.Vaapi, vaapi, [], []).HardwareArguments[3]);
            CollectionAssert.AreEqual(new[] { "-c:v", "h264_x" }, TranscodeLogic.ResolveEncoding(TranscodeAcceleration.Auto, null, vaapi, ["-hwaccel", "x"], ["-c:v", "h264_x"]).VideoArguments.ToArray());
            Assert.AreSame(TranscodePipelines.Software, TranscodeLogic.ResolveEncoding(TranscodeAcceleration.Auto, null, vaapi, ["-hwaccel", "x"], []));
            Assert.AreSame(TranscodePipelines.Software, TranscodeLogic.ResolveEncoding(TranscodeAcceleration.Software, TranscodeAcceleration.Vaapi, vaapi, [], []));
            CollectionAssert.Contains(TranscodeLogic.ResolveEncoding(TranscodeAcceleration.Nvenc, null, vaapi, [], []).VideoArguments.ToArray(), "h264_nvenc");
        }

        [TestMethod]
        public void Transcoder_HardwarePipeline_ScalesWithItsOwnFilter()
        {
            var vaapi = TranscodePipelines.For(TranscodePipeline.Vaapi, "/dev/dri/renderD128");
            var arguments = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, false, vaapi.VideoArguments, vaapi.HardwareArguments)
            {
                MaxHeight = 720,
                ScaleFilter = vaapi.ScaleFilter
            }).ToList();
            Assert.AreEqual("scale_vaapi=w=-2:h=min(ih\\,720):format=nv12", arguments[arguments.IndexOf("-vf") + 1]);

            // Software scales the way it always did; arguments from the configuration leave the size to the encoder.
            var software = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, false, [], []) { MaxHeight = 480 }).ToList();
            Assert.AreEqual("scale=-2:min(ih\\,480)", software[software.IndexOf("-vf") + 1]);
            var configured = MediaTranscoder.BuildArguments(new TranscodeRequest("/media/film.mkv", "/cache/s", 0, 6, 0, 1, false, false, ["-c:v", "h264_vaapi"], ["-hwaccel", "vaapi"]) { MaxHeight = 480 }).ToList();
            CollectionAssert.DoesNotContain(configured, "-vf");
        }

        [TestMethod]
        public async Task Benchmark_FakeFfmpeg_FindsHardwareAndTimesEveryPipeline()
        {
            var calls = new List<string>();
            var client = FakeClient(calls);

            var hardware = await client.DetectAsync(CancellationToken.None);
            string[] available = [.. hardware.Where(item => item.IsAvailable).Select(item => item.Pipeline.ToString())];
            CollectionAssert.AreEquivalent(new[] { "Software", "Vaapi", "Nvenc" }, available);
            Assert.AreEqual("/dev/dri/renderD128", hardware.Single(item => item.Pipeline == TranscodePipeline.Vaapi).Device);
            Assert.IsFalse(hardware.Single(item => item.Pipeline == TranscodePipeline.Qsv).IsAvailable);

            var results = await client.MeasureAsync(hardware, CancellationToken.None);
            Assert.AreEqual(6m, results.Single(item => item.Pipeline == TranscodePipeline.Vaapi && item.Height == 1080).SpeedFactor);
            Assert.AreEqual(3m, results.Single(item => item.Pipeline == TranscodePipeline.Vaapi && item.Height == 2160).SpeedFactor);
            Assert.AreEqual(0.3m, results.Single(item => item.Pipeline == TranscodePipeline.Software && item.Height == 2160).SpeedFactor);
            Assert.IsNull(results.Single(item => item.Pipeline == TranscodePipeline.Nvenc && item.Height == 1080).SpeedFactor);

            // The 4K clip is scaled down on the device the way playback does it.
            var scaled = calls.Single(call => call.Contains("h264_vaapi", StringComparison.Ordinal) && call.Contains("clip2160", StringComparison.Ordinal));
            StringAssert.Contains(scaled, "scale_vaapi=w=-2:h=min(ih\\,1080)");
            StringAssert.Contains(scaled, "-hwaccel vaapi");
        }

        [TestMethod]
        public async Task Registry_Admission_StopsTheNewestNormalConversionAndTellsItsPlayer()
        {
            using var cache = new LibraryTests.TemporaryDirectory();
            using var transcodes = new PlaybackTranscodes(new MediaTranscoder(), Microsoft.Extensions.Options.Options.Create(new MediaOptions { TranscodeCachePath = cache.Path, MaxTranscodes = 2 }));
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(), priority = Guid.NewGuid(), another = Guid.NewGuid(), remux = Guid.NewGuid();
            PlaybackTranscodeSource Source(Guid id, int weight, bool hasPriority) => new(id, 7, new StorageInput("film.mkv", null), 0, 1, weight == 0, true, 60_000) { Weight = weight, HasPriority = hasPriority };

            // Two 1080p conversions fill a maximum of two; copying and a third are told apart.
            Assert.AreEqual(0, transcodes.Admit([4], false, 2, _ => Source(first, 4, false)));
            Assert.AreEqual(0, transcodes.Admit([4], false, 2, _ => Source(second, 4, false)));
            Assert.AreEqual(0, transcodes.Admit([0], false, 2, _ => Source(remux, 0, false)));
            var busy = Assert.ThrowsExactly<ServiceUnavailableException>(() => transcodes.Admit([4, 2], false, 2, _ => Source(Guid.NewGuid(), 4, false)));
            Assert.AreEqual("playback_capacity_exhausted", busy.Code);

            // An account with priority stops the newest conversion of one without it; that player is told on its next request.
            Assert.AreEqual(0, transcodes.Admit([4], true, 2, _ => Source(priority, 4, true)));
            Assert.IsNotNull(transcodes.Find(first, 7));
            Assert.AreEqual("playback_evicted", Assert.ThrowsExactly<ServiceGoneException>(() => transcodes.Find(second, 7)).Code);
            Assert.AreEqual("playback_evicted", (await Assert.ThrowsExactlyAsync<ServiceGoneException>(() => transcodes.GetSegmentAsync(second, 7, 0, CancellationToken.None))).Code);

            // The next priority account stops the one conversion still without priority, and nobody after that.
            Assert.AreEqual(0, transcodes.Admit([4], true, 2, _ => Source(another, 4, true)));
            Assert.AreEqual("playback_evicted", Assert.ThrowsExactly<ServiceGoneException>(() => transcodes.Find(first, 7)).Code);
            Assert.ThrowsExactly<ServiceUnavailableException>(() => transcodes.Admit([4], true, 2, _ => Source(Guid.NewGuid(), 4, true)));
            Assert.IsNotNull(transcodes.Find(priority, 7));
            Assert.IsNotNull(transcodes.Find(remux, 7));

            // A smaller maximum counts at once for conversions that restart.
            transcodes.MaxTranscodes = 1;
            Assert.AreEqual(1, transcodes.MaxTranscodes);
        }

        [TestMethod]
        public async Task Settings_Owner_ReadsEditsAndKeepsTheBounds()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, settings: new Dictionary<string, string> { ["Media:MaxTranscodes"] = "3" });
            const string route = "/api/v1/admin/transcoding/settings";

            // The configuration is the default until the Owner saves a value.
            var settings = await ReadSettingsAsync(host);
            Assert.AreEqual((TranscodeAcceleration.Auto, 3), (settings.TranscodeAccelerationTypeId, settings.MaxTranscodes));
            CollectionAssert.AreEquivalent(new[] { 1080, 720, 480 }, settings.LadderHeights);

            using var saved = await host.SendAsync(HttpMethod.Put, route, new UpdateTranscodeSettingsDataV1(TranscodeAcceleration.Software, 5, [720, 360, 720], settings.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var changed = await ReadSettingsAsync(host);
            Assert.AreEqual((TranscodeAcceleration.Software, 5), (changed.TranscodeAccelerationTypeId, changed.MaxTranscodes));
            CollectionAssert.AreEqual(new[] { 360, 720 }, changed.LadderHeights);

            // An old version, an unknown height and a count out of range change nothing.
            using var stale = await host.SendAsync(HttpMethod.Put, route, new UpdateTranscodeSettingsDataV1(TranscodeAcceleration.Auto, 2, [], settings.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            using var unknown = await host.SendAsync(HttpMethod.Put, route, new UpdateTranscodeSettingsDataV1(TranscodeAcceleration.Auto, 2, [999], changed.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
            using var range = await host.SendAsync(HttpMethod.Put, route, new UpdateTranscodeSettingsDataV1(TranscodeAcceleration.Auto, 0, [], changed.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, range.StatusCode);
            Assert.AreEqual(changed.Version, (await ReadSettingsAsync(host)).Version);

            // An empty ladder is allowed: no lower picture is offered.
            using var empty = await host.SendAsync(HttpMethod.Put, route, new UpdateTranscodeSettingsDataV1(TranscodeAcceleration.Vaapi, 2, [], changed.Version));
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, await empty.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await ReadSettingsAsync(host)).LadderHeights.Length);

            // Only the Owner reads or changes it: the services ask for the instance settings grants.
            foreach (var role in new[] { AccountRoleType.User, AccountRoleType.MediaManager })
            {
                var permissions = ServiceGate.GetPermissions(41, 1, 41, role);
                CollectionAssert.DoesNotContain(permissions.ToArray(), "instance.settings.read");
                CollectionAssert.DoesNotContain(permissions.ToArray(), "instance.settings.update");
            }

            CollectionAssert.Contains(ServiceGate.GetPermissions(41, 1, 41, AccountRoleType.Owner).ToArray(), "instance.settings.update");
        }

        [TestMethod]
        public async Task Page_Transcoding_ShowsHardwareRunsTheBenchmarkAndSavesTheSettings()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var client = FakeClient([]);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton(client));

            // The machine's encoders and the editable settings; the sidebar links the page next to System.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Transcoding");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "h264_vaapi");
            StringAssert.Contains(html, "name=\"TranscodeAccelerationTypeId\"");
            StringAssert.Contains(html, "name=\"MaxTranscodes\"");
            StringAssert.Contains(html, "name=\"LadderHeights\"");
            StringAssert.Contains(html, "href=\"/Admin/Transcoding\"");
            StringAssert.Contains(html, "?handler=Benchmark");

            // The benchmark is queued from the page and shows as running.
            using var queued = await host.SendAsync(HttpMethod.Post, "/Admin/Transcoding?handler=Benchmark", new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html) });
            Assert.AreEqual(HttpStatusCode.Redirect, queued.StatusCode, await queued.Content.ReadAsStringAsync());
            using var running = await host.SendAsync(HttpMethod.Get, "/Admin/Transcoding");
            var shown = await running.Content.ReadAsStringAsync();
            StringAssert.Contains(shown, "data-transcoding-running");
            Assert.IsFalse(shown.Contains("?handler=Benchmark", StringComparison.Ordinal));

            // The settings save through their form.
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"TranscodeSettings\""))!;
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Transcoding", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(shown),
                ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["transcodeAccelerationTypeId"] = ((byte)TranscodeAcceleration.Software).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxTranscodes"] = "3",
                ["ladderHeights"] = "720"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"TranscodeAccelerationTypeId\" FROM \"TranscodeSettings\""));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"MaxTranscodes\" FROM \"TranscodeSettings\""));
            CollectionAssert.AreEqual(new[] { 720 }, (int[])(await host.ScalarAsync("SELECT \"LadderHeights\" FROM \"TranscodeSettings\""))!);
        }

        [TestMethod]
        public async Task Priority_Account_DefaultsOnForTheOwnerAndIsEditedOnTheAccount()
        {
            // The first Owner is created through setup, the way an instance starts.
            await using var host = await Host.CreateAsync();
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/setup/owner", new CreateOwnerDataV1("owner@example.test", "Owner", "owner-password-123", "owner-password-123", 1));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var accountId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1"))!;

            using var owner = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/" + accountId);
            var details = (await owner.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            Assert.IsTrue(details.TranscodingPriority);

            using var off = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/" + accountId, new UpdateAccountDataV1(details.DisplayName, details.AccountRoleTypeId, true, null, false));
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"TranscodingPriority\" FROM \"Accounts\" WHERE \"Id\" = " + accountId));

            // Leaving it out keeps the choice.
            using var kept = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/" + accountId, new UpdateAccountDataV1(details.DisplayName, details.AccountRoleTypeId, true));
            Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode, await kept.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"TranscodingPriority\" FROM \"Accounts\" WHERE \"Id\" = " + accountId));
        }

        [TestMethod]
        public async Task Benchmark_RunOnDemand_StoresTheResultAndItsProposal()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var client = FakeClient([]);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddSingleton(client));
            const string route = "/api/v1/admin/transcoding/benchmark";

            // Nothing runs by itself.
            var before = await ReadBenchmarkAsync(host);
            Assert.IsNull(before.RanAt);
            Assert.IsFalse(before.IsRunning);
            Assert.IsTrue(before.Hardware.Any(item => item.TranscodeAccelerationTypeId == TranscodeAcceleration.Vaapi && item.IsAvailable));

            // One queued run at a time, visible as running.
            using var queued = await host.SendAsync(HttpMethod.Post, route);
            Assert.IsTrue((await queued.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            using var again = await host.SendAsync(HttpMethod.Post, route);
            Assert.IsFalse((await again.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            Assert.IsTrue((await ReadBenchmarkAsync(host)).IsRunning);

            // The worker claims it like a scan.
            await using var scope = host.Services.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new OperationQueueParametersV1(), default, CancellationToken.None);
            Assert.AreEqual(OperationType.TranscodeBenchmark, claimed.OperationTypeId);
            await scope.ServiceProvider.GetRequiredService<ExecuteTranscodeBenchmarkV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), default, CancellationToken.None);

            var after = await ReadBenchmarkAsync(host);
            Assert.IsFalse(after.IsRunning);
            Assert.IsNull(after.ErrorKey);
            Assert.IsNotNull(after.RanAt);
            Assert.IsTrue(after.CpuCores > 0);
            Assert.AreEqual(TranscodeAcceleration.Vaapi, after.BestTranscodeAccelerationTypeId);
            Assert.AreEqual(4, after.RecommendedMaxTranscodes);
            Assert.AreEqual(6, after.Results.Count);
            Assert.AreEqual(6m, after.Results.Single(item => item.TranscodeAccelerationTypeId == TranscodeAcceleration.Vaapi && item.Height == 1080).SpeedFactor);

            // A second run replaces the first one's rows.
            using var next = await host.SendAsync(HttpMethod.Post, route);
            Assert.IsTrue((await next.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new OperationQueueParametersV1(), default, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<ExecuteTranscodeBenchmarkV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), default, CancellationToken.None);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"TranscodeBenchmarks\""));
            Assert.AreEqual(6L, await host.ScalarAsync("SELECT count(*) FROM \"TranscodeBenchmarkResults\""));
        }

        [TestMethod]
        public async Task Benchmark_NoEncoderRuns_FailsWithoutStoringAProposal()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var client = new TranscodeBenchmarkClient(new MediaTranscoder { ExecutablePath = "ffmpeg" })
            {
                Runner = (arguments, _, _) => Task.FromResult(new FfmpegRun(arguments.Contains("-hwaccels") || arguments.Contains("-encoders") ? 0 : 1, arguments.Contains("-encoders") ? FakeEncoders : "", TimeSpan.Zero)),
                RenderNodes = () => [],
                DeviceExists = _ => false
            };
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, services: services => services.AddSingleton(client));
            using var queued = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/transcoding/benchmark");
            Assert.IsTrue((await queued.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            await using var scope = host.Services.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new OperationQueueParametersV1(), default, CancellationToken.None);
            var failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() =>
                scope.ServiceProvider.GetRequiredService<ExecuteTranscodeBenchmarkV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), default, CancellationToken.None));
            Assert.AreEqual("benchmark_failed", failure.Code);
            await scope.ServiceProvider.GetRequiredService<FailOperationV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), new FailOperationDataV1(failure.Code), CancellationToken.None);

            var state = await ReadBenchmarkAsync(host);
            Assert.IsNull(state.RanAt);
            Assert.AreEqual("benchmark_failed", state.ErrorKey);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"TranscodeBenchmarks\""));
        }

        [TestMethod]
        public async Task Start_ServerAtItsLimit_OffersLighterQualityElseReportsBusyAndNeverCountsCopying()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateConversionHostAsync(storage, cache);
            var assetId = await CreateHevcFilmAsync(host, storage.Path);
            await host.Fixture.RunSqlAsync("UPDATE \"TranscodeSettings\" SET \"MaxTranscodes\" = 1");

            // A saver stream takes a quarter of the one 1080p conversion the server allows.
            var first = await StartAsync(host, assetId, Saver);
            Assert.AreEqual((PlaybackModeType.Transcode, false), (first.PlaybackModeTypeId, first.IsReduced));

            // The wanted 1080p no longer fits; the ladder's 720p does, then 480p, then nothing.
            var second = await StartAsync(host, assetId, Automatic);
            Assert.AreEqual((PlaybackModeType.Transcode, true), (second.PlaybackModeTypeId, second.IsReduced));
            var third = await StartAsync(host, assetId, Automatic);
            Assert.IsTrue(third.IsReduced);
            using var busy = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Automatic);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            StringAssert.Contains(await busy.Content.ReadAsStringAsync(), "playback_capacity_exhausted");
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"PlaybackSessions\""));

            // Without the ladder the wanted picture alone decides.
            await host.Fixture.RunSqlAsync("UPDATE \"TranscodeSettings\" SET \"LadderHeights\" = '{}', \"MaxTranscodes\" = 2");
            Assert.AreEqual(PlaybackModeType.Transcode, (await StartAsync(host, assetId, Automatic)).PlaybackModeTypeId);
            using var full = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Automatic);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, full.StatusCode);

            // Copying the picture and direct play never count against the limit.
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTracks\" SET \"Codec\" = 'h264' WHERE \"MediaTrackTypeId\" = 1");
            Assert.AreEqual(PlaybackModeType.Remux, (await StartAsync(host, assetId, Automatic)).PlaybackModeTypeId);
            Assert.AreEqual(PlaybackModeType.DirectPlay, (await StartAsync(host, assetId, new StartPlaybackDataV1(["mkv"], ["h264"], ["aac"]))).PlaybackModeTypeId);
        }

        [TestMethod]
        public async Task Start_PriorityAccount_StopsTheNewestConversionAndTellsItsPlayer()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var cache = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateConversionHostAsync(storage, cache);
            var assetId = await CreateHevcFilmAsync(host, storage.Path);
            await host.Fixture.RunSqlAsync("UPDATE \"TranscodeSettings\" SET \"MaxTranscodes\" = 1; UPDATE \"Accounts\" SET \"TranscodingPriority\" = FALSE");

            // An account without priority holds the only conversion.
            var normal = await StartAsync(host, assetId, Automatic);
            using var playing = await GetAsync(host, normal.StreamUrl);
            Assert.AreEqual(HttpStatusCode.OK, playing.StatusCode);
            using var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", Automatic);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            // The same account with priority takes the slot at the wanted quality; the other player is told on its next request.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"TranscodingPriority\" = TRUE");
            var privileged = await StartAsync(host, assetId, Automatic);
            Assert.IsFalse(privileged.IsReduced);
            using var stopped = await GetAsync(host, normal.StreamUrl);
            Assert.AreEqual(HttpStatusCode.Gone, stopped.StatusCode);
            StringAssert.Contains(await stopped.Content.ReadAsStringAsync(), "playback_evicted");
            using var segment = await GetAsync(host, normal.StreamUrl.Replace("playlist.m3u8", "segments/0.ts", StringComparison.Ordinal));
            Assert.AreEqual(HttpStatusCode.Gone, segment.StatusCode);
            using var kept = await GetAsync(host, privileged.StreamUrl);
            Assert.AreEqual(HttpStatusCode.OK, kept.StatusCode);

            // Priority accounts never stop each other, so another conversion finds no room at any quality while the slot is held.
            foreach (var data in new[] { Automatic, Saver })
            {
                using var next = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", data);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, next.StatusCode);
            }

            // The stopped player ends its session as usual.
            using var ended = await host.SendAsync(HttpMethod.Put, "/api/v1/playback/sessions/" + normal.PlaybackSessionId, new UpdatePlaybackDataV1(0, null, true, false));
            Assert.AreEqual(HttpStatusCode.OK, ended.StatusCode);
        }

        private static async Task<Host> CreateConversionHostAsync(LibraryTests.TemporaryDirectory storage, LibraryTests.TemporaryDirectory cache) =>
            await LibraryTests.CreateHostAsync(storage.Path, settings: new Dictionary<string, string>
            {
                ["Media:FfmpegPath"] = Path.Combine(cache.Path, "missing-ffmpeg"),
                ["Media:TranscodeCachePath"] = cache.Path
            });

        // A 1080p HEVC film with AAC audio: browsers that play H.264 only need it converted.
        private static async Task<long> CreateHevcFilmAsync(Host host, string storagePath)
        {
            var (assetId, _) = await PlaybackTests.CreatePlayableAsync(host, storagePath, wakeOnDemand: false, fileName: "Film.mkv");
            await host.Fixture.RunSqlAsync("UPDATE \"StoredFiles\" SET \"ProbedAt\" = now(), \"DurationMs\" = 600000; "
                + "INSERT INTO \"MediaTracks\" (\"StoredFileId\", \"StreamIndex\", \"MediaTrackTypeId\", \"Codec\", \"Width\", \"Height\", \"IsDefault\", \"IsForced\") "
                + "SELECT \"Id\", 0, 1, 'hevc', 1920, 1080, TRUE, FALSE FROM \"StoredFiles\" UNION ALL SELECT \"Id\", 1, 2, 'aac', NULL, NULL, TRUE, FALSE FROM \"StoredFiles\"");
            return assetId;
        }

        private static async Task<PlaybackPlanV1> StartAsync(Host host, long assetId, StartPlaybackDataV1 data)
        {
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", data);
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            return (await started.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
        }

        // Media elements send cookies, not antiforgery headers.
        private static async Task<HttpResponseMessage> GetAsync(Host host, string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var client = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            return await client.SendAsync(request);
        }

        private static async Task<TranscodeSettingsV1> ReadSettingsAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/transcoding/settings");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<TranscodeSettingsV1>())!;
        }

        private static async Task<TranscodeBenchmarkV1> ReadBenchmarkAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/transcoding/benchmark");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<TranscodeBenchmarkV1>())!;
        }

        private static TranscodeSpeedData Speed(TranscodePipeline pipeline, int height, decimal? speed) => new(pipeline, height, speed);

        // FFmpeg that lists VAAPI, NVENC and software; VAAPI times at 6 and 3, software at 1 and 0.3, and NVENC does not run.
        private static TranscodeBenchmarkClient FakeClient(List<string> calls) => new(new MediaTranscoder { ExecutablePath = "ffmpeg" })
        {
            RenderNodes = () => ["/dev/dri/renderD128"],
            DeviceExists = _ => true,
            Runner = (arguments, _, _) =>
            {
                var line = string.Join(' ', arguments);
                calls.Add(line);
                if (arguments.Contains("-hwaccels")) return Task.FromResult(new FfmpegRun(0, "Hardware acceleration methods:\nvaapi\ncuda\n", TimeSpan.Zero));
                if (arguments.Contains("-encoders")) return Task.FromResult(new FfmpegRun(0, FakeEncoders, TimeSpan.Zero));
                if (arguments.Contains("lavfi")) return Task.FromResult(new FfmpegRun(0, "", TimeSpan.Zero));
                if (line.Contains("h264_nvenc", StringComparison.Ordinal)) return Task.FromResult(new FfmpegRun(1, "", TimeSpan.Zero));
                var vaapi = line.Contains("h264_vaapi", StringComparison.Ordinal);
                var large = line.Contains("clip2160", StringComparison.Ordinal);
                var speed = vaapi ? large ? "3.00" : "6.00" : large ? "0.30" : "1.00";
                return Task.FromResult(new FfmpegRun(0, "frame=100\nspeed=" + speed + "x\nprogress=end\n", TimeSpan.FromSeconds(4)));
            }
        };
    }
}
