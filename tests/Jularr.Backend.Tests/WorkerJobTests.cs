using System.Net;
using System.Net.Http.Json;
using System.Security;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Service.Core;
using Jularr.Workers;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The registry of background jobs: what is due, paused or run on request, how a pass is kept, and what an Owner sees and controls.
    [TestClass]
    public sealed class WorkerJobTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void Definitions_CoverEveryWorkerAndTheSampler_AndStaggerTheirStart()
        {
            var jobs = WorkerJobs.Definitions;
            Assert.AreEqual(jobs.Count, jobs.Select(job => job.Key).Distinct().Count());
            foreach (var group in Enum.GetValues<WorkerGroupType>())
            {
                Assert.IsTrue(WorkerJobs.Of(group).Count > 0, group.ToString());
            }

            // Thirteen workers and the sampler. The queues and the downloads a restart interrupts start at once, every other job has its own offset;
            // a provider of the provider sync has no schedule of its own.
            Assert.AreEqual(14, Enum.GetValues<WorkerGroupType>().Length);
            foreach (var key in new[] { "operations", "backupQueue", "downloads", "resources" })
            {
                Assert.AreEqual(TimeSpan.Zero, WorkerJobs.Definition(key).StartDelay, key);
            }

            var parts = jobs.Where(job => job.ParentKey is not null).ToArray();
            CollectionAssert.AreEquivalent(new[] { "providerSyncPlex", "providerSyncJellyfin", "providerSyncEmby" }, parts.Select(job => job.Key).ToArray());
            Assert.IsTrue(parts.All(job => job.Group == WorkerGroupType.Provider && job.Interval is null && !job.CanRunNow));
            var delayed = jobs.Where(job => job.StartDelay > TimeSpan.Zero).ToArray();
            Assert.AreEqual(jobs.Count - 4 - parts.Length, delayed.Length);
            Assert.AreEqual(delayed.Length, delayed.Select(job => job.StartDelay).Distinct().Count(), "Every delayed job has its own offset.");
            Assert.IsTrue(delayed.All(job => job.StartDelay < TimeSpan.FromMinutes(5)));

            // Heavy jobs wait while Jularr is used, downloads never do.
            foreach (var key in new[] { "trickplay", "recognition", "scanScheduling", "providerSync", "aniListMatching" })
            {
                Assert.IsTrue(WorkerJobs.Definition(key).PausesWhileUsedByDefault, key);
            }

            foreach (var key in new[] { "downloads", "usenet", "operations", "backupQueue", "backup", "wantedSearch" })
            {
                Assert.IsFalse(WorkerJobs.Definition(key).CanPauseWhileUsed, key);
            }
        }

        [TestMethod]
        public void Begin_FollowsStartDelayPauseRunNowAndUse()
        {
            var time = new FixedTime(Start);
            var activity = new UserActivity(time);
            var jobs = new WorkerJobs(time, activity);
            jobs.Load([]);

            // Not before its offset, then once per interval.
            Assert.AreEqual(WorkerJobGate.NotDue, jobs.Begin("trickplay"));
            time.Now = Start.AddSeconds(80);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("trickplay"));
            Assert.AreEqual(WorkerJobStateType.Running, jobs.Describe("trickplay", null).State);
            jobs.Finish("trickplay", WorkerJobResultType.Succeeded, null);

            // Run now starts a pass at once, once.
            Assert.AreEqual(WorkerJobGate.NotDue, jobs.Begin("subtitles"));
            Assert.IsTrue(jobs.RequestRun("subtitles"));
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("subtitles"));
            Assert.IsTrue(jobs.IsRequestedRun("subtitles"));
            jobs.Finish("subtitles", WorkerJobResultType.Succeeded, null);
            Assert.AreEqual(WorkerJobGate.NotDue, jobs.Begin("subtitles"));
            Assert.IsFalse(jobs.RequestRun("operations"), "The queue runs on its own.");

            Assert.AreEqual(WorkerJobGate.NotDue, jobs.Begin("trickplay"));
            Assert.AreEqual(WorkerJobStateType.Idle, jobs.Describe("trickplay", null).State);
            time.Now = Start.AddMinutes(7);
            jobs.Pause("trickplay", true);
            Assert.AreEqual(WorkerJobGate.Paused, jobs.Begin("trickplay"), "A paused job does not run.");
            Assert.AreEqual(WorkerJobStateType.Paused, jobs.Describe("trickplay", null).State);
            Assert.IsFalse(jobs.RequestRun("trickplay"), "A paused job is resumed before it runs.");
            jobs.Pause("trickplay", false);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("trickplay"));
            jobs.Finish("trickplay", WorkerJobResultType.Succeeded, null);

            // A heavy job waits while Jularr is used unless asked for or told not to.
            time.Now = Start.AddMinutes(20);
            activity.Touch();
            Assert.AreEqual(WorkerJobGate.WaitingForUse, jobs.Begin("recognition"));
            Assert.AreEqual(WorkerJobStateType.WaitingForUse, jobs.Describe("recognition", null).State);
            Assert.IsTrue(jobs.IsHalted("recognition"));
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("downloads"), "Downloads never wait for people.");
            jobs.Finish("downloads", WorkerJobResultType.Succeeded, null);
            Assert.IsTrue(jobs.RequestRun("recognition"));
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("recognition"));
            jobs.Finish("recognition", WorkerJobResultType.Succeeded, null);
            time.Now = time.Now.AddMinutes(2);
            jobs.SetPausesWhileUsed("recognition", false);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("recognition"));
            jobs.Finish("recognition", WorkerJobResultType.Failed, "internal_error");
            var failed = jobs.Describe("recognition", null);
            Assert.AreEqual(WorkerJobStateType.Failed, failed.State);
            Assert.AreEqual("internal_error", failed.LastErrorKey);
            time.Now = time.Now.AddMinutes(10);
            Assert.IsFalse(jobs.IsHalted("recognition"));
        }

        [TestMethod]
        public void Finish_KeepsAQuietSuccessOnlyOnceAMinute_AndEveryChangeAtOnce()
        {
            var time = new FixedTime(Start);
            var jobs = new WorkerJobs(time, new UserActivity(time));
            jobs.Load([]);
            time.Now = Start.AddMinutes(1);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("downloads"));
            Assert.IsNotNull(jobs.Finish("downloads", WorkerJobResultType.Succeeded, null), "The first pass is kept.");

            // Tracking looks every minute, and again after 15 seconds while a download runs.
            jobs.RunAgainWithin("downloads", TimeSpan.FromSeconds(15));
            time.Now = time.Now.AddSeconds(15);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("downloads"));
            Assert.IsNull(jobs.Finish("downloads", WorkerJobResultType.Succeeded, null), "A quiet success is not written every 15 seconds.");
            jobs.RunAgainWithin("downloads", TimeSpan.FromSeconds(15));
            time.Now = time.Now.AddSeconds(15);
            Assert.AreEqual(WorkerJobGate.Run, jobs.Begin("downloads"));
            var failure = jobs.Finish("downloads", WorkerJobResultType.Failed, "provider_unavailable");
            Assert.AreEqual(WorkerJobResultType.Failed, failure?.ResultTypeId, "A failure is kept at once.");
            jobs.RunAgainWithin("downloads", TimeSpan.FromSeconds(15));
            time.Now = time.Now.AddSeconds(15);
            jobs.Begin("downloads");
            Assert.IsNull(jobs.Finish("downloads", WorkerJobResultType.Failed, "provider_unavailable"), "The same failure again is not written again.");
            time.Now = time.Now.AddSeconds(70);
            jobs.Begin("downloads");
            Assert.IsNotNull(jobs.Finish("downloads", WorkerJobResultType.Succeeded, null), "Recovering is kept at once.");
        }

        [TestMethod]
        public async Task Runner_RunsADueJobOnce_NotWhilePaused_AndOnRequest()
        {
            var time = new FixedTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            var passes = 0;
            Task Pass(IServiceProvider services, CancellationToken token)
            {
                passes++;
                return Task.CompletedTask;
            }

            // The offset keeps the first pass back; then it runs, and not again before its interval.
            Assert.IsNull(await runner.RunAsync("backup", Pass, default));
            time.Now = Start.AddMinutes(10);
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("backup", Pass, default));
            Assert.IsNull(await runner.RunAsync("backup", Pass, default));
            Assert.AreEqual(1, passes);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkerJobs\" WHERE \"Key\" = 'backup' AND \"LastResultTypeId\" = 1"));

            // Paused in the database before the process reads it: the job does not run after the restart either.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkerJobs\" (\"Key\", \"IsPaused\") VALUES ('maintenance', TRUE)");
            time.Now = Start.AddHours(1);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<Jularr.Backend.Service.System.Worker.V1.LoadWorkerJobsV1>()
                    .ExecuteAsync<UpdateResult>(new Jularr.Backend.Data.System.Worker.V1.LoadWorkerJobsParametersV1(), default);
            }

            Assert.IsNull(await runner.RunAsync("maintenance", Pass, default));
            Assert.AreEqual(1, passes);

            // Run now runs the job once, even before its interval is over.
            time.Now = Start.AddMinutes(15);
            Assert.IsTrue(jobs.RequestRun("backup"));
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("backup", Pass, default));
            Assert.IsNull(await runner.RunAsync("backup", Pass, default));
            Assert.AreEqual(2, passes);
        }

        [TestMethod]
        public async Task Runner_AFailedPass_BecomesOneNoticeInActivity_UntilTheJobRunsWellOrIsDismissed()
        {
            var time = new FixedTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            Task Fail(IServiceProvider services, CancellationToken token) => throw new InvalidOperationException("The seek thumbnails broke.");
            Task Work(IServiceProvider services, CancellationToken token) => Task.CompletedTask;
            Task Off(IServiceProvider services, CancellationToken token) => throw new SecurityException("The module is off.");

            // The first failure leaves the state and one notice, with a sanitized error.
            time.Now = Start.AddHours(1);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            Assert.AreEqual("internal_error", await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'catalog'"));
            Assert.AreEqual(1L, await OpenNoticesAsync(host, "catalog"));
            Assert.AreEqual("internal_error", await host.ScalarAsync("SELECT \"ErrorKey\" FROM \"Operations\" WHERE \"WorkerJobKey\" = 'catalog'"));

            // Failing again adds no second notice; a person who dismissed it is not reminded by every pass.
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'catalog'"));

            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DismissedAt\" = now() WHERE \"WorkerJobKey\" = 'catalog'");
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            Assert.AreEqual(0L, await OpenNoticesAsync(host, "catalog"));

            // Running well again closes the notice of a job; a new failure after that is told again.
            await host.Fixture.RunSqlAsync("UPDATE \"Operations\" SET \"DismissedAt\" = NULL WHERE \"WorkerJobKey\" = 'catalog'");
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("catalog", Work, default));
            Assert.AreEqual(0L, await OpenNoticesAsync(host, "catalog"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'catalog'"));
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            Assert.AreEqual(1L, await OpenNoticesAsync(host, "catalog"));

            // A switched-off module is no failure.
            time.Now = time.Now.AddMinutes(5);
            Assert.AreEqual(WorkerJobResultType.Skipped, await runner.RunAsync("maintenance", Off, default));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = 'maintenance'"));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"LastResultTypeId\" FROM \"WorkerJobs\" WHERE \"Key\" = 'maintenance'"));
        }

        [TestMethod]
        public async Task Admin_ListsJobs_PausesRunsAndSetsUse_ForTheOwnerOnly()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var time = new FixedTime(Start);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<TimeProvider>(time));

            // Every job with its group; nothing has run yet.
            using var listed = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/jobs");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var jobs = (await listed.Content.ReadFromJsonAsync<BaseDataList<WorkerJobV1>>())!;
            Assert.AreEqual(WorkerJobs.Definitions.Count, jobs.Count);
            Assert.AreEqual(WorkerJobStateType.Pending, jobs.Single(job => job.Key == "trickplay").WorkerJobStateTypeId);
            Assert.IsTrue(jobs.Single(job => job.Key == "trickplay").PausesWhileUsed);
            Assert.IsFalse(jobs.Single(job => job.Key == "usenet").CanRunNow);
            Assert.IsNull(jobs.Single(job => job.Key == "operations").IntervalSeconds);

            // Pausing is kept and shown; resuming undoes it.
            using var paused = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/trickplay/pause", new PauseWorkerJobDataV1(true));
            Assert.AreEqual(HttpStatusCode.OK, paused.StatusCode, await paused.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsPaused\" FROM \"WorkerJobs\" WHERE \"Key\" = 'trickplay'"));
            Assert.IsTrue(host.Services.GetRequiredService<WorkerJobs>().Describe("trickplay", null).IsPaused);
            using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/jobs");
            Assert.AreEqual(WorkerJobStateType.Paused, (await after.Content.ReadFromJsonAsync<BaseDataList<WorkerJobV1>>())!.Single(job => job.Key == "trickplay").WorkerJobStateTypeId);
            using var runPaused = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/trickplay/run");
            Assert.AreEqual(HttpStatusCode.BadRequest, runPaused.StatusCode, "A paused job is resumed before it runs.");
            using var resumed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/trickplay/pause", new PauseWorkerJobDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode);

            // Run now reaches the job's worker; jobs that run by themselves refuse it.
            using var run = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/trickplay/run");
            Assert.AreEqual(HttpStatusCode.OK, run.StatusCode, await run.Content.ReadAsStringAsync());
            using var runQueue = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/usenet/run");
            Assert.AreEqual(HttpStatusCode.BadRequest, runQueue.StatusCode);
            using var unknown = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/nothing/run");
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);

            // Only a heavy job can wait while Jularr is used.
            using var use = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/trickplay/use", new WorkerJobUseDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, use.StatusCode, await use.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"PausesWhileUsed\" FROM \"WorkerJobs\" WHERE \"Key\" = 'trickplay'"));
            using var useDownloads = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/jobs/downloads/use", new WorkerJobUseDataV1(true));
            Assert.AreEqual(HttpStatusCode.BadRequest, useDownloads.StatusCode);

            // A failure shows on the page, in Activity and as a notice that links to the page.
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            time.Now = Start.AddHours(2);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("subtitles", (_, _) => throw new InvalidOperationException("No provider."), default));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Seek thumbnails");
            StringAssert.Contains(html, "An unknown error occurred.");
            StringAssert.Contains(html, "Run now");
            using var activity = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity");
            var items = (await activity.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items;
            var notice = items.Single(item => item.OperationTypeId == OperationType.Job);
            Assert.AreEqual("subtitles", notice.Title);
            Assert.AreEqual("internal_error", notice.ReasonKey);
            using var activityPage = await host.SendAsync(HttpMethod.Get, "/Admin/Activity");
            var activityHtml = await activityPage.Content.ReadAsStringAsync();
            StringAssert.Contains(activityHtml, "Open Jobs");

            // A Media Manager and a plain user see neither the page nor the jobs.
            foreach (var role in new[] { 3, 2 })
            {
                await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = " + role + " WHERE \"Id\" = 41");
                foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/v1/admin/jobs"), (HttpMethod.Post, "/api/v1/admin/jobs/trickplay/run"), (HttpMethod.Post, "/api/v1/admin/jobs/trickplay/pause") })
                {
                    using var denied = await host.SendAsync(method, path, method == HttpMethod.Post ? new PauseWorkerJobDataV1(true) : null);
                    Assert.IsTrue(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, role + " " + path + " answered " + denied.StatusCode);
                }

                using var hidden = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
                Assert.IsFalse((await hidden.Content.ReadAsStringAsync()).Contains("Seek thumbnails", StringComparison.Ordinal), "role " + role);
            }
        }

        private static async Task<long> OpenNoticesAsync(Host host, string key) =>
            (long)(await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = '" + key + "' AND \"OperationTypeId\" = 4 AND \"DismissedAt\" IS NULL"))!;

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
