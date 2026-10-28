using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    // Jularr's own torrent engine as a download client: off it costs a module read and nothing else; on it never runs without a verified tunnel.
    [TestClass]
    public sealed class TorrentClientTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";

        [TestMethod]
        public async Task Engine_OffIdles_OnFailsClosedWithoutAVerifiedTunnel_AndProvesItsLock()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true, settings: new Dictionary<string, string> { ["Vpn:WireproxyPath"] = "jularr-missing-wireproxy" });
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Acquisition'");

            // Off: the cycle finds no work and the worker sleeps until a grab; the page offers no engine.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var cycle = await scope.ServiceProvider.GetRequiredService<DownloadTorrentsV1>().ExecuteAsync<TorrentCycleResultV1>(new AcquisitionCycleParametersV1(), default);
                Assert.IsFalse(cycle.HasJobs || cycle.Progressed);
            }

            using (var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Downloads"))
            {
                Assert.IsFalse((await page.Content.ReadAsStringAsync()).Contains("Add torrent engine", StringComparison.Ordinal));
            }

            using (var engineOff = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/torrent/engine"))
            {
                Assert.AreNotEqual(HttpStatusCode.OK, engineOff.StatusCode);
            }

            // On: the engine is a client without address or key.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('Vpn', 'Torrent')");
            using (var withAddress = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("Engine", AcquisitionConnectionType.Torrent, "https://engine.example.test", null, true, 50, [], null, null, null)))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, withAddress.StatusCode);
            }

            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Engine", AcquisitionConnectionType.Torrent, null, null, true, 50, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

            // Without a profile, and with one whose tunnel cannot start, the test reports no tunnel; nothing falls back to the plain network.
            using (var noProfile = await host.SendAsync(HttpMethod.Post, Connections + "/" + id + "/test"))
            {
                Assert.AreEqual(HttpStatusCode.OK, noProfile.StatusCode, await noProfile.Content.ReadAsStringAsync());
                var result = await noProfile.Content.ReadFromJsonAsync<JsonElement>();
                Assert.IsFalse(result.GetProperty("isAvailable").GetBoolean());
                Assert.AreEqual("vpn_unavailable", result.GetProperty("errorKey").GetString());
            }

            var profile = new MultipartFormDataContent
            {
                { new StringContent("Home"), "Name" },
                { new StringContent("[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.66.0.2/32\nDNS = 10.66.0.1\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nEndpoint = vpn.example.test:51820\nAllowedIPs = 0.0.0.0/0\n"), "Configuration" }
            };
            using (var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", profile))
            {
                Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            }

            using (var failedTunnel = await host.SendAsync(HttpMethod.Post, Connections + "/" + id + "/test"))
            {
                Assert.AreEqual("vpn_unavailable", (await failedTunnel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorKey").GetString());
            }

            // The engine proves its lock on every read and has sent no file data.
            using var engine = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/torrent/engine");
            Assert.AreEqual(HttpStatusCode.OK, engine.StatusCode, await engine.Content.ReadAsStringAsync());
            var status = await engine.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(status.GetProperty("isLockVerified").GetBoolean());
            Assert.AreEqual(0L, status.GetProperty("sentPieceBytes").GetInt64());
            Assert.IsFalse(status.GetProperty("hasVerifiedTunnel").GetBoolean());

            // The page offers the engine and shows its measured lock.
            using var on = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Downloads");
            var html = await on.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, on.StatusCode, html);
            StringAssert.Contains(html, "Add torrent engine");
            StringAssert.Contains(html, "Proven now: no file data can leave");
        }
    }
}
