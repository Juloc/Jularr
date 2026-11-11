using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Tests.Infrastructure;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Backend.Tests
{
    // The status page that answers while the update runs: the phases the update reports, the page and the listener that serves it, and the
    // hand-over to the application's own server.
    [TestClass]
    public sealed class StartupStatusTests
    {
        [TestMethod]
        public async Task DatabaseUpgrade_ReportsTheBackupAndEveryMigration_InOrder()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var cs = TestPostgres.ResolveConnectionString(nameof(DatabaseUpgrade_ReportsTheBackupAndEveryMigration_InOrder));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            var backups = Directory.CreateDirectory(Path.Combine(folder.Path, "backups")).FullName;
            var baseline = new DatabaseBaseline(cs, new BackupOptions { Path = backups }, DatabaseBaselineTests.Tools(cs, DatabaseMigrationTests.FakeDump(folder.Path, true), null),
                [DatabaseBaselineTests.Probe(2, "SELECT 1;"), DatabaseBaselineTests.Probe(3, "SELECT 1;")]);
            using var services = new ServiceCollection().AddLogging().AddSingleton(baseline).BuildServiceProvider();

            var progress = new List<DatabaseUpgradeProgress>();
            Assert.IsNull(await DatabaseUpgrade.RunAsync(services, default, progress.Add));

            CollectionAssert.AreEqual(
                new[]
                {
                    new DatabaseUpgradeProgress(DatabaseUpgradePhase.Checking),
                    new DatabaseUpgradeProgress(DatabaseUpgradePhase.BackingUp),
                    new DatabaseUpgradeProgress(DatabaseUpgradePhase.Migrating, 1, 2),
                    new DatabaseUpgradeProgress(DatabaseUpgradePhase.Migrating, 2, 2)
                },
                progress);
        }

        [TestMethod]
        public async Task DatabaseUpgrade_ACurrentDatabase_ReportsOnlyTheCheck_NoBackupNoMigration()
        {
            var cs = TestPostgres.ResolveConnectionString(nameof(DatabaseUpgrade_ACurrentDatabase_ReportsOnlyTheCheck_NoBackupNoMigration));
            await new DatabaseBaseline(cs, migrations: []).StartAsync(CancellationToken.None);
            using var services = new ServiceCollection().AddLogging().AddSingleton(new DatabaseBaseline(cs, migrations: [])).BuildServiceProvider();

            var progress = new List<DatabaseUpgradeProgress>();
            Assert.IsNull(await DatabaseUpgrade.RunAsync(services, default, progress.Add));
            CollectionAssert.AreEqual(new[] { new DatabaseUpgradeProgress(DatabaseUpgradePhase.Checking) }, progress);
        }

        [TestMethod]
        public void Status_FollowsThePhases_AndAFailureStays()
        {
            var status = new StartupStatus();
            Assert.AreEqual(StartupPhase.Checking, status.Snapshot().Phase);
            Assert.IsFalse(status.Snapshot().Updating);

            status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.BackingUp));
            Assert.AreEqual(StartupPhase.BackingUp, status.Snapshot().Phase);
            Assert.IsTrue(status.Snapshot().Updating);

            status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.Migrating, 2, 5));
            var migrating = status.Snapshot();
            Assert.AreEqual((StartupPhase.Migrating, 2, 5), (migrating.Phase, migrating.Done, migrating.Total));

            status.Starting();
            Assert.AreEqual(StartupPhase.Starting, status.Snapshot().Phase);
            Assert.IsTrue(status.Snapshot().Updating, "An update that is starting up stays an update.");

            var failure = new DatabaseUpgradeFailure("0002_probe", "reason");
            status.Fail(failure);
            status.Starting();
            status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.BackingUp));
            Assert.AreEqual(StartupPhase.Failed, status.Snapshot().Phase);
            Assert.AreSame(failure, status.Snapshot().Failure);
        }

        [TestMethod]
        public void Page_SaysInGerman_WhatTheUpdateIsDoing_AndReloadsItself()
        {
            var backup = StartupStatusPage.Render(new StartupSnapshot(StartupPhase.BackingUp, true, 0, 0, TimeSpan.FromSeconds(134), null));
            StringAssert.Contains(backup, "Jularr wird aktualisiert");
            StringAssert.Contains(backup, "Sicherung läuft");
            StringAssert.Contains(backup, "Läuft seit 2 Min 14 s");
            StringAssert.Contains(backup, "<meta http-equiv=\"refresh\" content=\"" + StartupStatusPage.ReloadSeconds.ToString(CultureInfo.InvariantCulture) + "\" />");
            StringAssert.Contains(backup, "data-startup-phase=\"backingup\"");
            StringAssert.Contains(backup, "class=\"step step--current\" aria-current=\"step\"><span class=\"dot\"></span>Sicherung");
            StringAssert.Contains(backup, "step--next\"><span class=\"dot\"></span>Datenbank-Update");

            var migrating = StartupStatusPage.Render(new StartupSnapshot(StartupPhase.Migrating, true, 3, 7, TimeSpan.FromSeconds(9), null));
            StringAssert.Contains(migrating, "Datenbank-Update läuft");
            StringAssert.Contains(migrating, "Schritt 3 von 7");
            StringAssert.Contains(migrating, "step--done\"><span class=\"dot\"></span>Sicherung");

            var starting = StartupStatusPage.Render(new StartupSnapshot(StartupPhase.Starting, true, 0, 0, TimeSpan.FromSeconds(9), null));
            StringAssert.Contains(starting, "Fast geschafft");
            StringAssert.Contains(starting, "step--done\"><span class=\"dot\"></span>Datenbank-Update");
        }

        // A restart that has nothing to update only says that Jularr is starting: no steps that never happen.
        [TestMethod]
        public void Page_OfAPlainRestart_ShowsNoSteps()
        {
            var page = StartupStatusPage.Render(new StartupSnapshot(StartupPhase.Checking, false, 0, 0, TimeSpan.FromSeconds(1), null));
            StringAssert.Contains(page, "Jularr startet");
            StringAssert.Contains(page, "Die Datenbank wird geprüft.");
            Assert.DoesNotContain("class=\"steps\"", page);
            Assert.DoesNotContain("aria-current", page);
        }

        // Neither the database nor a built frontend is needed: no stylesheet, script or image is fetched, and nothing of the update's reason leaks.
        [TestMethod]
        public void Page_IsSelfContained()
        {
            var page = StartupStatusPage.Render(new StartupSnapshot(StartupPhase.Migrating, true, 1, 2, TimeSpan.Zero, null));
            Assert.DoesNotContain("<link", page);
            Assert.DoesNotContain("<script", page);
            Assert.DoesNotContain("src=", page);
            Assert.DoesNotContain("href=", page);
            StringAssert.Contains(page, "<svg");
            StringAssert.Contains(page, "prefers-reduced-motion");
        }

        [TestMethod]
        [DataRow(0, "0 s")]
        [DataRow(59, "59 s")]
        [DataRow(60, "1 Min 0 s")]
        [DataRow(754, "12 Min 34 s")]
        [DataRow(3 * 3600 + 5 * 60 + 9, "3 Std 5 Min")]
        public void Elapsed_IsShortAndGerman(int seconds, string expected) =>
            Assert.AreEqual(expected, StartupStatusPage.Elapsed(TimeSpan.FromSeconds(seconds)));

        [TestMethod]
        public async Task Server_AnswersEveryRequestWith503_ThePhasePage_AndAfterAFailureTheMaintenancePage()
        {
            var address = "http://127.0.0.1:" + FreePort().ToString(CultureInfo.InvariantCulture);
            await using var server = await StartupStatusServer.StartAsync(["--urls", address]);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            server.Status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.BackingUp));
            using var backup = await client.GetAsync("/Admin/Works");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, backup.StatusCode);
            Assert.AreEqual("5", backup.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual("no-store", backup.Headers.CacheControl!.ToString());
            Assert.AreEqual("text/html; charset=utf-8", backup.Content.Headers.ContentType!.ToString());
            StringAssert.Contains(await backup.Content.ReadAsStringAsync(), "Sicherung läuft");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/v1/anything", new StringContent(""))).StatusCode);

            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, head.StatusCode);
            Assert.AreEqual("", await head.Content.ReadAsStringAsync());

            server.Status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.Migrating, 1, 2));
            StringAssert.Contains(await client.GetStringAsyncAnyStatus("/"), "Schritt 1 von 2");

            server.Status.Fail(new DatabaseUpgradeFailure("0002_probe", "reason"));
            using var failed = await client.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.AreEqual(120, failed.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
            var body = await failed.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "Datenbank-Update fehlgeschlagen");
            StringAssert.Contains(body, "0002_probe");
            Assert.DoesNotContain("data-startup-phase", body);
        }

        // The container's probes while the update runs: the process is alive but does not serve yet, also after a failed update.
        [TestMethod]
        public async Task Server_HealthProbes_AliveButNotReady_AlsoAfterAFailure()
        {
            var address = "http://127.0.0.1:" + FreePort().ToString(CultureInfo.InvariantCulture);
            await using var server = await StartupStatusServer.StartAsync(["--urls", address]);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            server.Status.Report(new DatabaseUpgradeProgress(DatabaseUpgradePhase.Migrating, 1, 2));
            await AssertProbeAsync(client, Health.LivePath, HttpStatusCode.OK, "Healthy");
            await AssertProbeAsync(client, Health.ReadyPath, HttpStatusCode.ServiceUnavailable, "Unhealthy");

            server.Status.Fail(new DatabaseUpgradeFailure("0002_probe", "reason"));
            await AssertProbeAsync(client, Health.LivePath, HttpStatusCode.OK, "Healthy");
            await AssertProbeAsync(client, Health.ReadyPath, HttpStatusCode.ServiceUnavailable, "Unhealthy");
        }

        // The application's probes: anonymous and without details. Ready only while the application serves, with this release's schema and a
        // database that answers within the limit on a connection of its own; a lock or a stop makes it 503, never a hanging answer.
        [TestMethod]
        public async Task Application_HealthProbes_ReadyFollowsTheDatabaseAndTheStop()
        {
            await using var host = await AuthenticationTests.Host.CreateAsync(library: true, endpoints: endpoints => endpoints.MapJularrHealth());
            await AssertProbeAsync(host.Client, Health.LivePath, HttpStatusCode.OK, "Healthy");
            await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.OK, "Healthy");

            // Another session holds the version table: the probe gives up at its limit.
            await using (var holder = new NpgsqlConnection(host.Fixture.ConnectionString))
            {
                await holder.OpenAsync();
                await using var transaction = await holder.BeginTransactionAsync();
                await using (var hold = new NpgsqlCommand("LOCK TABLE \"DatabaseSchemaVersions\" IN ACCESS EXCLUSIVE MODE", holder, transaction))
                {
                    await hold.ExecuteNonQueryAsync();
                }

                var clock = Stopwatch.StartNew();
                await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.ServiceUnavailable, "Unhealthy");
                Assert.IsLessThan(DatabaseReadyCheck.Limit + TimeSpan.FromSeconds(3), clock.Elapsed);
                await AssertProbeAsync(host.Client, Health.LivePath, HttpStatusCode.OK, "Healthy");
            }

            await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.OK, "Healthy");

            // A schema of another release is not ready.
            await host.Fixture.RunSqlAsync("UPDATE \"DatabaseSchemaVersions\" SET \"Version\" = \"Version\" + 1000 WHERE \"Version\" = (SELECT max(\"Version\") FROM \"DatabaseSchemaVersions\")");
            await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.ServiceUnavailable, "Unhealthy");
            await host.Fixture.RunSqlAsync("UPDATE \"DatabaseSchemaVersions\" SET \"Version\" = \"Version\" - 1000 WHERE \"Version\" > 1000");
            await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.OK, "Healthy");

            // From the stop on the application is no longer ready; it still lives.
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await AssertProbeAsync(host.Client, Health.ReadyPath, HttpStatusCode.ServiceUnavailable, "Unhealthy");
            await AssertProbeAsync(host.Client, Health.LivePath, HttpStatusCode.OK, "Healthy");
        }

        private static async Task AssertProbeAsync(HttpClient client, string path, HttpStatusCode status, string body)
        {
            using var response = await client.GetAsync(path);
            Assert.AreEqual(status, response.StatusCode, path);
            Assert.AreEqual(body, await response.Content.ReadAsStringAsync(), path);
            Assert.IsTrue(response.Headers.CacheControl!.NoStore, path);
        }

        // The hand-over: the status page keeps answering for as long as the application needs before its own server binds (here a hosted service
        // that takes a while to start, as the real ones do), and the application answers from the moment the status page lets go.
        [TestMethod]
        public async Task Handover_KeepsAnsweringUntilTheApplicationsServerBinds()
        {
            var address = "http://127.0.0.1:" + FreePort().ToString(CultureInfo.InvariantCulture);
            string[] args = ["--urls", address];
            await using var status = await StartupStatusServer.StartAsync(args);

            var handover = new StartupHandover();
            var builder = WebApplication.CreateBuilder(args);
            var slow = new SlowStart(TimeSpan.FromSeconds(1.5));
            builder.Services.AddSingleton<IHostedService>(slow);
            handover.Install(builder);
            await using var app = builder.Build();
            app.MapGet("/", () => "the application");
            handover.Arm(async () =>
            {
                await status.StopAsync();
                await status.DisposeAsync();
            });

            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(2) };
            var starting = app.StartAsync();
            var answers = new List<(long At, string Outcome)>();
            while (!starting.IsCompleted)
            {
                answers.Add((Stopwatch.GetTimestamp(), await OutcomeAsync(client)));
                await Task.Delay(10);
            }

            await starting;
            var beforeBind = answers.Where(answer => answer.At < slow.EndedAt).ToList();
            Assert.IsGreaterThan(5, beforeBind.Count, "Observed " + beforeBind.Count + " of " + answers.Count + " answers; the hosted service ended at " + (slow.EndedAt - answers[0].At) + ", the first answer " + answers[0].Outcome);
            Assert.IsTrue(beforeBind.All(answer => answer.Outcome == "503"), string.Join(",", beforeBind.Select(answer => answer.Outcome).Distinct()));
            Assert.AreEqual("the application", await client.GetStringAsync("/"));
        }

        private static async Task<string> OutcomeAsync(HttpClient client)
        {
            try
            {
                using var response = await client.GetAsync("/");
                return ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            catch (HttpRequestException)
            {
                return "refused";
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private sealed class SlowStart(TimeSpan delay) : IHostedService
        {
            public long EndedAt { get; private set; } = long.MaxValue;

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(delay, cancellationToken);
                EndedAt = Stopwatch.GetTimestamp();
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }

    internal static class StartupStatusTestClient
    {
        // The body of an answer whatever its status (the status page answers 503).
        public static async Task<string> GetStringAsyncAnyStatus(this HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            return await response.Content.ReadAsStringAsync();
        }
    }
}
