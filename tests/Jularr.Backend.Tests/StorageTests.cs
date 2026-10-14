using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Service.System.Storage.V1;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Data.Synology;
using Jularr.Integrations.Logic.Synology;
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
        [TestMethod]
        public void Synology_Certificates_NeedAnAuthorityOrTheOwnersTrust()
        {
            var selfSigned = new SynologyCertificate(new string('B', 64), false);
            Assert.IsFalse(SynologyClient.Accepts(null, selfSigned));
            Assert.IsFalse(SynologyClient.Accepts(new string('C', 64), selfSigned));
            Assert.IsTrue(SynologyClient.Accepts(new string('b', 64), selfSigned));
            Assert.IsTrue(SynologyClient.Accepts(null, new SynologyCertificate(new string('D', 64), true)));
        }

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

        [TestMethod]
        public async Task Connections_LostAfterConnect_ReconnectWithBackoffAndShowTheirState()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var share = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(share);
            File.WriteAllText(Path.Combine(share, "movie.mkv"), "movie");
            var socketPath = Path.Combine(Path.GetTempPath(), "jularr-mount-" + Guid.NewGuid().ToString("N")[..12] + ".sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
            host.Services.GetRequiredService<NasMountClient>().SocketPath = socketPath;
            var answers = new Queue<string>();
            var actions = new List<NasMountAction>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // The helper answers "mounted", "unmounted" or "failed" in turn, no NAS.
            var helper = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptAsync(stop.Token);
                    using var stream = new NetworkStream(socket);
                    var request = await NasMountProtocol.ReadAsync<NasMountRequest>(stream, stop.Token);
                    actions.Add(request.Action);
                    var answer = answers.Dequeue();
                    await NasMountProtocol.WriteAsync(stream, answer switch
                    {
                        "mounted" => new NasMountResponse(true, request.Version),
                        "unmounted" => new NasMountResponse(false, 0),
                        _ => new NasMountResponse(false, 0, "storage_unavailable")
                    }, stop.Token);
                }
            });

            async Task ReconnectAsync()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ReconnectStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageConnectionsParametersV1(), default, CancellationToken.None);
            }

            async Task<StorageMountDetailsV1> ReadAsync()
            {
                using var read = await host.SendAsync(HttpMethod.Get, Route + "/" + mountId);
                return (await read.Content.ReadFromJsonAsync<StorageMountDetailsV1>())!;
            }

            try
            {
                // Connect remembers the wish and what the share shows.
                answers.Enqueue("mounted");
                using var connected = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Connect));
                Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
                var mount = await ReadAsync();
                Assert.AreEqual(StorageHealthType.Online, mount.StorageHealthTypeId);
                Assert.IsNotNull(mount.CheckedAt);
                using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId);
                StringAssert.Contains(await page.Content.ReadAsStringAsync(), "class=\"storage__health\" role=\"status\">Online");

                // A mounted share is left alone.
                answers.Enqueue("mounted");
                await ReconnectAsync();
                CollectionAssert.AreEqual(new[] { NasMountAction.Connect, NasMountAction.Status }, actions);

                // Lost and not back yet: asleep (it has Wake-on-LAN), retried later.
                actions.Clear();
                answers.Enqueue("unmounted");
                answers.Enqueue("failed");
                await ReconnectAsync();
                CollectionAssert.AreEqual(new[] { NasMountAction.Status, NasMountAction.Connect }, actions);
                Assert.AreEqual(StorageHealthType.Sleeping, (await ReadAsync()).StorageHealthTypeId);
                Assert.AreEqual(1, await host.ScalarAsync("SELECT \"ReconnectAttempts\" FROM \"StorageMountStates\""));
                actions.Clear();
                await ReconnectAsync();
                Assert.AreEqual(0, actions.Count);

                // Due again, it reconnects by itself.
                await host.Fixture.RunSqlAsync("UPDATE \"StorageMountStates\" SET \"ReconnectAt\" = now() - interval '1 minute'");
                answers.Enqueue("unmounted");
                answers.Enqueue("mounted");
                await ReconnectAsync();
                Assert.AreEqual(StorageHealthType.Online, (await ReadAsync()).StorageHealthTypeId);
                Assert.AreEqual(0, await host.ScalarAsync("SELECT \"ReconnectAttempts\" FROM \"StorageMountStates\""));

                // Disconnect ends both the wish and the state.
                actions.Clear();
                answers.Enqueue("unmounted");
                using var disconnected = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Disconnect));
                Assert.AreEqual(HttpStatusCode.OK, disconnected.StatusCode, await disconnected.Content.ReadAsStringAsync());
                Assert.IsNull((await ReadAsync()).StorageHealthTypeId);
                answers.Enqueue("unmounted");
                using var checkedStatus = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.ReadStatus));
                Assert.AreEqual(HttpStatusCode.OK, checkedStatus.StatusCode);
                Assert.IsNull((await ReadAsync()).StorageHealthTypeId);
                await ReconnectAsync();
                CollectionAssert.AreEqual(new[] { NasMountAction.Disconnect, NasMountAction.Status }, actions);

                // A Connect while the NAS is away keeps the wish for the worker.
                answers.Enqueue("failed");
                using var failed = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/connection", new ExecuteStorageMountDataV1(StorageMountOperationType.Connect));
                Assert.AreNotEqual(HttpStatusCode.OK, failed.StatusCode);
                Assert.AreEqual(StorageHealthType.Sleeping, (await ReadAsync()).StorageHealthTypeId);
                Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsConnectRequested\" AND \"ReconnectAt\" > now() FROM \"StorageMountStates\""));
            }
            finally
            {
                await stop.CancelAsync();
                await helper.ContinueWith(_ => { });
                if (File.Exists(socketPath)) File.Delete(socketPath);
            }
        }

        [TestMethod]
        public async Task Synology_DsmAccess_ReadsHealthAndSendsPowerCommandsWithoutExposingThePassword()
        {
            var dsm = new FakeDsm();
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, services: services => services.AddHttpClient<SynologyClient>().ConfigurePrimaryHttpMessageHandler(() => dsm));
            var mountId = await LibraryTests.CreateMountAsync(host);
            var route = Route + "/" + mountId + "/synology";

            // A new access needs its password, which is never returned.
            using var noPassword = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://nas.test:5000", "admin", null));
            Assert.AreEqual(HttpStatusCode.BadRequest, noPassword.StatusCode);
            using var saved = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://nas.test:5000", "admin", "dsm-secret"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, Route + "/" + mountId);
            var json = await read.Content.ReadAsStringAsync();
            Assert.IsFalse(json.Contains("dsm-secret", StringComparison.Ordinal));
            Assert.AreEqual("admin", (await read.Content.ReadFromJsonAsync<StorageMountDetailsV1>())!.SynologyUsername);

            // A trusted certificate belongs to its address; plain HTTP has none.
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"SynologyCertificateSha256\" = repeat('A', 64)");
            using var same = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://nas.test:5000", "admin", null));
            Assert.AreEqual(HttpStatusCode.OK, same.StatusCode, await same.Content.ReadAsStringAsync());
            Assert.IsNotNull(await host.ScalarAsync("SELECT \"SynologyCertificateSha256\" FROM \"StorageMounts\""));
            using var certificate = await host.SendAsync(HttpMethod.Post, route + "/certificate");
            Assert.IsNull((await certificate.Content.ReadFromJsonAsync<SynologyCertificateV1>())!.Sha256);

            // Health in one session that is closed again; the password only in the login body.
            using var health = await host.SendAsync(HttpMethod.Post, route + "/status");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, await health.Content.ReadAsStringAsync());
            var status = (await health.Content.ReadFromJsonAsync<SynologyStatusV1>())!;
            Assert.AreEqual("DS920+", status.Model);
            Assert.AreEqual(8_000_000_000_000L, status.Volumes.Single().TotalBytes);
            Assert.AreEqual(36, status.Disks.Single().Temperature);
            CollectionAssert.AreEqual(new[] { "login", "info", "load_info", "logout" }, dsm.Methods);
            Assert.AreEqual("dsm-secret", dsm.Password);
            Assert.IsTrue(dsm.Addresses.All(address => !address.Contains("dsm-secret", StringComparison.Ordinal)));

            // Power commands and a rejected login.
            dsm.Methods.Clear();
            using var shutdown = await host.SendAsync(HttpMethod.Post, route + "/power", new PowerStorageSynologyDataV1(SynologyPowerType.Shutdown));
            Assert.AreEqual(HttpStatusCode.OK, shutdown.StatusCode, await shutdown.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "login", "shutdown" }, dsm.Methods);

            // A lost answer counts as sent; a refusal does not.
            dsm.DropPower = true;
            using var dropped = await host.SendAsync(HttpMethod.Post, route + "/power", new PowerStorageSynologyDataV1(SynologyPowerType.Restart));
            Assert.AreEqual(HttpStatusCode.OK, dropped.StatusCode, await dropped.Content.ReadAsStringAsync());
            dsm.DropPower = false;
            dsm.RefusePower = true;
            using var refused = await host.SendAsync(HttpMethod.Post, route + "/power", new PowerStorageSynologyDataV1(SynologyPowerType.Restart));
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "synology_not_admin");
            dsm.RejectLogin = true;
            using var rejected = await host.SendAsync(HttpMethod.Post, route + "/status");
            StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "synology_login_failed");
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "Read NAS health");
            using var confirm = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId + "&power=Shutdown");
            StringAssert.Contains(await confirm.Content.ReadAsStringAsync(), "Shut down the NAS now?");

            // The stored password never follows another address.
            using var moved = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://elsewhere.test:5000", "admin", null));
            Assert.AreEqual(HttpStatusCode.BadRequest, moved.StatusCode);

            // No address removes the access.
            using var removed = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1(null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"StorageMounts\" WHERE \"SynologyProtectedPassword\" IS NOT NULL"));
        }

        private sealed class FakeDsm : HttpMessageHandler
        {
            public List<string> Methods { get; } = [];
            public List<string> Addresses { get; } = [];
            public string? Password { get; private set; }
            public bool RejectLogin { get; set; }
            public bool DropPower { get; set; }
            public bool RefusePower { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Addresses.Add(request.RequestUri!.ToString());
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                var method = form["method"]!;
                Methods.Add(method);
                if (method == "login") Password = form["passwd"];
                if (method == "reboot" && DropPower) throw new HttpRequestException("The NAS went down.");
                var body = method switch
                {
                    "login" when RejectLogin => "{\"success\":false,\"error\":{\"code\":400}}",
                    "reboot" when RefusePower => "{\"success\":false,\"error\":{\"code\":105}}",
                    "login" => "{\"success\":true,\"data\":{\"sid\":\"session-1\"}}",
                    "info" => "{\"success\":true,\"data\":{\"model\":\"DS920+\",\"firmware_ver\":\"DSM 7.2\",\"sys_temp\":41}}",
                    "load_info" => "{\"success\":true,\"data\":{\"volumes\":[{\"vol_path\":\"/volume1\",\"status\":\"normal\",\"size\":{\"total\":\"8000000000000\",\"used\":\"2000000000000\"}}],\"disks\":[{\"name\":\"Drive 1\",\"model\":\"WD80EFZZ \",\"status\":\"normal\",\"temp\":36}]}}",
                    _ => "{\"success\":true}"
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
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
