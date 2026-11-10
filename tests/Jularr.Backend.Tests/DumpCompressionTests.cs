using Jularr.Backend.Tests.Infrastructure;

namespace Jularr.Backend.Tests
{
    // The backup before an update dumps with zstd level 1 (much faster than the default), and falls back when pg_dump cannot.
    [TestClass]
    public sealed class DumpCompressionTests
    {
        private const string Connection = "Host=127.0.0.1;Port=15433;Username=jularr_test;Password=x;Database=jularr_test_admin";

        [TestMethod]
        public async Task Dump_AsksForZstdLevelOne()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("The fake pg_dump is a shell script.");
            }

            using var folder = new LibraryTests.TemporaryDirectory();
            var arguments = Path.Combine(folder.Path, "arguments.txt");
            var script = Script(folder.Path, "printf '%s\\n' \"$@\" > '" + arguments + "'\nfor a in \"$@\"; do case \"$a\" in --file=*) printf 'PGDMP' > \"${a#--file=}\";; esac; done\n");

            var dump = Path.Combine(folder.Path, "one.dump");
            await DatabaseBaselineTests.Tools(Connection, script, null).DumpAsync(dump, default);

            Assert.IsTrue(File.Exists(dump));
            CollectionAssert.Contains(File.ReadAllLines(arguments), "--compress=zstd:1");
        }

        [TestMethod]
        public async Task Dump_ByAPgDumpWithoutZstd_IsRunAgainWithItsDefaultCompression()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("The fake pg_dump is a shell script.");
            }

            using var folder = new LibraryTests.TemporaryDirectory();
            var script = Script(folder.Path,
                "for a in \"$@\"; do case \"$a\" in --file=*) file=\"${a#--file=}\";; esac; done\n"
                + "for a in \"$@\"; do case \"$a\" in --compress=zstd*) printf 'partial' > \"$file\"; echo 'pg_dump: error: this build does not support compression with ZSTD' >&2; exit 1;; esac; done\n"
                + "printf 'PGDMP' > \"$file\"\n");

            var dump = Path.Combine(folder.Path, "two.dump");
            await DatabaseBaselineTests.Tools(Connection, script, null).DumpAsync(dump, default);

            Assert.AreEqual("PGDMP", await File.ReadAllTextAsync(dump));
        }

        [TestMethod]
        public async Task Dump_ThatFailsForAnotherReason_IsNotRetried()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("The fake pg_dump is a shell script.");
            }

            using var folder = new LibraryTests.TemporaryDirectory();
            var count = Path.Combine(folder.Path, "count.txt");
            var script = Script(folder.Path, "echo x >> '" + count + "'\necho 'pg_dump: error: connection to server failed' >&2\nexit 1\n");

            await Assert.ThrowsAsync<Jularr.Integrations.Data.Database.PostgresToolException>(() =>
                DatabaseBaselineTests.Tools(Connection, script, null).DumpAsync(Path.Combine(folder.Path, "three.dump"), default));
            Assert.AreEqual(1, File.ReadAllLines(count).Length);
        }

        private static string Script(string folder, string body)
        {
            var script = Path.Combine(folder, "pg-dump-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
            File.WriteAllText(script, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return script;
        }
    }
}
