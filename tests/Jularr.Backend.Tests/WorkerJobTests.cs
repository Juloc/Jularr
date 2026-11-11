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
        public void Definitions_EveryGroupAndJob_HaveTheirTextOnTheJobsPage()
        {
            var keys = Jularr.Frontend.Localization.UiResources.English.Select(resource => resource.Key).ToHashSet();
            foreach (var group in Enum.GetValues<WorkerGroupType>())
            {
                Assert.IsTrue(keys.Contains("jobs.worker." + group), "jobs.worker." + group);
            }

            foreach (var job in WorkerJobs.Definitions)
            {
                Assert.IsTrue(keys.Contains("jobs.job." + job.Key), "jobs.job." + job.Key);
            }
        }

        [TestMethod]
        public void Definitions_CoverEveryWorkerAndTheSampler_AndStaggerTheirStart()
        {
            var jobs = WorkerJobs.Definitions;
            Assert.AreEqual(jobs.Count, jobs.Select(job => job.Key).Distinct().Count());
            foreach (var group in Enum.GetValues<WorkerGroupType>())
            {
                Assert.IsTrue(WorkerJobs.Of(group).Count > 0, group.ToString());
            }

            // Seventeen workers and the sampler. Queues and interrupted downloads start at once; other workers have their own offset;
            // a provider of the provider sync has no schedule of its own.
            Assert.AreEqual(18, Enum.GetValues<WorkerGroupType>().Length);
            foreach (var key in new[] { "operations", "backupQueue", "mediaImportQueue", "benchmarkQueue", "searchQueue", "downloads", "resources" })
            {
                Assert.AreEqual(TimeSpan.Zero, WorkerJobs.Definition(key).StartDelay, key);
            }

            var parts = jobs.Where(job => job.ParentKey is not null).ToArray();
            CollectionAssert.AreEquivalent(new[] { "providerSyncPlex", "providerSyncJellyfin", "providerSyncEmby" }, parts.Select(job => job.Key).ToArray());
            Assert.IsTrue(parts.All(job => job.Group == WorkerGroupType.Provider && job.Interval is null && !job.CanRunNow));
            var delayed = jobs.Where(job => job.StartDelay > TimeSpan.Zero).ToArray();
            Assert.AreEqual(jobs.Count - 7 - parts.Length, delayed.Length);
            Assert.IsTrue(delayed.GroupBy(job => job.StartDelay).All(group => group.Select(job => job.Group).Distinct().Count() == 1), "Only jobs sharing a worker may share their start offset.");
            Assert.IsTrue(delayed.All(job => job.StartDelay < TimeSpan.FromMinutes(5)));

            // Rounds that copy whole libraries or downloads onto the NAS get hours before their limit, an operation (never resumed after it) the most;
            // quick looks get minutes.
            foreach (var key in new[] { "operations", "backupQueue", "mediaImportQueue", "benchmarkQueue", "searchQueue" })
            {
                Assert.IsTrue(WorkerJobs.Definition(key).RoundLimit >= TimeSpan.FromHours(12), key);
            }

            foreach (var key in new[] { "catalog", "providerSync", "downloads", "usenet" })
            {
                Assert.IsTrue(WorkerJobs.Definition(key).RoundLimit >= TimeSpan.FromHours(3), key);
            }

            Assert.IsTrue(WorkerJobs.Definition("translationMachines").RoundLimit <= TimeSpan.FromMinutes(10));

            // Heavy jobs wait while Jularr is used, downloads never do.
            foreach (var key in new[] { "trickplay", "recognition", "readingCopies", "scanScheduling", "providerSync", "aniListMatching" })
            {
                Assert.IsTrue(WorkerJobs.Definition(key).PausesWhileUsedByDefault, key);
            }

            foreach (var key in new[] { "downloads", "usenet", "operations", "backupQueue", "backup", "wantedSearch", "discoverFeeds", "mediaImportQueue" })
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

            // A failure shows on the schedule, in the Tasks (leading to the schedule) and as a notice.
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            time.Now = Start.AddHours(2);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("subtitles", (_, _) => throw new InvalidOperationException("No provider."), default));
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            var html = await page.Content.ReadAsStringAsync();
            // The list has 25 jobs to a page; the failed one may be on the next.
            using var secondPage = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs?pageNumber=2");
            html += await secondPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Seek thumbnails");
            StringAssert.Contains(html, "An unknown error occurred.");
            StringAssert.Contains(html, "Run now");
            using var activity = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity");
            var items = (await activity.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items;
            var notice = items.Single(item => item.OperationTypeId == OperationType.Job);
            Assert.AreEqual("subtitles", notice.Title);
            Assert.AreEqual("internal_error", notice.ReasonKey);
            // The Jobs entry's Tasks show the notice, leading back to the schedule.
            using var tasksPage = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs/Tasks");
            var tasksHtml = await tasksPage.Content.ReadAsStringAsync();
            StringAssert.Contains(tasksHtml, "handler=Dismiss&amp;operationId=" + notice.Id);
            StringAssert.Contains(tasksHtml, "Open Jobs");

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

        [TestMethod]
        public async Task JobsPage_AFailedPass_SaysWhy_InTheRowAndInTheHistory_AndAnUnknownCodeReadsAsUnknownError()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            var time = new FixedTime(Start);
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();

            // A job that failed and runs well again keeps its entry in the history, with the reason of its error code.
            time.Now = Start.AddHours(1);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("aniListSync", (_, _) => throw new ServiceUnavailableException("anilist_unavailable"), default));
            time.Now = Start.AddHours(2);
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("aniListSync", (_, _) => Task.CompletedTask, default));
            Assert.AreEqual(0L, await OpenNoticesAsync(host, "aniListSync"));
            using var recovered = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            var html = await recovered.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode, html);
            StringAssert.Contains(html, "AniList could not be reached.");
            StringAssert.Contains(html, "Background job");

            // A failure whose code has no text reads as an unknown error, in the row and in the drawer, never as the raw exception.
            time.Now = Start.AddHours(3);
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("metadataFill", (_, _) => throw new ServiceUnavailableException("code_without_text"), default));
            using var unknown = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs");
            html = await unknown.Content.ReadAsStringAsync();
            using var secondPage = await host.SendAsync(HttpMethod.Get, "/Admin/Jobs?pageNumber=2");
            html += await secondPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, unknown.StatusCode, html);
            StringAssert.Contains(html, "An unknown error occurred.");
            Assert.IsFalse(html.Contains("code_without_text", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("temporarily unavailable", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Runner_ARoundPastItsLimit_FailsWithTheTimeLimit_AndTheGroupGoesOn()
        {
            var time = new ManualTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            time.Advance(TimeSpan.FromMinutes(5));

            // A step that waits for ever, but listens to its token, is stopped at the group's limit.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = runner.RunAsync("catalog", async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            time.Advance(WorkerJobs.Definition("catalog").RoundLimit - TimeSpan.FromSeconds(1));
            Assert.IsFalse(running.IsCompleted, "The round runs up to its limit.");
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(WorkerJobResultType.Failed, await running.WaitAsync(TimeSpan.FromSeconds(10)));

            // The failure is kept and told like any other; the next pass is planned.
            Assert.AreEqual("job_round_timeout", await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'catalog'"));
            Assert.AreEqual(1L, await OpenNoticesAsync(host, "catalog"));
            var status = jobs.Describe("catalog", null);
            Assert.AreEqual(WorkerJobStateType.Failed, status.State);
            Assert.AreEqual(status.LastFinishedAt + TimeSpan.FromMinutes(1), status.NextRunAt);

            // The worker is free again: the group's next job runs.
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("discoverFeeds", (_, _) => Task.CompletedTask, default));
        }

        [TestMethod]
        public async Task Runner_ARoundIgnoringItsStop_IsNamedInTheLog_AndStillEndsAtItsLimit()
        {
            var time = new ManualTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            time.Advance(TimeSpan.FromMinutes(5));

            // The round does not listen; it is awaited, never abandoned, and the log names it after the grace.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = runner.RunAsync("maintenance", (_, _) => release.Task, default);
            var limit = WorkerJobs.Definition("maintenance").RoundLimit;
            await time.WaitForTimerAsync(limit + WorkerJobRunner.StopGrace);
            time.Advance(limit);
            time.Advance(WorkerJobRunner.StopGrace);
            Assert.IsFalse(running.IsCompleted);
            release.SetResult();

            // Coming back late is still a round past its limit.
            Assert.AreEqual(WorkerJobResultType.Failed, await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("job_round_timeout", await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'maintenance'"));
            var log = host.Services.GetRequiredService<RecentLogs>().Read(100).Where(entry => entry.Category == typeof(WorkerJobRunner).FullName).Select(entry => entry.Message).ToArray();
            Assert.IsTrue(log.Any(message => message.Contains("maintenance ignores its time limit", StringComparison.Ordinal)), string.Join(" | ", log));
        }

        [TestMethod]
        public async Task Runner_ABusyPass_IsKeptAtItsLimit_AndGoesOnAtTheNextTick()
        {
            var time = new ManualTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            time.Advance(TimeSpan.FromMinutes(5));
            var rounds = 0;
            Task<bool> Busy(IServiceProvider services, CancellationToken token)
            {
                rounds++;
                time.Advance(TimeSpan.FromMinutes(4));
                return Task.FromResult(true);
            }

            // Rounds go on while there is work; the pass that reaches its limit is kept and the job is due again at once.
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunWhileBusyAsync("covers", Busy, TimeSpan.Zero, default));
            Assert.AreEqual(3, rounds);
            var kept = jobs.Describe("covers", null);
            Assert.AreEqual(WorkerJobStateType.Idle, kept.State);
            Assert.AreEqual(kept.LastFinishedAt, kept.NextRunAt);
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"LastResultTypeId\" FROM \"WorkerJobs\" WHERE \"Key\" = 'covers'"));

            // The next tick goes on with the work.
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunWhileBusyAsync("covers", (_, _) => Task.FromResult(++rounds < 5), TimeSpan.Zero, default));
            Assert.AreEqual(5, rounds);
        }

        [TestMethod]
        public async Task Runner_AStateThatCannotBeKeptOnce_IsTriedAgain_AndAfterTwoFailuresTheNextPassKeepsIt()
        {
            var time = new ManualTime(Start);
            await using var host = await Host.CreateAsync(library: true, services: services => services.AddSingleton<TimeProvider>(time));
            var runner = host.Services.GetRequiredService<WorkerJobRunner>();
            var jobs = host.Services.GetRequiredService<WorkerJobs>();
            Task Fail(IServiceProvider services, CancellationToken token) => throw new InvalidOperationException("The catalog broke.");
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.AreEqual(WorkerJobResultType.Succeeded, await runner.RunAsync("catalog", (_, _) => Task.CompletedTask, default));

            // The database refuses the catalog's record, counting each try.
            const string refuse = """
                CREATE SEQUENCE "RefusedRecords";
                CREATE FUNCTION "RefuseRecord"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('"RefusedRecords"'); RAISE EXCEPTION 'busy'; END $$;
                CREATE TRIGGER "RefuseRecord" BEFORE INSERT OR UPDATE ON "WorkerJobs" FOR EACH ROW WHEN (NEW."Key" = 'catalog') EXECUTE FUNCTION "RefuseRecord"();
                """;
            await host.Fixture.RunSqlAsync(refuse);
            time.Advance(TimeSpan.FromMinutes(2));
            var running = runner.RunAsync("catalog", Fail, default);
            await time.WaitForTimerAsync(WorkerJobRunner.ReportRetry);
            await host.Fixture.RunSqlAsync("DROP TRIGGER \"RefuseRecord\" ON \"WorkerJobs\"");
            time.Advance(WorkerJobRunner.ReportRetry);
            Assert.AreEqual(WorkerJobResultType.Failed, await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT last_value FROM \"RefusedRecords\""));
            Assert.AreEqual("internal_error", await host.ScalarAsync("SELECT \"LastErrorKey\" FROM \"WorkerJobs\" WHERE \"Key\" = 'catalog'"));

            // Refused twice, the state is not lost: the next pass keeps it although its outcome did not change.
            await host.Fixture.RunSqlAsync("CREATE TRIGGER \"RefuseRecord\" BEFORE INSERT OR UPDATE ON \"WorkerJobs\" FOR EACH ROW WHEN (NEW.\"Key\" = 'catalog') EXECUTE FUNCTION \"RefuseRecord\"()");
            time.Advance(TimeSpan.FromMinutes(2));
            running = runner.RunAsync("catalog", Fail, default);
            await time.WaitForTimerAsync(WorkerJobRunner.ReportRetry);
            time.Advance(WorkerJobRunner.ReportRetry);
            Assert.AreEqual(WorkerJobResultType.Failed, await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT last_value FROM \"RefusedRecords\""));
            await host.Fixture.RunSqlAsync("DROP TRIGGER \"RefuseRecord\" ON \"WorkerJobs\"");
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.IsTrue(jobs.RequestRun("catalog"));
            Assert.AreEqual(WorkerJobResultType.Failed, await runner.RunAsync("catalog", Fail, default));
            Assert.AreEqual(time.Now.UtcDateTime.Ticks, ((DateTime)(await host.ScalarAsync("SELECT \"LastFinishedAt\" FROM \"WorkerJobs\" WHERE \"Key\" = 'catalog'"))!).Ticks);
        }

        private static async Task<long> OpenNoticesAsync(Host host, string key) =>
            (long)(await host.ScalarAsync("SELECT count(*) FROM \"Operations\" WHERE \"WorkerJobKey\" = '" + key + "' AND \"OperationTypeId\" = 4 AND \"DismissedAt\" IS NULL"))!;

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        // A clock that moves only when the test moves it; its timers fire on that move.
        internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
        {
            private readonly object _gate = new();
            private readonly List<ManualTimer> _timers = [];
            private DateTimeOffset _now = now;

            public DateTimeOffset Now
            {
                get
                {
                    lock (_gate)
                    {
                        return _now;
                    }
                }
            }

            public override DateTimeOffset GetUtcNow() => Now;
            public override long GetTimestamp() => Now.UtcTicks;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                return timer;
            }

            public void Advance(TimeSpan by)
            {
                List<ManualTimer> due;
                lock (_gate)
                {
                    _now += by;
                    due = [.. _timers.Where(timer => timer.DueAt <= _now)];
                    foreach (var timer in due)
                    {
                        if (timer.Period > TimeSpan.Zero) timer.DueAt = _now + timer.Period;
                        else _timers.Remove(timer);
                    }
                }

                foreach (var timer in due)
                {
                    timer.Fire();
                }
            }

            // Until something waits on this clock for exactly this long, such as a retry's pause.
            public async Task WaitForTimerAsync(TimeSpan dueTime)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (true)
                {
                    lock (_gate)
                    {
                        if (_timers.Any(timer => timer.DueTime == dueTime)) return;
                    }

                    Assert.IsTrue(DateTime.UtcNow < deadline, "Nothing waited for " + dueTime + ".");
                    await Task.Delay(10);
                }
            }

            private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
            {
                public DateTimeOffset DueAt { get; set; }
                public TimeSpan DueTime { get; private set; }
                public TimeSpan Period { get; private set; }

                public void Fire() => callback(state);

                public bool Change(TimeSpan dueTime, TimeSpan period)
                {
                    lock (clock._gate)
                    {
                        clock._timers.Remove(this);
                        DueTime = dueTime;
                        Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                        if (dueTime == Timeout.InfiniteTimeSpan) return true;
                        DueAt = clock._now + dueTime;
                        clock._timers.Add(this);
                    }

                    return true;
                }

                public void Dispose()
                {
                    lock (clock._gate)
                    {
                        clock._timers.Remove(this);
                    }
                }

                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
