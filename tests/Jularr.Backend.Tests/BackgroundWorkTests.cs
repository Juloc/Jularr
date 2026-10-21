using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Jularr.Backend.Service.System.Worker.V1;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Backend.Tests
{
    // What the follow-up of the Jobs page changed: the Owner's intervals, workers that wait for work instead of polling, and a backup that runs beside a scan.
    [TestClass]
    public sealed class BackgroundWorkTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void Registry_FollowsTheOwnersIntervals_ForSearchProviderSyncAndBackup()
        {
            var time = new FixedTime(Start);
            var jobs = new WorkerJobs(time, new UserActivity(time));
            jobs.Load([]);

            // The defaults are the intervals the workers always had.
            Assert.AreEqual(TimeSpan.FromMinutes(2), jobs.Interval("wantedSearch"));
            Assert.AreEqual(TimeSpan.FromMinutes(30), jobs.Interval("providerSync"));
            Assert.AreEqual(TimeSpan.FromHours(24), jobs.Interval("backup"));
            Assert.AreEqual(TimeSpan.FromMinutes(10), jobs.Interval("covers"), "A fixed interval stays.");

            // A job counts its next pass from the interval the Owner set.
            jobs.UseSettings(new WorkerSettingsData(12, 3, 45, 5));
            Assert.AreEqual(TimeSpan.FromMinutes(5), jobs.Interval("wantedSearch"));
            Assert.AreEqual(TimeSpan.FromMinutes(45), jobs.Interval("providerSync"));
            Assert.AreEqual(TimeSpan.FromHours(12), jobs.Interval("backup"));
            time.Now = Start.AddMinutes(1);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("wantedSearch"));
            jobs.Finish("wantedSearch", WorkerJobResultType.Succeeded, null);
            Assert.AreEqual(Start.AddMinutes(6).UtcDateTime, jobs.Describe("wantedSearch", null).NextRunAt);
            time.Now = Start.AddMinutes(5);
            Assert.AreEqual(WorkerJobGate.NotDue, jobs.Begin("wantedSearch"));

            // A new interval applies to a job that is already waiting; the backup job keeps looking every half hour.
            jobs.UseSettings(new WorkerSettingsData(12, 3, 45, 2));
            Assert.AreEqual(Start.AddMinutes(3).UtcDateTime, jobs.Describe("wantedSearch", null).NextRunAt);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("wantedSearch"));
            jobs.Finish("wantedSearch", WorkerJobResultType.Succeeded, null);
            time.Now = Start.AddMinutes(100);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("backup"));
            jobs.Finish("backup", WorkerJobResultType.Succeeded, null);
            Assert.AreEqual(Start.AddMinutes(130).UtcDateTime, jobs.Describe("backup", null).NextRunAt);
            Assert.AreEqual(TimeSpan.FromHours(12), jobs.Describe("backup", null).Interval);

            // Looking again sooner than the interval is for jobs that have more to do.
            time.Now = Start.AddMinutes(200);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("downloads"));
            jobs.Finish("downloads", WorkerJobResultType.Succeeded, null);
            Assert.AreEqual(Start.AddMinutes(201).UtcDateTime, jobs.Describe("downloads", null).NextRunAt);
            jobs.RunAgainWithin("downloads", TimeSpan.FromSeconds(15));
            Assert.AreEqual(Start.AddMinutes(200).AddSeconds(15).UtcDateTime, jobs.Describe("downloads", null).NextRunAt);
            jobs.RunAgainWithin("downloads", TimeSpan.FromMinutes(5));
            Assert.AreEqual(Start.AddMinutes(200).AddSeconds(15).UtcDateTime, jobs.Describe("downloads", null).NextRunAt, "It never looks later than before.");
        }

        [TestMethod]
        public async Task Settings_SavedByTheOwner_ReachTheRegistryTheJobsPageAndAFreshProcess()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/settings");
            var settings = (await read.Content.ReadFromJsonAsync<InstanceSettingsDetailsV1>())!;
            Assert.AreEqual((24, 7, 30, 2), (settings.BackupIntervalHours, settings.BackupKeep, settings.ProviderSyncIntervalMinutes, settings.WantedSearchIntervalMinutes));

            // Out-of-range values are refused.
            foreach (var invalid in new[] { new UpdateInstanceSettingsDataV1("Jularr", settings.UiLocaleId, 1, backupKeep: 0), new UpdateInstanceSettingsDataV1("Jularr", settings.UiLocaleId, 1, providerSyncIntervalMinutes: 4),
                new UpdateInstanceSettingsDataV1("Jularr", settings.UiLocaleId, 1, wantedSearchIntervalMinutes: 0), new UpdateInstanceSettingsDataV1("Jularr", settings.UiLocaleId, 1, backupIntervalHours: 721) })
            {
                using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", invalid);
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            // Saved settings are kept, shown on the Jobs rows and followed by the registry at once.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                new UpdateInstanceSettingsDataV1("Jularr", settings.UiLocaleId, 1, backupIntervalHours: 0, backupKeep: 3, providerSyncIntervalMinutes: 45, wantedSearchIntervalMinutes: 5));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"BackupKeep\" FROM \"InstanceSettings\""));
            Assert.AreEqual(TimeSpan.FromMinutes(45), jobs.Interval("providerSync"));
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/jobs");
            var rows = (await listed.Content.ReadFromJsonAsync<BaseDataList<WorkerJobV1>>())!;
            Assert.AreEqual(5 * 60, rows.Single(job => job.Key == "wantedSearch").IntervalSeconds);
            Assert.AreEqual(45 * 60, rows.Single(job => job.Key == "providerSync").IntervalSeconds);
            Assert.AreEqual(0, rows.Single(job => job.Key == "backup").IntervalSeconds, "0 hours shows as off.");
            CollectionAssert.AreEqual(new[] { "providerSyncPlex", "providerSyncJellyfin", "providerSyncEmby" },
                rows.Where(job => job.ParentKey == "providerSync").Select(job => job.Key).ToArray());
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "45 min");

            // A process that starts later reads them before its first pass.
            jobs.UseSettings(new WorkerSettingsData(24, 7, 30, 2));
            Assert.AreEqual(TimeSpan.FromMinutes(30), jobs.Interval("providerSync"));
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LoadWorkerJobsV1>().ExecuteAsync<UpdateResult>(new Jularr.Backend.Data.System.Worker.V1.LoadWorkerJobsParametersV1(), default);
            Assert.AreEqual(TimeSpan.FromMinutes(5), jobs.Interval("wantedSearch"));
            Assert.AreEqual(TimeSpan.FromMinutes(45), jobs.Interval("providerSync"));
        }

        [TestMethod]
        public async Task WorkSignal_WakesAWaitingWorkerAtOnce_OtherwiseTheSafetyTickDoes()
        {
            var signal = new WorkSignal();

            // Announced work wakes a worker that would sleep for an hour, and is taken.
            var watch = Stopwatch.StartNew();
            var waiting = signal.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, CancellationToken.None);
            Assert.IsFalse(waiting.IsCompleted);
            signal.Notify();
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.IsFalse(signal.TryTake(), "The announcement is taken by the wake.");

            // Work announced before the worker looks is not lost, and several announcements are one.
            signal.Notify();
            signal.Notify();
            await signal.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(signal.TryTake());

            // Without work the slow safety tick ends the wait; stopping ends it too.
            await signal.WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            using var stop = new CancellationTokenSource();
            var sleeping = signal.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, stop.Token);
            await stop.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => sleeping);
        }

        [TestMethod]
        public async Task OperationWorker_WakesForAQueuedScanWithoutWaitingForItsSafetyTick()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var mountId = await LibraryTests.CreateMountAsync(host);
            Directory.CreateDirectory(Path.Combine(storage.Path, mountId.ToString(), "Movies"));
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);
            using var worker = new OperationWorker(TimeProvider.System, host.Services.GetRequiredService<IServiceScopeFactory>(), host.Services.GetRequiredService<WorkerJobRunner>(),
                host.Services.GetRequiredService<WorkSignals>(), NullLogger<OperationWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
            try
            {
                // The lane has looked once and sleeps for a minute; queueing a scan wakes it.
                await Task.Delay(1500);
                using var queued = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/roots/" + rootId + "/scans");
                Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode, await queued.Content.ReadAsStringAsync());
                await WaitForAsync(async () => (long)(await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"OperationTypeId\" = 1 AND \"OperationStatusTypeId\" = 3"))! == 1);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        [TestMethod]
        public async Task Queue_ScansAndBackups_AreClaimedApart_SoABackupRunsWhileAScanDoes()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, settings: new Dictionary<string, string> { ["Backup:PgDumpPath"] = Path.Combine(storage.Path, "missing", "pg_dump.exe") });
            var mountId = await LibraryTests.CreateMountAsync(host);
            var rootId = await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            // The backup is the older one in the queue, and a scan is running.
            using var backup = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/backups");
            Assert.IsTrue((await backup.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            using var scan = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/library/roots/" + rootId + "/scans");
            Assert.AreEqual(HttpStatusCode.OK, scan.StatusCode);
            await using var scope = host.Services.CreateAsyncScope();
            var claim = scope.ServiceProvider.GetRequiredService<ClaimOperationV1>();
            var scanClaim = await claim.ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.LibraryScan), default);
            Assert.AreEqual(OperationType.LibraryScan, scanClaim.OperationTypeId, "The scan lane never takes the backup.");
            Assert.IsNull((await claim.ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.LibraryScan), default)).OperationId);

            // While the scan is still running the backup lane takes the backup and runs it; the scan is untouched.
            await using var other = host.Services.CreateAsyncScope();
            Assert.IsTrue(await OperationWorker.RunNextAsync(other.ServiceProvider, NullLogger.Instance, OperationType.Backup, CancellationToken.None));
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"Id\" = " + scanClaim.OperationId));
            Assert.AreEqual((short)4, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 3"));
            Assert.IsFalse(await OperationWorker.RunNextAsync(other.ServiceProvider, NullLogger.Instance, OperationType.Backup, CancellationToken.None));

            // Only scans and backups are queued work.
            await Assert.ThrowsAsync<DataValidationException>(() => claim.ExecuteAsync<ClaimOperationResultV1>(new ClaimOperationParametersV1(OperationType.Download), default));
        }

        [TestMethod]
        public async Task DownloadWorker_LooksAtOnceForANewDownload_AndSlowsDownWithoutOne()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            var signals = host.Services.GetRequiredService<WorkSignals>();

            // Nothing runs: tracking reports it and waits a minute between looks.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Acquisition'");
                Assert.IsFalse((await scope.ServiceProvider.GetRequiredService<MonitorDownloadsV1>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default)).Success);
            }

            using var worker = new DownloadWorker(TimeProvider.System, jobs, host.Services.GetRequiredService<WorkerJobRunner>(), signals, host.Services.GetRequiredService<LiveChanges>());
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(() => Task.FromResult(jobs.Describe("downloads", null).LastFinishedAt is not null));
                var first = jobs.Describe("downloads", null);
                Assert.IsTrue(first.NextRunAt - first.LastFinishedAt >= TimeSpan.FromSeconds(55), "Without a download the next look is a minute away.");

                // A grabbed download wakes the worker without waiting for its tick.
                signals.Downloads.Notify();
                await WaitForAsync(() => Task.FromResult(jobs.Describe("downloads", null).LastFinishedAt > first.LastFinishedAt));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!await condition())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, "The worker did not react within 20 seconds.");
                await Task.Delay(100);
            }
        }

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
