using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Npgsql;

namespace Jularr.E2E.Tests
{
    // The update at the start of a new version: the backup and the migrations run before the application can serve, and the configured
    // addresses must answer meanwhile with the status page instead of a closed port.
    [TestClass]
    public sealed class StartupStatusPageTests
    {
        private static readonly string[] Baselines = ["Account", "Instance", "Media", "Acquisition", "Storage", "Library", "Playback"];

        [TestMethod]
        [Timeout(300_000)]
        public async Task Update_ShowsTheStatusPageWhileTheBackupRuns_ThenTheApplicationTakesOver()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("The slow backup is a shell script.");
            }

            var folder = Directory.CreateTempSubdirectory("jularr-e2e-update-").FullName;
            var release = Path.Combine(folder, "backup-may-finish");
            var dump = Path.Combine(folder, "slow-pg-dump.sh");

            // A pg_dump that takes as long as the test wants: it writes the custom-format signature once the release file exists.
            await File.WriteAllTextAsync(dump, "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in --file=*) file=\"${a#--file=}\";; esac; done\n"
                + "while [ ! -f \"" + release + "\" ]; do sleep 0.2; done\nprintf 'PGDMP' > \"$file\"\n");
            File.SetUnixFileMode(dump, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var backups = Directory.CreateDirectory(Path.Combine(folder, "backups")).FullName;

            BrowserSession? session = null;
            var phases = new List<string>();
            try
            {
                // The database is the frozen baseline without a version table: the next start has to back it up and migrate it.
                await using var jularr = await JularrInstance.StartAsync(
                    new Dictionary<string, string> { ["Backup:PgDumpPath"] = dump, ["Backup:Path"] = backups },
                    beforeStart: InstallBaselinesAsync,
                    whileStarting: async instance =>
                    {
                        session = await BrowserSession.StartAsync(instance);
                        var page = session.Page;

                        // Until the backup is running the page says that the database is being checked; then it names the backup.
                        await WaitForPhaseAsync(page, "backingup");
                        var response = await page.GotoAsync("/Admin/Works");
                        Assert.AreEqual(503, response!.Status);
                        Assert.IsNotNull(response.Headers.GetValueOrDefault("retry-after"));
                        Assert.AreEqual("no-store", response.Headers.GetValueOrDefault("cache-control"));
                        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Jularr wird aktualisiert");
                        await Assertions.Expect(page.Locator("[role=status]")).ToContainTextAsync("Sicherung läuft");
                        await Assertions.Expect(page.Locator(".step--current")).ToHaveTextAsync("Sicherung");
                        await Assertions.Expect(page.Locator("[data-startup-elapsed]")).ToContainTextAsync("Läuft seit");
                        phases.Add(await page.Locator("[data-startup-phase]").GetAttributeAsync("data-startup-phase") ?? "");

                        // Phone, tablet and desktop: nothing runs wider than the screen.
                        foreach (var (name, width, height) in new[] { ("phone", 375, 812), ("tablet", 768, 1024), ("desktop", 1280, 800) })
                        {
                            await page.SetViewportSizeAsync(width, height);
                            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
                            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"), "The status page runs wider than the " + name + ".");
                            await Evidence.SaveAsync(page, "startup-status-backup-" + name);
                        }

                        // The page reloads itself: once the backup is released, the browser (never told to reload) ends on the application.
                        await File.WriteAllTextAsync(release, "");
                        await page.WaitForFunctionAsync("() => !document.querySelector('[data-startup-phase]') && document.readyState === 'complete'", null, new() { Timeout = 90_000 });
                    });

                var final = await session!.Page.GotoAsync("/");
                Assert.AreEqual(200, final!.Status);
                Assert.AreEqual(0, await session.Page.Locator("[data-startup-phase]").CountAsync());
                CollectionAssert.Contains(phases, "backingup");
                Assert.AreEqual(1, Directory.GetDirectories(backups).Length, "The update's backup is missing.");
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                Directory.Delete(folder, true);
            }
        }

        private static async Task WaitForPhaseAsync(IPage page, string phase)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await page.GotoAsync("/");
                    if (await page.Locator("[data-startup-phase=" + phase + "]").CountAsync() > 0)
                    {
                        return;
                    }
                }
                catch (PlaywrightException)
                {
                    // The host process is still starting: nothing listens for the first moments.
                }

                await Task.Delay(200);
            }

            Assert.Fail("The status page never showed the phase " + phase + ".");
        }

        private static async Task InstallBaselinesAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            foreach (var name in Baselines)
            {
                var sql = await File.ReadAllTextAsync(Path.Combine(JularrInstance.Repository, "database", name + ".sql"));
                sql = Regex.Replace(sql, @"\A\s*BEGIN;|COMMIT;\s*\z", "");
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
