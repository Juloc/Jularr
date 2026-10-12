using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.MountHelper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class StorageTests
    {
        private const string Route = "/api/v1/admin/storage/mounts";
        private const string Password = "nas-password,with=punctuation";

        [TestMethod]
        public async Task Connections_SaveAndEdit_ProtectCredentialsAndRestrictOwnerAccess()
        {
            await using var host = await CreateHostAsync();
            using var created = await host.SendAsync(HttpMethod.Post, Route, Smb());
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<CreateStorageMountResultV1>())!.Id;
            var stored = (byte[])(await host.ScalarAsync("SELECT \"ProtectedPassword\" FROM \"StorageMounts\""))!;
            Assert.IsFalse(Encoding.UTF8.GetString(stored).Contains(Password, StringComparison.Ordinal));

            using var read = await host.SendAsync(HttpMethod.Get, Route + "?pageSize=1");
            var rows = (await read.Content.ReadFromJsonAsync<PageResult<StorageMountDetailsV1>>())!;
            Assert.AreEqual(1, rows.Items.Count);
            Assert.IsTrue(rows.Items[0].HasPassword);
            Assert.IsFalse((await read.Content.ReadAsStringAsync()).Contains(Password, StringComparison.Ordinal));
            using var empty = await host.SendAsync(HttpMethod.Get, Route + "?pageSize=1&page=2");
            Assert.AreEqual(0, (await empty.Content.ReadFromJsonAsync<PageResult<StorageMountDetailsV1>>())!.Items.Count);

            // Omitted passwords remain protected.
            using var changed = await host.SendAsync(HttpMethod.Put, Route + "/" + id, Smb(password: null, name: "Changed"));
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(stored, (byte[])(await host.ScalarAsync("SELECT \"ProtectedPassword\" FROM \"StorageMounts\""))!);

            using var invalidHost = await host.SendAsync(HttpMethod.Post, Route, Smb(server: "https://nas/"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidHost.StatusCode);
            using var traversal = await host.SendAsync(HttpMethod.Post, Route, Smb(path: "/share/../outside"));
            Assert.AreEqual(HttpStatusCode.BadRequest, traversal.StatusCode);
            using var legacyVersion = await host.SendAsync(HttpMethod.Post, Route, Smb(version: "1.0"));
            Assert.AreEqual(HttpStatusCode.BadRequest, legacyVersion.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"StorageMounts\""));

            // NFS has no SMB credentials.
            using var nfs = await host.SendAsync(HttpMethod.Put, Route + "/" + id,
                new SaveStorageMountDataV1("NFS", StorageProtocolType.Nfs, "nas.test", "/exports/media", "4.1", null, null, true, null));
            Assert.AreEqual(HttpStatusCode.OK, nfs.StatusCode, await nfs.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedPassword\" IS NULL FROM \"StorageMounts\""));

            // MediaManager cannot configure storage.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            using var deniedRead = await host.SendAsync(HttpMethod.Get, Route);
            using var deniedCreate = await host.SendAsync(HttpMethod.Post, Route, Smb());
            using var deniedConnect = await host.SendAsync(HttpMethod.Post, Route + "/" + id + "/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Connect));
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedConnect.StatusCode);
        }

        [TestMethod]
        public async Task Razor_SettingsAndSetup_UseTheSameStoredConnectionsWithoutExposingPasswords()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var setup = await host.SendAsync(HttpMethod.Get, "/Setup/Storage");
            Assert.AreEqual(HttpStatusCode.OK, setup.StatusCode, await setup.Content.ReadAsStringAsync());
            var html = await setup.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "maxlength=\"253\"");
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

            using var saved = await host.SendAsync(HttpMethod.Post, "/Setup/Storage", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["Name"] = "<NAS>", ["StorageProtocolTypeId"] = "Smb", ["Host"] = "nas.test", ["SharePath"] = "/media",
                ["ProtocolVersion"] = "3.1.1", ["Username"] = "jularr", ["Password"] = Password, ["IsReadOnly"] = "True", ["WakeOnDemand"] = "False"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var settings = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=1");
            html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, html);
            StringAssert.Contains(html, "&lt;NAS&gt;");
            Assert.IsFalse(html.Contains(Password, StringComparison.Ordinal));
            StringAssert.Contains(html, "name=\"Password\"");

            // Missing helper does not break browsing.
            using var unavailable = await host.SendAsync(HttpMethod.Post, Route + "/1/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Connect));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            StringAssert.Contains(await unavailable.Content.ReadAsStringAsync(), "storage_helper_unavailable");
            using var nfs = await host.SendAsync(HttpMethod.Get, "/Setup/Storage?protocol=Nfs");
            Assert.AreEqual(HttpStatusCode.OK, nfs.StatusCode, await nfs.Content.ReadAsStringAsync());
            Assert.IsFalse((await nfs.Content.ReadAsStringAsync()).Contains("name=\"Password\"", StringComparison.Ordinal));

            using var browser = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            browser.DefaultRequestHeaders.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var denied = await browser.PostAsync("/Admin/Storage?handler=Connection&storageMountId=1",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["OperationTypeId"] = "Connect" }));
            Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
        }

        [TestMethod]
        public async Task MountOperation_PrivateSocket_DecryptsOnlyForConnectAndDetectsConcurrentConfigurationChanges()
        {
            await using var host = await CreateHostAsync();
            using var created = await host.SendAsync(HttpMethod.Post, Route, Smb());
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var socketPath = Path.Combine(Path.GetTempPath(), "jularr-mount-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            host.Services.GetRequiredService<NasMountClient>().SocketPath = socketPath;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                // Controlled external boundary, no NAS.
                var helper = Task.Run(async () =>
                {
                    using var socket = await listener.AcceptAsync(timeout.Token);
                    using var stream = new NetworkStream(socket);
                    var request = await NasMountProtocol.ReadAsync<NasMountRequest>(stream, timeout.Token);
                    Assert.AreEqual(Password, request.Password);
                    Assert.AreEqual(NasMountAction.Connect, request.Action);
                    await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"Version\" = \"Version\" + 1 WHERE \"Id\" = 1");
                    await NasMountProtocol.WriteAsync(stream, new NasMountResponse(true, request.Version), timeout.Token);
                });
                using var connected = await host.SendAsync(HttpMethod.Post, Route + "/1/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Connect));
                Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
                var status = (await connected.Content.ReadFromJsonAsync<StorageMountStatusV1>())!;
                Assert.IsTrue(status.IsMounted);
                Assert.IsFalse(status.IsCurrentConfiguration);
                await helper;

                // Status never needs the password.
                var statusHelper = Task.Run(async () =>
                {
                    using var socket = await listener.AcceptAsync(timeout.Token);
                    using var stream = new NetworkStream(socket);
                    var request = await NasMountProtocol.ReadAsync<NasMountRequest>(stream, timeout.Token);
                    Assert.IsNull(request.Password);
                    Assert.AreEqual(NasMountAction.Status, request.Action);
                    await NasMountProtocol.WriteAsync(stream, new NasMountResponse(true, request.Version), timeout.Token);
                });
                using var current = await host.SendAsync(HttpMethod.Post, Route + "/1/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.ReadStatus));
                Assert.AreEqual(HttpStatusCode.OK, current.StatusCode, await current.Content.ReadAsStringAsync());
                Assert.IsTrue((await current.Content.ReadFromJsonAsync<StorageMountStatusV1>())!.IsCurrentConfiguration);
                await statusHelper;
            }
            finally
            {
                if (File.Exists(socketPath)) File.Delete(socketPath);
            }
        }

        [TestMethod]
        public async Task Helper_ProtocolBoundary_RejectsUnsafeRequestsAndKeepsSecretsOffCommandArguments()
        {
            var request = new NasMountRequest(NasMountAction.Connect, 17, 1, "Smb", "nas.test", "/share/Media Library", "3.1.1", "jularr", null, Password, true);
            MountManager.Validate(request);
            var command = MountManager.BuildMountCommand(request, "/mnt/jularr/17", "/run/jularr-mount-helper/credential-test");
            Assert.AreEqual("/usr/bin/mount", command.FileName);
            CollectionAssert.Contains(command.ArgumentList.ToArray(), "//nas.test/share/Media Library");
            Assert.IsFalse(string.Join(' ', command.ArgumentList).Contains(Password, StringComparison.Ordinal));
            StringAssert.Contains(command.ArgumentList[3], "cache=strict");
            StringAssert.Contains(command.ArgumentList[3], "nosuid,nodev,noexec,ro");

            var traversal = new NasMountRequest(NasMountAction.Connect, 17, 1, "Nfs", "nas.test", "/share/../etc", "4.1", null, null, null, false);
            Assert.ThrowsExactly<ArgumentException>(() => MountManager.Validate(traversal));
            var injectedCredentials = new NasMountRequest(NasMountAction.Connect, 17, 1, "Smb", "nas.test", "/share", "3.1.1", "user\npassword=other", null, Password, true);
            Assert.ThrowsExactly<ArgumentException>(() => MountManager.Validate(injectedCredentials));

            // Reject oversized IPC before allocation.
            using var frame = new MemoryStream(new byte[] { 0, 2, 0, 0 });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NasMountProtocol.ReadAsync<NasMountRequest>(frame, CancellationToken.None));
        }

        private static SaveStorageMountDataV1 Smb(string? password = Password, string name = "NAS", string server = "nas.test", string path = "/media", string version = "3.1.1")
        {
            return new SaveStorageMountDataV1(name, StorageProtocolType.Smb, server, path, version, "jularr", null, true, password);
        }

        private static async Task<Host> CreateHostAsync(bool razor = false)
        {
            var host = await Host.CreateAsync(razor: razor, storage: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            return host;
        }
    }
}
