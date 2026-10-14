using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Admin.Backup.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Backup.V1;
using Jularr.Backend.Service.System.Operation.V1;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BackupTests
    {
        private const string Route = "/api/v1/admin/backups";

        [TestMethod]
        public async Task Backups_Folder_ListsCopiesOfThisSchemaAndRestoresOnlyBeforeSetupCompletes()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            var missingTool = Path.Combine(folder.Path, "missing", "pg_tool.exe");
            using var keys = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(new Dictionary<string, string>
            {
                ["Backup:Path"] = folder.Path,
                ["Backup:PgDumpPath"] = missingTool,
                ["Backup:PgRestorePath"] = missingTool,
                ["DataProtection:KeyDirectory"] = keys.Path
            });
            await WriteBackupAsync(folder.Path, "jularr-20261001-120000", BackupFiles.SchemaFingerprint);
            await WriteBackupAsync(folder.Path, "jularr-20261002-120000", "other-schema");
            Directory.CreateDirectory(Path.Combine(folder.Path, "jularr-20261003-120000.partial"));
            Directory.CreateDirectory(Path.Combine(folder.Path, "jularr-20261001-120000", "keys"));
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "jularr-20261001-120000", "keys", "key-1.xml"), "<key />");
            Directory.CreateDirectory(Path.Combine(folder.Path, "notes"));

            // Newest first; only finished backup folders count.
            var backups = await ReadAsync(host);
            Assert.IsTrue(backups.IsConfigured);
            Assert.IsTrue(backups.CanRestore);
            CollectionAssert.AreEqual(new[] { "jularr-20261002-120000", "jularr-20261001-120000" }, backups.Items.Select(item => item.Name).ToArray());
            CollectionAssert.AreEqual(new[] { false, true }, backups.Items.Select(item => item.IsCompatible).ToArray());

            // Restores need this schema and a working pg_restore.
            using var other = await host.SendAsync(HttpMethod.Post, Route + "/jularr-20261002-120000/restore");
            StringAssert.Contains(await other.Content.ReadAsStringAsync(), "backup_incompatible");
            using var noTool = await host.SendAsync(HttpMethod.Post, Route + "/jularr-20261001-120000/restore");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noTool.StatusCode, await noTool.Content.ReadAsStringAsync());
            Assert.IsTrue(File.Exists(Path.Combine(keys.Path, "key-1.xml")));
            using var traversal = await host.SendAsync(HttpMethod.Post, Route + "/..%2Fnotes/removal");
            Assert.AreNotEqual(HttpStatusCode.OK, traversal.StatusCode);
            Assert.IsTrue(Directory.Exists(Path.Combine(folder.Path, "notes")));

            // A finished setup is never replaced.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSetupState\" SET \"CompletedAt\" = now()");
            Assert.IsFalse((await ReadAsync(host)).CanRestore);
            using var completed = await host.SendAsync(HttpMethod.Post, Route + "/jularr-20261001-120000/restore");
            StringAssert.Contains(await completed.Content.ReadAsStringAsync(), "setup_completed");

            // Deleting removes the folder; pruning keeps the newest.
            using var deleted = await host.SendAsync(HttpMethod.Post, Route + "/jularr-20261002-120000/removal");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await ReadAsync(host)).Items.Count);
            await WriteBackupAsync(folder.Path, "jularr-20261004-120000", BackupFiles.SchemaFingerprint);
            BackupFiles.Prune(folder.Path, 1);
            CollectionAssert.AreEqual(new[] { "jularr-20261004-120000" }, (await ReadAsync(host)).Items.Select(item => item.Name).ToArray());
            Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "jularr-20261003-120000.partial")));
            Assert.AreEqual(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), (await ReadAsync(host)).Items[0].CreatedAt);
        }

        [TestMethod]
        public async Task Backups_Queue_RunOneAtATimeAndFailWithoutPgDumpLeavingNothingBehind()
        {
            using var folder = new LibraryTests.TemporaryDirectory();
            await using var host = await CreateHostAsync(new Dictionary<string, string>
            {
                ["Backup:Path"] = folder.Path,
                ["Backup:PgDumpPath"] = Path.Combine(folder.Path, "missing", "pg_dump.exe")
            });

            // One queued backup at a time.
            using var queued = await host.SendAsync(HttpMethod.Post, Route);
            Assert.IsTrue((await queued.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            using var again = await host.SendAsync(HttpMethod.Post, Route);
            Assert.IsFalse((await again.Content.ReadFromJsonAsync<UpdateResult>())!.Success);
            Assert.IsTrue((await ReadAsync(host)).IsRunning);

            // The worker claims it like a scan; without pg_dump nothing half-written stays.
            await using var scope = host.Services.CreateAsyncScope();
            var claimed = await scope.ServiceProvider.GetRequiredService<ClaimOperationV1>().ExecuteAsync<ClaimOperationResultV1>(new OperationQueueParametersV1(), default, CancellationToken.None);
            Assert.AreEqual(OperationType.Backup, claimed.OperationTypeId);
            var failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() =>
                scope.ServiceProvider.GetRequiredService<ExecuteBackupV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), default, CancellationToken.None));
            Assert.AreEqual("backup_tool_missing", failure.Code);
            await scope.ServiceProvider.GetRequiredService<FailOperationV1>().ExecuteAsync<UpdateResult>(new OperationParametersV1(claimed.OperationId!.Value), new FailOperationDataV1(failure.Code),
                CancellationToken.None);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder.Path).Length);
            var state = await ReadAsync(host);
            Assert.IsFalse(state.IsRunning);
            Assert.AreEqual("backup_tool_missing", state.ErrorKey);
        }

        private static async Task WriteBackupAsync(string root, string name, string fingerprint)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, BackupFiles.DumpName), "dump");
            await File.WriteAllTextAsync(Path.Combine(folder, "backup.json"), "{\"SchemaFingerprint\":\"" + fingerprint + "\",\"CreatedAt\":\"2026-10-01T12:00:00Z\"}");
        }

        private static async Task<BackupsV1> ReadAsync(Host host)
        {
            using var read = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            return (await read.Content.ReadFromJsonAsync<BackupsV1>())!;
        }

        private static async Task<Host> CreateHostAsync(IReadOnlyDictionary<string, string> settings)
        {
            var host = await Host.CreateAsync(library: true, settings: settings);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            return host;
        }
    }
}
