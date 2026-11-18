using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Live.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Logic.Live;
using Jularr.Backend.Data.User.Live.V1;
using Jularr.Backend.Logic.Live;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Modules.Live;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LiveTests
    {
        private const string Release = "Secret.Release.Name.2026.1080p.WEB-DL-GRP";
        private const string OtherRelease = "Other.Release.Name.2026.720p.WEB-DL-GRP";

        private static LiveDownloadFacts Download(OperationStatusType status = OperationStatusType.Running, DownloadStateType? state = DownloadStateType.Downloading, bool waiting = false, int? percent = null,
            long? size = null, long? remaining = null, int? eta = null) => new(status, state, waiting, percent, size, remaining, eta);

        [TestMethod]
        public void Combine_RunningDownloads_CountBytesPaceAndUnpacking()
        {
            Assert.IsNull(LivePhases.Combine([]));
            Assert.IsNull(LivePhases.Combine([Download(OperationStatusType.Completed, size: 10, remaining: 0), Download(OperationStatusType.Failed)]), "Finished downloads no longer run.");

            // Bytes decide when every client reports them: 400 of 1000 and 100 of 500 are 500 of 1500; each pace is what is left over its time.
            var combined = LivePhases.Combine([Download(size: 1000, remaining: 600, eta: 60, percent: 99), Download(size: 500, remaining: 400, eta: 40)])!;
            Assert.AreEqual(33, combined.ProgressPercent);
            Assert.AreEqual(2, combined.Count);
            Assert.AreEqual(1500L, combined.SizeBytes);
            Assert.AreEqual(1000L, combined.RemainingBytes);
            Assert.AreEqual(60, combined.EtaSeconds);
            Assert.AreEqual(20L, combined.BytesPerSecond);
            Assert.IsFalse(combined.IsImporting);

            // Without sizes the clients' own percent is averaged; no pace without a time left.
            var reported = LivePhases.Combine([Download(percent: 20), Download(percent: 60)])!;
            Assert.AreEqual(40, reported.ProgressPercent);
            Assert.IsNull(reported.BytesPerSecond);
            Assert.IsNull(reported.EtaSeconds);
            Assert.IsNull(LivePhases.Combine([Download()])!.ProgressPercent);

            // Importing only once every running download is unpacking; waiting and paused say so.
            Assert.IsFalse(LivePhases.Combine([Download(state: DownloadStateType.PostProcessing), Download()])!.IsImporting);
            Assert.IsTrue(LivePhases.Combine([Download(state: DownloadStateType.PostProcessing), Download(state: DownloadStateType.PostProcessing)])!.IsImporting);
            Assert.IsTrue(LivePhases.Combine([Download(state: DownloadStateType.Extracting), Download(state: DownloadStateType.Completed)])!.IsImporting);
            Assert.IsTrue(LivePhases.Combine([Download(waiting: true), Download()])!.IsWaiting);
            Assert.IsTrue(LivePhases.Combine([Download(state: DownloadStateType.Paused)])!.IsPaused);
        }

        // A running download of the given client state, or none.
        [TestMethod]
        [DataRow(AcquisitionRequestStatusType.Open, null, AcquisitionRequestStatusType.Open, null)]
        [DataRow(AcquisitionRequestStatusType.Approved, null, AcquisitionRequestStatusType.Approved, null)]
        [DataRow(AcquisitionRequestStatusType.Searching, DownloadStateType.Downloading, AcquisitionRequestStatusType.Searching, null)]
        [DataRow(AcquisitionRequestStatusType.Downloading, null, AcquisitionRequestStatusType.Downloading, null)]
        [DataRow(AcquisitionRequestStatusType.Downloading, DownloadStateType.Downloading, AcquisitionRequestStatusType.Downloading, 50)]
        [DataRow(AcquisitionRequestStatusType.Downloading, DownloadStateType.PostProcessing, AcquisitionRequestStatusType.Importing, null)]
        [DataRow(AcquisitionRequestStatusType.Completed, DownloadStateType.PostProcessing, AcquisitionRequestStatusType.Completed, null)]
        [DataRow(AcquisitionRequestStatusType.Failed, null, AcquisitionRequestStatusType.Failed, null)]
        public void OfRequest_StatusAndDownload_GiveThePhaseAndPercent(AcquisitionRequestStatusType status, DownloadStateType? state, AcquisitionRequestStatusType expected, int? percent)
        {
            var downloads = state is { } client ? LivePhases.Combine([Download(state: client, size: 100, remaining: 50, eta: 10)]) : null;
            var phase = LivePhases.OfRequest(status, downloads);

            Assert.AreEqual(expected, phase.Phase);
            Assert.AreEqual(percent, phase.ProgressPercent, "Only a download has a percent.");
            Assert.AreEqual(percent is null ? null : 10, phase.EtaSeconds);
        }

        [TestMethod]
        public async Task LiveChanges_StreamsAreCapped_AndOnlyChangesWakeThem()
        {
            var changes = new LiveChanges();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.IsFalse(changes.WantsDownloadProgress);
            var waiting = changes.WaitForSubscribersAsync(cancellation.Token);
            Assert.IsFalse(waiting.IsCompleted, "Nobody is watching.");

            // One account holds a few streams; another is not held back by it.
            var leases = Enumerable.Range(0, LiveChanges.MaximumPerAccount).Select(_ => changes.TryJoin("1")).ToArray();
            Assert.IsTrue(leases.All(lease => lease is not null));
            await waiting;
            Assert.IsNull(changes.TryJoin("1"));
            using var other = changes.TryJoin("2");
            Assert.IsNotNull(other);
            Assert.AreEqual(LiveChanges.MaximumPerAccount + 1, changes.Subscribers);
            leases[0]!.Dispose();
            leases[0]!.Dispose();
            Assert.AreEqual(LiveChanges.MaximumPerAccount, changes.Subscribers, "A stream is counted out once.");
            using var again = changes.TryJoin("1");
            Assert.IsNotNull(again);

            // Only a different version is a change; a quiet moment returns what was seen.
            var seen = changes.Sequence;
            changes.Publish("a", true);
            Assert.IsTrue(changes.WantsDownloadProgress);
            Assert.AreEqual(seen + 1, await changes.WaitForChangeAsync(seen, TimeSpan.FromSeconds(1), cancellation.Token));
            changes.Publish("a", true);
            Assert.AreEqual(seen + 1, changes.Sequence);
            Assert.AreEqual(seen + 1, await changes.WaitForChangeAsync(seen + 1, TimeSpan.FromMilliseconds(50), cancellation.Token));
            var next = changes.WaitForChangeAsync(seen + 1, TimeSpan.FromSeconds(5), cancellation.Token);
            changes.Publish("b", false);
            Assert.AreEqual(seen + 2, await next);
            Assert.IsFalse(changes.WantsDownloadProgress);

            Assert.AreEqual(LiveChanges.IdleInterval, LiveChanges.IntervalFor(false, false));
            Assert.AreEqual(LiveChanges.JobInterval, LiveChanges.IntervalFor(false, true));
            Assert.AreEqual(LiveChanges.DownloadInterval, LiveChanges.IntervalFor(true, true));
        }

        [TestMethod]
        public async Task LiveServices_PersonSeesOwnRequests_AdministratorSeesEverything()
        {
            await using var host = await CreateHostAsync();
            await SeedDownloadsAsync(host);
            await SignInAsync(host);

            // A person: only the profile's own request, its phase and progress, nothing of release, indexer, client or error.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var mine = await host.SendAsync(HttpMethod.Get, "/api/v1/live");
            var text = await mine.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, mine.StatusCode, text);
            var own = JsonSerializer.Deserialize<BaseDataList<LiveRequestV1>>(text, JsonSerializerOptions.Web)!;
            var request = own.Single();
            Assert.AreEqual(500, request.Id);
            Assert.AreEqual(AcquisitionRequestStatusType.Downloading, request.AcquisitionRequestStatusTypeId);
            Assert.AreEqual(40, request.ProgressPercent);
            Assert.AreEqual(60, request.EtaSeconds);
            CollectionAssert.AreEquivalent(new[] { "id", "workId", "acquisitionRequestStatusTypeId", "progressPercent", "etaSeconds" },
                JsonDocument.Parse(text).RootElement[0].EnumerateObject().Select(property => property.Name).ToArray());
            foreach (var secret in new[] { Release, OtherRelease, "Indexer One", "Client One", "indexerName", "releaseTitle", "clientName", "reasonKey" })
            {
                Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), "The person's answer mentions " + secret);
            }

            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/live");
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

            // Unpacking is importing, and a percent no longer shows.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DownloadStateTypeId\" = 4 WHERE \"Id\" = 800");
            using var importing = await host.SendAsync(HttpMethod.Get, "/api/v1/live");
            var phase = (await importing.Content.ReadFromJsonAsync<BaseDataList<LiveRequestV1>>())!.Single();
            Assert.AreEqual(AcquisitionRequestStatusType.Importing, phase.AcquisitionRequestStatusTypeId);
            Assert.IsNull(phase.ProgressPercent);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DownloadStateTypeId\" = 2 WHERE \"Id\" = 800");

            // An administrator sees every profile's request, the queue and the targets with release, indexer, client and speed.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 1 WHERE \"Id\" = 41; UPDATE \"Operations\" SET \"ErrorKey\" = NULL, \"AttentionKey\" = 'download_storage_unavailable' WHERE \"Id\" = 801");
            using var everything = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/live");
            var all = (await everything.Content.ReadFromJsonAsync<LiveAdminV1>())!;
            CollectionAssert.AreEquivalent(new long[] { 500, 501 }, all.Requests.Select(item => item.Id).ToArray());
            var first = all.Requests.Single(item => item.Id == 500);
            Assert.AreEqual(Release, first.ReleaseTitle);
            Assert.AreEqual("Indexer One", first.IndexerName);
            Assert.AreEqual("Client One", first.ClientName);
            Assert.AreEqual(10L, first.BytesPerSecond);
            Assert.AreEqual("download_storage_unavailable", all.Requests.Single(item => item.Id == 501).ReasonKey);
            CollectionAssert.AreEquivalent(new long[] { 800, 801 }, all.Operations.Select(item => item.Id).ToArray());
            Assert.IsTrue(all.Operations.Single(item => item.Id == 801).IsWaiting);

            // A running download that does not move says why through its hint, without being a waiting one.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"HintKey\" = 'torrent_no_peers' WHERE \"Id\" = 800");
            using var hinted = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/live");
            var hint = (await hinted.Content.ReadFromJsonAsync<LiveAdminV1>())!.Operations.Single(item => item.Id == 800);
            Assert.AreEqual("torrent_no_peers", hint.ReasonKey);
            Assert.IsFalse(hint.IsWaiting);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"HintKey\" = NULL WHERE \"Id\" = 800");
            var target = all.Wanted.Single(item => item.Id == 600);
            Assert.AreEqual(10, target.WorkId);
            Assert.AreEqual(40, target.ProgressPercent);
            Assert.AreEqual(1000L, target.SizeBytes);

            // The administrator's own button still follows the administrator's own request only.
            using var ownAgain = await host.SendAsync(HttpMethod.Get, "/api/v1/live");
            Assert.AreEqual(500, (await ownAgain.Content.ReadFromJsonAsync<BaseDataList<LiveRequestV1>>())!.Single().Id);
        }

        [TestMethod]
        public async Task LiveStream_SnapshotFirst_ThenHeartbeatAndChanges_ScopedByRole()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true, services: services => services.AddHostedService<LiveWorker>(), endpoints: endpoints => endpoints.MapJularrLive());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, '<Work>'), (20, 3, 'Book');
                """);
            await SeedDownloadsAsync(host);
            host.Services.GetRequiredService<LiveChanges>().HeartbeatInterval = TimeSpan.FromMilliseconds(300);

            // Without a session there is no stream.
            using var anonymous = await host.OpenStreamAsync("/live/events");
            Assert.AreNotEqual("text/event-stream", anonymous.Content.Headers.ContentType?.MediaType);
            await SignInAsync(host);

            // The first event is the whole state: the profile's own request and, for an administrator, the queue.
            using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = await host.OpenStreamAsync("/live/events", ending.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("no", response.Headers.GetValues("X-Accel-Buffering").Single());
            StringAssert.Contains(response.Headers.CacheControl!.ToString(), "no-store");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ending.Token));
            var (name, entities) = await NextEventAsync(reader, ending.Token);
            Assert.AreEqual("snapshot", name);
            Assert.AreEqual("Downloading 40 %", entities.GetProperty("request:500").GetProperty("text").GetProperty("phase").GetString());
            Assert.AreEqual("Downloading 40 %", entities.GetProperty("request-work:10").GetProperty("text").GetProperty("phase").GetString());
            Assert.AreEqual(Release, entities.GetProperty("admin-request:500").GetProperty("text").GetProperty("release").GetString());
            Assert.AreEqual("Indexer One · Client One", entities.GetProperty("wanted:600").GetProperty("text").GetProperty("source").GetString());
            Assert.AreEqual(40, entities.GetProperty("operation:800").GetProperty("percent").GetInt32());
            StringAssert.Contains(entities.GetProperty("operation:800").GetProperty("text").GetProperty("detail").GetString()!, "40 %");
            Assert.AreEqual("2", entities.GetProperty("queue").GetProperty("text").GetProperty("active").GetString());
            Assert.AreEqual("0", entities.GetProperty("queue").GetProperty("text").GetProperty("errors").GetString());

            // The administration menu's figures: two downloads open, no task that needs help yet.
            Assert.AreEqual("2", entities.GetProperty("menu").GetProperty("text").GetProperty("downloads").GetString());
            Assert.IsFalse(entities.GetProperty("menu").GetProperty("text").TryGetProperty("jobs", out _));

            // A quiet stream says it is alive.
            await ReadUntilAsync(reader, line => line == ": heartbeat", ending.Token);

            // A client's new numbers reach the open stream as the next event.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"ProgressPercent\" = 70, \"RemainingBytes\" = 300, \"EtaSeconds\" = 30 WHERE \"Id\" = 800");
            while (true)
            {
                var (next, changed) = await NextEventAsync(reader, ending.Token);
                Assert.AreEqual("update", next);
                if (changed.GetProperty("request:500").GetProperty("percent").GetInt32() == 70)
                {
                    Assert.AreEqual("Downloading 70 %", changed.GetProperty("request-work:10").GetProperty("text").GetProperty("phase").GetString());
                    Assert.AreEqual("70 % · < 1 min left", changed.GetProperty("request:500").GetProperty("text").GetProperty("facts").GetString());
                    break;
                }
            }

            // A download the client holds paused says so in the place of its pace, so the queue's column keeps a value.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DownloadStateTypeId\" = 3 WHERE \"Id\" = 800");
            while (true)
            {
                var (next, changed) = await NextEventAsync(reader, ending.Token);
                Assert.AreEqual("update", next);
                if (changed.TryGetProperty("operation:800", out var paused) && paused.GetProperty("text").TryGetProperty("speed", out var speed))
                {
                    Assert.AreEqual("Paused", speed.GetString());
                    break;
                }
            }

            // A failed job (its state and the notice the runner writes with it) reaches the open stream as the menu's next figure, the tasks that need help.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WorkerJobs" ("Key", "LastResultTypeId", "LastErrorKey", "LastFinishedAt") VALUES ('downloads', 2, 'job_failed', now());
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "WorkerJobKey", "ErrorKey", "FinishedAt") VALUES (4, 4, 'downloads', 'job_failed', now());
                """);
            while (true)
            {
                var (next, changed) = await NextEventAsync(reader, ending.Token);
                Assert.AreEqual("update", next);
                if (changed.GetProperty("menu").GetProperty("text").TryGetProperty("jobs", out var failed))
                {
                    Assert.AreEqual("1", failed.GetString());
                    break;
                }
            }

            // A person's stream carries their own request and nothing of the queue, release, indexer or client.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var personResponse = await host.OpenStreamAsync("/live/events", ending.Token);
            using var personReader = new StreamReader(await personResponse.Content.ReadAsStreamAsync(ending.Token));
            var (personName, personEntities, raw) = await NextEventWithDataAsync(personReader, ending.Token);
            Assert.AreEqual("snapshot", personName);
            CollectionAssert.AreEquivalent(new[] { "request:500", "request-work:10" }, personEntities.EnumerateObject().Select(property => property.Name).ToArray());
            foreach (var secret in new[] { Release, OtherRelease, "Indexer One", "Client One", "release", "source", "error" })
            {
                Assert.IsFalse(raw.Contains(secret, StringComparison.Ordinal), "The person's stream mentions " + secret);
            }
        }

        // A change the database does not show (a search begins, a search ends) wakes the open streams, and costs nothing while nobody watches.
        [TestMethod]
        public async Task ActiveSearches_BeginAndEnd_WakeTheStreams_AndAreListedOnce()
        {
            var changes = new LiveChanges();
            var searches = new ActiveSearches(changes);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var before = changes.Sequence;
            searches.Begin(1, null, null).Dispose();
            Assert.AreEqual(before, changes.Sequence, "Nobody watches, so nothing is signalled.");

            using var watching = changes.TryJoin("1");
            var seen = changes.Sequence;
            var woken = changes.WaitForChangeAsync(seen, TimeSpan.FromSeconds(5), cancellation.Token);
            var first = searches.Begin(7, 1, 2);
            Assert.AreEqual(seen + 1, await woken);
            using var second = searches.Begin(7, 1, 2);
            using var other = searches.Begin(7, 1, null);
            CollectionAssert.AreEquivalent(new[] { new ActiveSearch(7, 1, 2), new ActiveSearch(7, 1, null) }, searches.Snapshot().ToArray(), "The same search twice is one.");

            first.Dispose();
            first.Dispose();
            Assert.AreEqual(2, searches.Snapshot().Count, "One of the two equal searches still runs.");
            second.Dispose();
            other.Dispose();
            Assert.AreEqual(0, searches.Snapshot().Count);
        }

        // The units of a season are named while they are searched or downloaded, each with its own phase and percent, and the season sums them up; a search without a download
        // shows on its target. Nothing is named once it is over, which is how a page knows to read itself again.
        [TestMethod]
        public async Task LiveStream_UnitsOfASeason_ShowTheirSearchAndDownload_AndTheSeasonSumsThemUp()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true, services: services => services.AddHostedService<LiveWorker>(), endpoints: endpoints => endpoints.MapJularrLive());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (30, 2, 'Show'), (31, 2, 'Quiet');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (3100, 30, 1, 1);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                SELECT 3000 + number, 30, 3100, 2, number, current_date - 30 FROM generate_series(1, 4) AS number;
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (630, 30), (631, 31);
                INSERT INTO "AcquisitionConnections" ("Id", "Name", "AcquisitionConnectionTypeId", "BaseUrl", "ProtectedApiKey")
                VALUES (900, 'Indexer One', 1, 'https://indexer.example.test/', NULL), (901, 'Client One', 2, 'https://client.example.test/', decode('00', 'hex'));
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "AcquisitionConnectionId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId", "ParentUnitNumber", "UnitStart", "UnitEnd")
                VALUES (730, 630, 900, 'three', 'Show.S01E02-E03', 1, 1, 2, 3);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "AcquisitionConnectionId", "ProgressPercent", "SizeBytes", "RemainingBytes", "EtaSeconds", "DownloadStateTypeId", "StartedAt")
                VALUES (830, 2, 2, 730, 901, 40, 1000, 600, 60, 2, now());
                """);
            await SignInAsync(host);
            using var ending = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = await host.OpenStreamAsync("/live/events", ending.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ending.Token));

            // The two episodes the release brings download, the season says so; the others are not named.
            var (name, entities) = await NextEventAsync(reader, ending.Token);
            Assert.AreEqual("snapshot", name);
            foreach (var id in new[] { "unit:3002", "unit:3003" })
            {
                Assert.AreEqual("downloading", entities.GetProperty(id).GetProperty("phase").GetString());
                Assert.AreEqual(40, entities.GetProperty(id).GetProperty("percent").GetInt32());
                Assert.AreEqual("40 %", entities.GetProperty(id).GetProperty("text").GetProperty("phase").GetString());
                Assert.AreEqual("Downloading 40 %", entities.GetProperty(id).GetProperty("text").GetProperty("title").GetString());
            }

            Assert.IsFalse(entities.TryGetProperty("unit:3001", out _));
            Assert.IsFalse(entities.TryGetProperty("unit:3004", out _));
            Assert.AreEqual("2 of 4 loading", entities.GetProperty("season:3100").GetProperty("text").GetProperty("phase").GetString());
            Assert.AreEqual("2 of 4 loading", entities.GetProperty("title:30").GetProperty("text").GetProperty("phase").GetString());

            // A search of the whole season marks what it would take: the episodes with nothing, not the two that load.
            var searches = host.Services.GetRequiredService<ActiveSearches>();
            var season = searches.Begin(630, 1, null);
            while (true)
            {
                var (_, changed) = await NextEventAsync(reader, ending.Token);
                if (!changed.TryGetProperty("unit:3001", out var searching)) continue;
                Assert.AreEqual("searching", searching.GetProperty("phase").GetString());
                Assert.AreEqual("Searching", searching.GetProperty("text").GetProperty("phase").GetString());
                Assert.IsTrue(changed.TryGetProperty("unit:3004", out _));
                Assert.AreEqual("downloading", changed.GetProperty("unit:3002").GetProperty("phase").GetString(), "A download wins over a search.");
                break;
            }

            // It ends, and the units are not named any more.
            season.Dispose();
            while (true)
            {
                var (_, changed) = await NextEventAsync(reader, ending.Token);
                if (changed.TryGetProperty("unit:3001", out _)) continue;
                Assert.IsFalse(changed.TryGetProperty("unit:3004", out _));
                break;
            }

            // One unit searched on its own is named even though it has a file or no download; unpacking is importing without a percent.
            using var single = searches.Begin(630, 1, 4);
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DownloadStateTypeId\" = 4 WHERE \"Id\" = 830");
            while (true)
            {
                var (_, changed) = await NextEventAsync(reader, ending.Token);
                if (!changed.TryGetProperty("unit:3004", out _) || changed.GetProperty("unit:3002").GetProperty("phase").GetString() != "importing") continue;
                Assert.IsFalse(changed.GetProperty("unit:3002").TryGetProperty("percent", out _));
                Assert.AreEqual("2 of 4 importing", changed.GetProperty("season:3100").GetProperty("text").GetProperty("phase").GetString());
                Assert.AreEqual("2 of 4 importing", changed.GetProperty("title:30").GetProperty("text").GetProperty("phase").GetString());
                break;
            }

            // A target that only searches says so on its own entity.
            using var quiet = searches.Begin(631, null, null);
            while (true)
            {
                var (_, changed) = await NextEventAsync(reader, ending.Token);
                if (!changed.TryGetProperty("wanted:631", out var target)) continue;
                Assert.AreEqual("searching", target.GetProperty("phase").GetString());
                break;
            }
        }

        // The profile 101 of account 41 and the profile 202 of account 42 each wait for a download that runs.
        private static async Task SeedDownloadsAsync(Host host)
        {
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionConnections" ("Id", "Name", "AcquisitionConnectionTypeId", "BaseUrl", "ProtectedApiKey")
                VALUES (900, 'Indexer One', 1, 'https://indexer.example.test/', NULL), (901, 'Client One', 2, 'https://client.example.test/', decode('00', 'hex'));
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, 10, 101, 41, 1, 4, 41, now()), (501, 20, 202, 42, 1, 4, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (600, 10), (601, 20);
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (500, 600), (501, 601);
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "AcquisitionConnectionId", "ReleaseIdentity", "Title", "AcquisitionReleaseStatusTypeId")
                VALUES (700, 600, 900, 'one', '{Release}', 1), (701, 601, 900, 'two', '{OtherRelease}', 1);
                INSERT INTO "Operations" ("Id", "OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "AcquisitionConnectionId", "ProgressPercent", "SizeBytes", "RemainingBytes", "EtaSeconds", "DownloadStateTypeId", "StartedAt")
                VALUES (800, 2, 2, 700, 901, 40, 1000, 600, 60, 2, now()), (801, 2, 2, 701, 901, 10, 2000, 1800, 300, 2, now());
                """);
        }

        private static async Task<Host> CreateHostAsync()
        {
            var host = await Host.CreateAsync(media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, '<Work>'), (20, 3, 'Book');
                """);
            return host;
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new Jularr.Backend.Data.Public.Authentication.V1.LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        }

        private static async Task<(string Name, JsonElement Entities)> NextEventAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var (name, entities, _) = await NextEventWithDataAsync(reader, cancellationToken);
            return (name, entities);
        }

        // One event: its name and the entities of its data, with the data as sent.
        private static async Task<(string Name, JsonElement Entities, string Data)> NextEventWithDataAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var name = (await ReadUntilAsync(reader, line => line.StartsWith("event: ", StringComparison.Ordinal), cancellationToken))["event: ".Length..];
            var data = (await ReadUntilAsync(reader, line => line.StartsWith("data: ", StringComparison.Ordinal), cancellationToken))["data: ".Length..];
            return (name, JsonDocument.Parse(data).RootElement.GetProperty("entities").Clone(), data);
        }

        private static async Task<string> ReadUntilAsync(StreamReader reader, Func<string, bool> match, CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The stream ended.");
                if (match(line))
                {
                    return line;
                }
            }
        }
    }
}
