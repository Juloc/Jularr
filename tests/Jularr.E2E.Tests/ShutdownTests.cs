namespace Jularr.E2E.Tests
{
    // An update stops the running version (docker stop: SIGTERM, a kill after the grace period): it ends within its budget with exit code 0,
    // and the operation it interrupted is queued again and finished after the start.
    [TestClass]
    public sealed class ShutdownTests
    {
        // The host's shutdown budget and the worker's grace for a running step; compose.yaml kills only after 20 seconds.
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

        [TestMethod]
        [Timeout(300_000)]
        public async Task Stop_DuringABackup_ExitsWithinBudget_AndRequeues()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("The stop is a POSIX signal and the slow backup a shell script.");
            }

            var folder = Directory.CreateTempSubdirectory("jularr-e2e-stop-").FullName;
            var running = Path.Combine(folder, "backup-running");
            var release = Path.Combine(folder, "backup-may-finish");
            var dump = Path.Combine(folder, "slow-pg-dump.sh");

            // A pg_dump that runs until the test releases it, the way a long backup or scan step does.
            await File.WriteAllTextAsync(dump, "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in --file=*) file=\"${a#--file=}\";; esac; done\n"
                + "touch \"" + running + "\"\nwhile [ ! -f \"" + release + "\" ]; do sleep 0.2; done\nprintf 'PGDMP' > \"$file\"\n");
            File.SetUnixFileMode(dump, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var backups = Directory.CreateDirectory(Path.Combine(folder, "backups")).FullName;
            try
            {
                await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Backup:PgDumpPath"] = dump, ["Backup:Path"] = backups });

                // An idle host stops at once and cleanly.
                var idle = await jularr.StopGracefullyAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual(0, idle.ExitCode);
                Assert.IsLessThan(Budget, idle.Took, "The idle stop took " + idle.Took + ".");

                // A backup queued while the host was down; the backup lane takes it at the start.
                await jularr.RunSqlAsync("INSERT INTO \"Operations\" (\"OperationTypeId\") VALUES (3)");
                await jularr.StartAgainAsync();
                await WaitUntilAsync(() => Task.FromResult(File.Exists(running)), "The backup never started.");
                Assert.AreEqual((short)2, await jularr.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 3"));

                // The stop cuts the step after its grace and ends cleanly within the budget.
                var (exitCode, took) = await jularr.StopGracefullyAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual(0, exitCode);
                Assert.IsLessThan(Budget, took, "The stop took " + took + ".");
                Assert.AreEqual((short)2, await jularr.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 3"),
                    "The interrupted backup is neither finished nor failed.");

                // After the start the backup is queued again and finished.
                File.Delete(running);
                await File.WriteAllTextAsync(release, "");
                await jularr.StartAgainAsync();
                await WaitUntilAsync(async () => (short?)await jularr.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 3") == 3,
                    "The interrupted backup was not finished after the start.");
                Assert.IsTrue(File.Exists(running), "The backup ran again.");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(200);
            }

            Assert.Fail(failure);
        }
    }
}
