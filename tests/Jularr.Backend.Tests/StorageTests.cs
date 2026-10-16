using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Service.System.Storage.V1;
using Jularr.Integrations.Data.Synology;
using Jularr.Integrations.Logic.Synology;
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
            using var deniedCheck = await host.SendAsync(HttpMethod.Post, Route + "/" + id + "/check");
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedRead.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedCheck.StatusCode);
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
                ["ProtocolVersion"] = "3.1.1", ["Username"] = "jularr", ["Password"] = Password, ["IsReadOnly"] = "True", ["WakeOnDemand"] = "False",
                ["IsVolume"] = "True"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var settings = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=1");
            html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, html);
            StringAssert.Contains(html, "&lt;NAS&gt;");
            Assert.IsFalse(html.Contains(Password, StringComparison.Ordinal));
            StringAssert.Contains(html, "name=\"Password\"");

            // The share as a Docker volume, its password a variable for .env.
            StringAssert.Contains(html, "type: cifs");
            StringAssert.Contains(html, "password=${JULARR_NAS_1_PASSWORD}");
            using var nfs = await host.SendAsync(HttpMethod.Get, "/Setup/Storage?protocol=Nfs");
            Assert.AreEqual(HttpStatusCode.OK, nfs.StatusCode, await nfs.Content.ReadAsStringAsync());
            Assert.IsFalse((await nfs.Content.ReadAsStringAsync()).Contains("name=\"Password\"", StringComparison.Ordinal));

            using var browser = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            browser.DefaultRequestHeaders.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var denied = await browser.PostAsync("/Admin/Storage?handler=Check&storageMountId=1", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
        }

        [TestMethod]
        public void Volume_ComposeBlock_MountsTheShareWhereJularrLooksAndKeepsThePasswordAVariable()
        {
            var smb = new StorageMountData(7, "NAS", StorageProtocolType.Smb, "nas.test", "/Media $hare", "3.1.1", "ju\"loc", "HOME", false, null, null, null, false, 1);
            var block = StorageVolumeLogic.ComposeBlock(smb, "/mnt/jularr/");
            StringAssert.Contains(block, "      - nas-7:/mnt/jularr/7");
            StringAssert.Contains(block, "type: cifs");
            StringAssert.Contains(block, "device: \"//nas.test/Media $$hare\"");
            StringAssert.Contains(block, "o: \"username=ju\\\"loc,password=${JULARR_NAS_7_PASSWORD},domain=HOME,vers=3.1.1,uid=1654,gid=1654\"");

            var nfs = new StorageMountData(8, "NFS", StorageProtocolType.Nfs, "nas.test", "/exports/media", "4.1", null, null, true, null, null, null, false, 1);
            block = StorageVolumeLogic.ComposeBlock(nfs, "/mnt/jularr");
            StringAssert.Contains(block, "device: \":/exports/media\"");
            StringAssert.Contains(block, "o: \"addr=nas.test,nfsvers=4.1,soft,ro\"");
        }

        private static SaveStorageMountDataV1 Smb(string? password = Password, string name = "NAS", string server = "nas.test", string path = "/media", string version = "3.1.1")
        {
            return new SaveStorageMountDataV1(name, StorageProtocolType.Smb, server, path, version, "jularr", null, true, password);
        }

        [TestMethod]
        public async Task Connections_FailedChecks_CheckAgainWithBackoffNotifyOnceAndShowTheirState()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var mountId = await LibraryTests.CreateMountAsync(host);
            var share = Path.Combine(storage.Path, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(share);
            File.WriteAllText(Path.Combine(share, "movie.mkv"), "movie");

            async Task CheckDueAsync()
            {
                await host.Fixture.RunSqlAsync("UPDATE \"StorageMountStates\" SET \"NextCheckAt\" = now() - interval '1 second' WHERE \"FailedChecks\" > 0");
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CheckStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageChecksParametersV1(), default, CancellationToken.None);
            }

            async Task<StorageMountDetailsV1> ReadAsync()
            {
                using var read = await host.SendAsync(HttpMethod.Get, Route + "/" + mountId);
                return (await read.Content.ReadFromJsonAsync<StorageMountDetailsV1>())!;
            }

            // A check records what the share shows.
            using var available = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/check");
            Assert.AreEqual(HttpStatusCode.OK, available.StatusCode, await available.Content.ReadAsStringAsync());
            Assert.AreEqual(StorageAvailabilityType.Available, (await available.Content.ReadFromJsonAsync<StorageMountStatusV1>())!.StorageAvailabilityTypeId);
            Assert.AreEqual(StorageHealthType.Online, (await ReadAsync()).StorageHealthTypeId);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Storage?storageMountId=" + mountId);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "class=\"storage__health\" role=\"status\">Online");

            // Gone: asleep (it has Wake-on-LAN), checked again after a pause that grows.
            Directory.Move(share, share + "-away");
            using var away = await host.SendAsync(HttpMethod.Post, Route + "/" + mountId + "/check");
            Assert.AreEqual(HttpStatusCode.OK, away.StatusCode);
            Assert.AreEqual(StorageHealthType.Sleeping, (await ReadAsync()).StorageHealthTypeId);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"FailedChecks\" = 1 AND \"NextCheckAt\" > now() FROM \"StorageMountStates\""));
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<CheckStorageMountsV1>().ExecuteAsync<UpdateResult>(new StorageChecksParametersV1(), default, CancellationToken.None);
            }

            Assert.AreEqual(1, await host.ScalarAsync("SELECT \"FailedChecks\" FROM \"StorageMountStates\""));
            await CheckDueAsync();
            await CheckDueAsync();
            await CheckDueAsync();
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"FailedChecks\" = 4 AND \"NextCheckAt\" > now() + interval '9 minutes' FROM \"StorageMountStates\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT count(*) > 0 AND max(\"Occurrences\") = 1 FROM \"Notifications\" WHERE \"Subject\" = 'NAS'"));

            // Back: the next check finds it and the pauses end.
            Directory.Move(share + "-away", share);
            await CheckDueAsync();
            Assert.AreEqual(StorageHealthType.Online, (await ReadAsync()).StorageHealthTypeId);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"FailedChecks\" = 0 AND \"NextCheckAt\" IS NULL FROM \"StorageMountStates\""));
        }

        [TestMethod]
        public void Synology_Certificates_NeedAnAuthorityOrTheOwnersTrust()
        {
            var selfSigned = new SynologyCertificate(new string('B', 64), false);
            Assert.IsFalse(SynologyClient.Accepts(null, selfSigned));
            Assert.IsFalse(SynologyClient.Accepts(new string('C', 64), selfSigned));
            Assert.IsTrue(SynologyClient.Accepts(new string('b', 64), selfSigned));
            Assert.IsTrue(SynologyClient.Accepts(null, new SynologyCertificate(new string('D', 64), true)));
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
            using var trust = await host.SendAsync(HttpMethod.Post, route + "/certificate/trust", new TrustSynologyCertificateDataV1(new string('A', 64)));
            Assert.AreEqual(HttpStatusCode.BadRequest, trust.StatusCode);
            using var otherAddress = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://nas2.test:5000", "admin", "dsm-secret"));
            Assert.AreEqual(HttpStatusCode.OK, otherAddress.StatusCode, await otherAddress.Content.ReadAsStringAsync());
            Assert.IsTrue(await host.ScalarAsync("SELECT \"SynologyCertificateSha256\" FROM \"StorageMounts\"") is null or DBNull);
            using var back = await host.SendAsync(HttpMethod.Put, route, new UpdateStorageSynologyDataV1("http://nas.test:5000", "admin", "dsm-secret"));
            Assert.AreEqual(HttpStatusCode.OK, back.StatusCode);

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
