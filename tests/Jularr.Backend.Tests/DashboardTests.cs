using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Instance.V1;
using Jularr.Integrations.Logic.MediaServer;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DashboardTests
    {
        private const string Health = "/api/v1/admin/system/health";
        private const string Connections = "/api/v1/admin/acquisition/connections";
        private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public async Task Health_OnlyConfiguredServicesAppear_AreJudgedByTheirOwnTests_AndAnswersAreKept()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var downloads = new LibraryTests.TemporaryDirectory();
            var servers = new FakeServers();
            await using var host = await CreateHostAsync(storage.Path, servers);

            // Nothing configured: the database is all there is, and nothing was asked yet.
            var stored = await ReadHealthAsync(host);
            Assert.AreEqual(HealthServiceType.Database, stored.Services.Single().HealthServiceTypeId);
            Assert.IsTrue(stored.IsStable);
            Assert.IsNull(stored.CheckedAt);

            // A storage connection, an indexer and a download client appear with what is stored about them; a media server needs its module.
            var mountId = await LibraryTests.CreateMountAsync(host);
            await SaveConnectionAsync(host, new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", FakeServers.Key, true, 10, [], null, null, null));
            await SaveConnectionAsync(host, new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", FakeServers.Key, true, 10, [], "movies", "/downloads", downloads.Path));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('ProgressSync', 'Library')");
            using var jellyfin = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/media-servers/Jellyfin", new SaveMediaServerDataV1("http://jellyfin.example.test:8096", FakeServers.Key));
            Assert.AreEqual(HttpStatusCode.OK, jellyfin.StatusCode, await jellyfin.Content.ReadAsStringAsync());
            stored = await ReadHealthAsync(host);
            CollectionAssert.AreEqual(new[] { HealthServiceType.Database, HealthServiceType.Storage, HealthServiceType.DownloadClient, HealthServiceType.Indexer },
                stored.Services.Select(service => service.HealthServiceTypeId).ToArray());
            Assert.AreEqual(HealthStateType.Unknown, stored.Services.Single(service => service.HealthServiceTypeId == HealthServiceType.Indexer).HealthStateTypeId);
            Assert.AreEqual(0, servers.SabRequests, "Reading the answer asks nobody.");

            // Check now asks the client, the indexer and the media server through their own tests.
            var first = await CheckNowAsync(host);
            Assert.IsTrue(first.IsStable, string.Join(", ", first.Services.Select(service => service.Key + ":" + service.HealthStateTypeId + ":" + service.ErrorKey)));
            Assert.IsNotNull(first.CheckedAt);
            CollectionAssert.AreEqual(new[] { HealthServiceType.Database, HealthServiceType.Storage, HealthServiceType.MediaServer, HealthServiceType.DownloadClient, HealthServiceType.Indexer },
                first.Services.Select(service => service.HealthServiceTypeId).ToArray());
            Assert.IsTrue(first.Services.Where(service => service.HealthServiceTypeId != HealthServiceType.Storage).All(service => service.HealthStateTypeId == HealthStateType.Available));
            Assert.AreEqual(1, servers.SabRequests);
            Assert.AreEqual(1, servers.IndexerRequests);
            Assert.IsNotNull(await host.ScalarAsync("SELECT \"CheckedAt\" FROM \"AcquisitionConnections\" WHERE \"Name\" = 'Indexer'"), "The test is kept with the connection.");

            // The answer is kept: reading it, and a check within the minute, ask nobody. The client is down, but only a new check finds out.
            servers.SabAvailable = false;
            Assert.AreEqual(HealthStateType.Available, (await ReadHealthAsync(host)).Services.Single(service => service.HealthServiceTypeId == HealthServiceType.DownloadClient).HealthStateTypeId);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var reused = await scope.ServiceProvider.GetRequiredService<RunHealthCheckV1>().ExecuteAsync<HealthV1>(new HealthCheckParametersV1(), default);
                Assert.AreEqual(first.CheckedAt, reused.CheckedAt);
            }

            Assert.AreEqual(1, servers.SabRequests);
            var second = await CheckNowAsync(host);
            Assert.IsFalse(second.IsStable);
            var client = second.Services.Single(service => service.HealthServiceTypeId == HealthServiceType.DownloadClient);
            Assert.AreEqual(HealthStateType.Unavailable, client.HealthStateTypeId);
            Assert.AreEqual("acquisition_unavailable", client.ErrorKey);
            Assert.AreEqual(2, servers.SabRequests);

            // An indexer's test counts for half an hour; the client is asked again every time.
            Assert.AreEqual(1, servers.IndexerRequests);
            servers.JellyfinAvailable = false;
            var third = await CheckNowAsync(host);
            Assert.AreEqual(HealthStateType.Unavailable, third.Services.Single(service => service.HealthServiceTypeId == HealthServiceType.MediaServer).HealthStateTypeId);
            Assert.AreEqual(1, servers.IndexerRequests);

            // A module that is off takes its rows with it.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('ProgressSync', 'Acquisition')");
            var off = await CheckNowAsync(host);
            CollectionAssert.AreEqual(new[] { HealthServiceType.Database, HealthServiceType.Storage }, off.Services.Select(service => service.HealthServiceTypeId).ToArray());
            Assert.IsTrue(off.IsStable);
            Assert.AreEqual(mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), off.Services.Single(service => service.HealthServiceTypeId == HealthServiceType.Storage).Key.Split(':')[1]);
        }

        [TestMethod]
        public async Task Health_StorageAndProviders_AreJudgedFromWhatIsStoredAndObserved()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var servers = new FakeServers();
            await using var host = await CreateHostAsync(storage.Path, servers);
            var mountId = await LibraryTests.CreateMountAsync(host);

            // An offline connection that cannot be woken is a failure; one that is asleep is not.
            await host.Fixture.RunSqlAsync($"UPDATE \"StorageMounts\" SET \"WakeMacAddress\" = NULL, \"WakeBroadcastAddress\" = NULL, \"WakeOnDemand\" = FALSE WHERE \"Id\" = {mountId}");
            await host.Fixture.RunSqlAsync($"INSERT INTO \"StorageMountStates\" (\"StorageMountId\", \"StorageAvailabilityTypeId\", \"DiagnosticCode\", \"CheckedAt\") VALUES ({mountId}, 2, 'probe_timeout', now()) ON CONFLICT (\"StorageMountId\") DO UPDATE SET \"StorageAvailabilityTypeId\" = 2, \"DiagnosticCode\" = 'probe_timeout', \"CheckedAt\" = now()");
            var offline = (await CheckNowAsync(host)).Services.Single(service => service.HealthServiceTypeId == HealthServiceType.Storage);
            Assert.AreEqual(HealthStateType.Unavailable, offline.HealthStateTypeId);
            Assert.AreEqual("probe_timeout", offline.ErrorKey);
            await host.Fixture.RunSqlAsync($"UPDATE \"StorageMounts\" SET \"WakeMacAddress\" = 'AA:BB:CC:DD:EE:FF' WHERE \"Id\" = {mountId}");
            Assert.AreEqual(HealthStateType.Available, (await CheckNowAsync(host)).Services.Single(service => service.HealthServiceTypeId == HealthServiceType.Storage).HealthStateTypeId);

            // A provider with a stored key shows before anything asked it; what its client last got is its state, and nothing is asked again.
            await host.Fixture.RunSqlAsync("UPDATE \"Providers\" SET \"ProtectedSecret\" = '\\x00'::bytea WHERE \"Key\" = 'tmdb'");
            Assert.AreEqual(HealthStateType.Unknown, (await CheckNowAsync(host)).Services.Single(service => service.HealthServiceTypeId == HealthServiceType.Provider).HealthStateTypeId);
            var observations = host.Services.GetRequiredService<ProviderObservations>();
            observations.Record("tmdb", true);
            observations.Record("anilist", false);
            var providers = (await CheckNowAsync(host)).Services.Where(service => service.HealthServiceTypeId == HealthServiceType.Provider).ToDictionary(service => service.Key);
            Assert.AreEqual(HealthStateType.Available, providers["provider:tmdb"].HealthStateTypeId);
            Assert.AreEqual(HealthStateType.Unavailable, providers["provider:anilist"].HealthStateTypeId);
            Assert.AreEqual("provider_unavailable", providers["provider:anilist"].ErrorKey);
            Assert.IsFalse(providers.ContainsKey("provider:igdb"), "A provider nobody configured or asked has no row.");
            await host.Fixture.RunSqlAsync("UPDATE \"Providers\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'anilist'");
            Assert.IsFalse((await CheckNowAsync(host)).Services.Any(service => service.Key == "provider:anilist"));
        }

        [TestMethod]
        public async Task Health_Permissions_AreTheOwnersAlone()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, new FakeServers());
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            foreach (var path in new[] { Health, "/api/v1/admin/system/dashboard", "/api/v1/admin/system/logs" })
            {
                using var read = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode, path);
            }

            using var check = await host.SendAsync(HttpMethod.Post, Health + "/check");
            Assert.AreEqual(HttpStatusCode.Forbidden, check.StatusCode);
        }

        [TestMethod]
        public async Task HealthJob_AFailingService_FailsThePassOnce_AndTheNoticeClosesOnceItAnswers()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var downloads = new LibraryTests.TemporaryDirectory();
            var servers = new FakeServers();
            var time = new FixedTime(Start);
            await using var host = await CreateHostAsync(storage.Path, servers, services => services.AddSingleton<TimeProvider>(time));
            await SaveConnectionAsync(host, new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", FakeServers.Key, true, 10, [], "movies", "/downloads", downloads.Path));
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            var worker = new HealthWorker(time, jobs, host.Services.GetRequiredService<WorkerJobRunner>());
            var pass = typeof(HealthWorker).GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task RunAsync() => await (Task)pass.Invoke(worker, [CancellationToken.None])!;

            // Everything answers: a quiet success and no notice.
            time.Now = Start.AddMinutes(5);
            await RunAsync();
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkerJobs\" WHERE \"Key\" = 'health' AND \"LastResultTypeId\" = 1"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'health'"));

            // The client stops answering: the pass fails, and one notice tells the Owner.
            servers.SabAvailable = false;
            time.Now = time.Now.AddMinutes(3);
            await RunAsync();
            Assert.AreEqual("health_degraded", await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'health'"));
            time.Now = time.Now.AddMinutes(3);
            await RunAsync();
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'health' AND \"DismissedAt\" IS NULL AND \"ErrorKey\" = 'health_degraded'"));

            // It answers again: the notice closes by itself.
            servers.SabAvailable = true;
            time.Now = time.Now.AddMinutes(3);
            await RunAsync();
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'health' AND \"DismissedAt\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'health'"));
        }

        [TestMethod]
        public async Task Pages_DashboardPerformanceAndLogs_RenderForTheOwner_AndCheckNowAsksAgain()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            using var downloads = new LibraryTests.TemporaryDirectory();
            var servers = new FakeServers();
            await using var host = await CreateHostAsync(storage.Path, servers);
            await SaveConnectionAsync(host, new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", FakeServers.Key, true, 10, [], "movies", "/downloads", downloads.Path));

            // The first look shows what is stored: a client nobody asked yet.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "dashboard-services__row");
            StringAssert.Contains(html, "SAB");
            StringAssert.Contains(html, "Not checked");
            Assert.AreEqual(0, servers.SabRequests);

            // Check now asks it and shows the answer.
            using var checkedNow = await host.SendAsync(HttpMethod.Post, "/Admin/Dashboard?handler=Check", new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html) });
            Assert.AreEqual(HttpStatusCode.Redirect, checkedNow.StatusCode, await checkedNow.Content.ReadAsStringAsync());
            Assert.AreEqual(1, servers.SabRequests);
            using var after = await host.SendAsync(HttpMethod.Get, checkedNow.Headers.Location!.OriginalString);
            var answered = await after.Content.ReadAsStringAsync();
            StringAssert.Contains(answered, "state-chip--success");
            StringAssert.Contains(answered, "Stable");

            servers.SabAvailable = false;
            using var failed = await host.SendAsync(HttpMethod.Post, "/Admin/Dashboard?handler=Check", new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(answered) });
            using var warning = await host.SendAsync(HttpMethod.Get, failed.Headers.Location!.OriginalString);
            var warned = await warning.Content.ReadAsStringAsync();
            StringAssert.Contains(warned, "state-chip--warning");
            StringAssert.Contains(warned, "Warning");

            // The load and the log are pages of their own; the Diagnostics module is their precondition.
            foreach (var path in new[] { "/Admin/Performance", "/Admin/Logs", "/Admin/Logs?level=Error&source=Job&q=x", "/Admin/System", "/Admin/Jobs" })
            {
                using var other = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.OK, other.StatusCode, path + await other.Content.ReadAsStringAsync());
            }

            using var jobs = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            StringAssert.Contains(await jobs.Content.ReadAsStringAsync(), "jobs-health");
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Diagnostics'");
            using var plain = await host.SendAsync(HttpMethod.Get, "/Admin/Performance");
            Assert.AreEqual(HttpStatusCode.Forbidden, plain.StatusCode);
            using var dashboard = await host.SendAsync(HttpMethod.Get, "/Admin/Dashboard");
            Assert.AreEqual(HttpStatusCode.OK, dashboard.StatusCode);
            Assert.IsFalse((await dashboard.Content.ReadAsStringAsync()).Contains("resource-chart", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Logs_ActivityJobsAndTheApplication_AreMergedFilteredAndPaged()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, new FakeServers());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ActivityEvents" ("ActivityEventTypeId", "OccurredAt", "WorkTitle", "ReleaseTitle") VALUES
                    (1, now() - interval '50 minutes', 'Long Film', 'Long.Film.2020.1080p'),
                    (2, now() - interval '40 minutes', 'Old Film', 'Old.Film.1999');
                INSERT INTO "WorkerJobs" ("Key") VALUES ('catalog');
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "WorkerJobKey", "ErrorKey", "StartedAt", "FinishedAt")
                    VALUES (4, 4, 'catalog', 'provider_unavailable', now() - interval '30 minutes', now() - interval '30 minutes');
                """);
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.Test").LogError(new InvalidOperationException("Disk said no."), "Import of {Title} failed.", "Film");
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jularr.Test").LogInformation("Routine work.");

            // Newest first: the application's own error, the failed job, the failed download and the grab.
            var all = await ReadLogsAsync(host, "");
            Assert.AreEqual(4, all.Items.Count(entry => entry.Origin is "Jularr.Test" or "catalog" or "DownloadFailed" or "Grabbed"));
            var ours = all.Items.Where(entry => entry.Origin is "Jularr.Test" or "catalog" or "DownloadFailed" or "Grabbed").ToList();
            CollectionAssert.AreEqual(new[] { LogSourceType.Application, LogSourceType.Job, LogSourceType.Activity, LogSourceType.Activity }, ours.Select(entry => entry.Source).ToArray());
            CollectionAssert.AreEqual(new[] { LogLevelType.Error, LogLevelType.Error, LogLevelType.Warning, LogLevelType.Info }, ours.Select(entry => entry.Level).ToArray());
            Assert.AreEqual("Import of Film failed. (InvalidOperationException: Disk said no.)", ours[0].Message);
            Assert.IsFalse(all.Items.Any(entry => entry.Message.Contains("Routine work.", StringComparison.Ordinal)));

            // Level, source and search each narrow the list.
            Assert.IsTrue((await ReadLogsAsync(host, "?level=Error")).Items.All(entry => entry.Level == LogLevelType.Error));
            Assert.AreEqual("catalog", (await ReadLogsAsync(host, "?source=Job")).Items.Single().Origin);
            Assert.AreEqual("Old Film · Old.Film.1999", (await ReadLogsAsync(host, "?search=old.film")).Items.Single().Message);
            Assert.AreEqual(0, (await ReadLogsAsync(host, "?level=Info&source=Application")).Items.Count);

            // A page ends where it is full, and the next one goes on.
            var firstPage = await ReadLogsAsync(host, "?pageSize=2");
            var nextPage = await ReadLogsAsync(host, "?pageSize=2&page=2");
            Assert.AreEqual(2, firstPage.Items.Count);
            Assert.IsTrue(nextPage.Items.Count > 0);
            Assert.IsTrue(firstPage.Items.Last().At >= nextPage.Items.First().At);

            // Without the Diagnostics module the application's messages are not collected, and the rest stays.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Diagnostics'");
            host.Services.GetRequiredService<RecentLogs>().Enable(false);
            var plain = await ReadLogsAsync(host, "");
            Assert.IsFalse(plain.Items.Any(entry => entry.Source == LogSourceType.Application));
            Assert.AreEqual(3, plain.Items.Count(entry => entry.Origin is "catalog" or "DownloadFailed" or "Grabbed"));
        }

        [TestMethod]
        public void RecentLogs_KeepOnlyTheNewestThousand_NewestFirst()
        {
            using var logs = new RecentLogs(TimeProvider.System);
            var logger = logs.CreateLogger("Jularr.Test");
            for (var index = 0; index < 1100; index++) logger.LogWarning("Warning {Index}", index);
            logger.LogInformation("Routine");

            var kept = logs.Read(int.MaxValue);
            Assert.AreEqual(1000, kept.Count);
            Assert.AreEqual("Warning 1099", kept[0].Message);
            Assert.AreEqual("Warning 100", kept[^1].Message);
            logs.Enable(false);
            Assert.AreEqual(0, logs.Read(int.MaxValue).Count);
        }

        [TestMethod]
        public async Task PerformanceHistory_IsWrittenInBatches_PrunedAndReadBackAfterARestart()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(storage.Path, new FakeServers());
            var timings = host.Services.GetRequiredService<ServiceTimings>();
            var history = host.Services.GetRequiredService<ResourceHistory>();
            var now = DateTime.UtcNow;
            timings.Record("Probe/Service", TimeSpan.FromMilliseconds(40), false);
            timings.Record("Probe/Service", TimeSpan.FromMilliseconds(10), true);
            history.Add(new ResourceSample(now.AddMinutes(-1), 12.5m, 300_000_000));
            history.Add(new ResourceSample(now, 20.0m, 310_000_000));
            await host.Fixture.RunSqlAsync("INSERT INTO \"PerformanceSamples\" (\"SampledAt\", \"ProcessorPercent\", \"MemoryBytes\") VALUES (now() - interval '30 hours', 1, 1)");

            // One write takes everything new, and what is older than a day leaves.
            await SaveHistoryAsync(host);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"PerformanceSamples\" WHERE \"MemoryBytes\" IN (300000000, 310000000)"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"PerformanceSamples\" WHERE \"MemoryBytes\" = 1"));
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT \"Calls\" FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT \"Errors\" FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));
            Assert.AreEqual(40L, await host.ScalarAsync("SELECT \"MaximumMilliseconds\" FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));

            // What was written is not written again; a later call changes the same row.
            Assert.IsFalse(timings.ReadUnsaved().Any(slice => slice.Service == "Probe/Service"));
            Assert.AreEqual(0, history.ReadUnsaved().Count(sample => sample.MemoryBytes is 300_000_000 or 310_000_000));
            timings.Record("Probe/Service", TimeSpan.FromMilliseconds(70), false);
            Assert.IsTrue(timings.ReadUnsaved().Any(slice => slice.Service == "Probe/Service"));
            await SaveHistoryAsync(host);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT \"Calls\" FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));

            // A restart starts empty and reads the day back once; the calls made since add up with it.
            timings.Enable(false);
            timings.Enable(true);
            timings.Record("Probe/Service", TimeSpan.FromMilliseconds(5), false);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<LoadPerformanceHistoryV1>().ExecuteAsync<UpdateResult>(new PerformanceHistoryParametersV1(), default);
            }

            var read = timings.Read(100).Single(timing => timing.Service == "Probe/Service");
            Assert.AreEqual(4L, read.Count);
            Assert.AreEqual(1L, read.Errors);
            Assert.AreEqual(70d, read.MaxMilliseconds);
            var restarted = new ResourceHistory(TimeProvider.System, host.Services.GetRequiredService<WorkerJobRunner>(), host.Services.GetRequiredService<RecentLogs>(), timings);
            restarted.Load([new ResourceSample(now.AddMinutes(-2), 1m, 5), new ResourceSample(now.AddMinutes(-1), 2m, 6)]);
            restarted.Add(new ResourceSample(now, 3m, 7));
            Assert.AreEqual(3, restarted.Read().Count);
            Assert.AreEqual(7L, restarted.ReadUnsaved().Single().MemoryBytes, "What was read back is in the database already.");
            var totals = timings.Totals();
            Assert.IsTrue(totals.Calls >= 4);
            await SaveHistoryAsync(host);
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT \"Calls\" FROM \"ServiceTimings\" WHERE \"Service\" = 'Probe/Service'"));
        }

        private static async Task<UpdateResult> SaveHistoryAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SavePerformanceHistoryV1>().ExecuteAsync<UpdateResult>(new PerformanceHistoryParametersV1(), default);
        }

        private static async Task<Host> CreateHostAsync(string storage, FakeServers servers, Action<IServiceCollection>? more = null) =>
            await LibraryTests.CreateHostAsync(storage, razor: true, services: services =>
            {
                services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => servers);
                services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => servers);
                services.AddHttpClient<MediaServerClient>().ConfigurePrimaryHttpMessageHandler(() => servers);
                more?.Invoke(services);
            });

        private static async Task SaveConnectionAsync(Host host, SaveAcquisitionConnectionDataV1 data)
        {
            using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        }

        private static async Task<HealthV1> ReadHealthAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Health);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<HealthV1>())!;
        }

        private static async Task<HealthV1> CheckNowAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Post, Health + "/check");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<HealthV1>())!;
        }

        private static async Task<PageResult<LogEntryV1>> ReadLogsAsync(Host host, string query)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/system/logs" + query);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<LogEntryV1>>())!;
        }

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        // SABnzbd, an indexer and a Jellyfin server that can be switched off, counting what they are asked.
        private sealed class FakeServers : HttpMessageHandler
        {
            public const string Key = "fake-key";
            private const string Capabilities = """
                <caps><server title="Fake" version="1.0" /><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /></searching>
                <categories><category id="2000" name="Movies" /></categories></caps>
                """;

            public bool SabAvailable { get; set; } = true;
            public bool JellyfinAvailable { get; set; } = true;
            public int SabRequests { get; private set; }
            public int IndexerRequests { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                switch (request.RequestUri!.Host)
                {
                    case "sab.example.test":
                        var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                        if (form["mode"] == "version") SabRequests++;
                        if (!SabAvailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        return form["mode"] == "version" ? Json("""{"version":"4.3.0"}""") : form["apikey"] == Key ? Json("""{"queue":{"slots":[]}}""") : Json("""{"status":false,"error":"API Key Incorrect"}""");
                    case "indexer.example.test":
                        IndexerRequests++;
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Capabilities, Encoding.UTF8, "application/xml") };
                    default:
                        if (!JellyfinAvailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        return request.RequestUri.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal) ? Json("""[{"Id":"u1","Name":"Alice"}]""")
                            : Json("""{"Id":"abc123","ServerName":"Fake Jellyfin","Version":"10.9.0"}""");
                }
            }

            private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
