using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Logic.Network;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class VpnTunnelTests
    {
        private const string Key = "yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=";

        private const string Profile = """
            [Interface]
            PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
            Address = 10.66.0.2/32, fd00::2/128
            DNS = 10.66.0.1

            [Peer]
            PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
            Endpoint = vpn.example.test:51820
            AllowedIPs = 0.0.0.0/0, ::/0
            """;

        [TestMethod]
        public async Task Socks5_ConnectsThroughTheProxyWithTheHostName_AndRelaysData()
        {
            using var target = new TcpListener(IPAddress.Loopback, 0);
            target.Start();
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            await using var proxy = new FakeSocks5("user", "secret", targetPort);

            using var socket = await Socks5Connector.ConnectAsync(proxy.EndPoint, "user", "secret", "tracker.example.test", 6969, CancellationToken.None);
            using var accepted = await target.AcceptTcpClientAsync();
            await socket.SendAsync("ping"u8.ToArray(), SocketFlags.None);
            var buffer = new byte[4];
            await accepted.GetStream().ReadExactlyAsync(buffer);

            Assert.AreEqual("ping", Encoding.ASCII.GetString(buffer));
            Assert.AreEqual("tracker.example.test:6969", proxy.Requested, "The name goes to the proxy unresolved.");
        }

        [TestMethod]
        public async Task Socks5_ProxyDown_FailsWithoutTouchingTheTarget()
        {
            using var target = new TcpListener(IPAddress.Loopback, 0);
            target.Start();
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var closed = FreeEndPoint();

            await Assert.ThrowsAsync<SocketException>(() => Socks5Connector.ConnectAsync(closed, "user", "secret", "127.0.0.1", targetPort, CancellationToken.None));

            // No direct fallback: the target never sees a connection.
            Assert.IsFalse(target.Pending());
        }

        [TestMethod]
        public async Task Socks5_WrongSignIn_IsRefused()
        {
            await using var proxy = new FakeSocks5("user", "secret", 1);

            await Assert.ThrowsAsync<IOException>(() => Socks5Connector.ConnectAsync(proxy.EndPoint, "user", "wrong", "example.test", 80, CancellationToken.None));
        }

        [TestMethod]
        public void Configuration_KeepsTheProfileAndOpensOnlyTheLoopbackDoor()
        {
            var socks = new IPEndPoint(IPAddress.Loopback, 40000);

            var text = WireproxyTunnel.BuildConfiguration(Profile, socks, "u1", "p1", "1.1.1.1", 20);

            var lines = text.Split('\n');
            var check = Array.IndexOf(lines, "CheckAlive = 1.1.1.1");
            Assert.IsTrue(check > Array.IndexOf(lines, "[Interface]") && check < Array.IndexOf(lines, "[Peer]"), "The pings belong to the interface.");
            StringAssert.Contains(text, "PrivateKey = " + Key);
            StringAssert.Contains(text, "[Socks5]\nBindAddress = 127.0.0.1:40000\nUsername = u1\nPassword = p1\n");
            Assert.IsFalse(text.Contains("TunnelDomains", StringComparison.OrdinalIgnoreCase), "Domain rules would dial around the tunnel.");
            Assert.IsFalse(text.Contains("[http]", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void Counters_ReadHandshakeAndBytes()
        {
            var counters = WireproxyTunnel.ParseCounters("private_key=REDACTED\npublic_key=abc\nlast_handshake_time_sec=1761650000\nlast_handshake_time_nsec=5\nrx_bytes=1200\ntx_bytes=800\n");

            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1761650000), counters.HandshakeAt);
            Assert.AreEqual(1200L, counters.ReceivedBytes);
            Assert.AreEqual(800L, counters.SentBytes);
            Assert.IsNull(WireproxyTunnel.ParseCounters("last_handshake_time_sec=0\n").HandshakeAt);
        }

        [TestMethod]
        public async Task Profiles_ModuleOff_AreUnavailable()
        {
            await using var host = await Host.CreateAsync(library: true);
            await SignInAsync(host);

            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/network/vpn/profiles");

            Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Vpn'"));
        }

        [TestMethod]
        public async Task Profiles_ImportCheckDelete_KeepTheKeysAndFailClosedWithoutTheHelper()
        {
            await using var host = await Host.CreateAsync(library: true, settings: new Dictionary<string, string> { ["Vpn:WireproxyPath"] = "jularr-missing-wireproxy" });
            await SignInAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Vpn'");

            // Unsupported directives and a profile without DNS are refused.
            using (var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", Import("Bad", Profile.Replace("DNS = 10.66.0.1", "PostUp = curl example.test"))))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "invalid_wireguard_configuration");
            }

            using (var noDns = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", Import("No DNS", Profile.Replace("DNS = 10.66.0.1\n", ""))))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, noDns.StatusCode);
                StringAssert.Contains(await noDns.Content.ReadAsStringAsync(), "wireguard_dns_required");
            }

            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", Import("Home", Profile, "wg0.conf"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

            // The key is stored protected and never returned.
            var stored = (byte[])(await host.ScalarAsync("SELECT \"ProtectedConfiguration\" FROM \"VpnProfiles\" WHERE \"Id\" = " + id))!;
            Assert.IsFalse(Encoding.UTF8.GetString(stored).Contains(Key, StringComparison.Ordinal));
            using var list = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/network/vpn/profiles");
            var body = await list.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(Key, StringComparison.Ordinal));
            var profile = JsonDocument.Parse(body).RootElement[0];
            Assert.AreEqual("vpn.example.test", profile.GetProperty("endpointHost").GetString());
            Assert.IsTrue(profile.GetProperty("hasIpv6Route").GetBoolean());
            Assert.AreEqual((int)VpnTunnelPhase.Stopped, profile.GetProperty("tunnel").GetProperty("phase").GetInt32());

            // Without its helper the tunnel fails and claims nothing.
            using var check = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles/" + id + "/check");
            var tunnel = await check.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual((int)VpnTunnelPhase.Failed, tunnel.GetProperty("phase").GetInt32());
            Assert.AreEqual("vpn_helper_missing", tunnel.GetProperty("errorKey").GetString());
            Assert.AreEqual(JsonValueKind.Null, tunnel.GetProperty("exitAddress").ValueKind);

            using var duplicate = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", Import("Home", Profile));
            StringAssert.Contains(await duplicate.Content.ReadAsStringAsync(), "duplicate_value");

            using var deleted = await host.SendAsync(HttpMethod.Delete, "/api/v1/admin/network/vpn/profiles/" + id + "?version=1");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"VpnProfiles\""));
        }

        [TestMethod]
        public async Task Page_ShowsTheProfileWithoutKeysAndItsMenuEntryOnlyWithTheModule()
        {
            await using var host = await Host.CreateAsync(library: true, razor: true);
            await SignInAsync(host);
            using (var off = await host.SendAsync(HttpMethod.Get, "/Admin/Dashboard"))
            {
                Assert.IsFalse((await off.Content.ReadAsStringAsync()).Contains("href=\"/Admin/Network\"", StringComparison.Ordinal));
            }

            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Vpn'");
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/network/vpn/profiles", Import("Home", Profile, "wg0.conf"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Network");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "vpn.example.test:51820");
            StringAssert.Contains(html, "Not running");
            StringAssert.Contains(html, "href=\"/Admin/Network\"");
            Assert.IsFalse(html.Contains(Key, StringComparison.Ordinal));
        }

        // A wg0.conf as an uploaded file or as pasted text.
        private static MultipartFormDataContent Import(string name, string configuration, string? fileName = null)
        {
            var form = new MultipartFormDataContent { { new StringContent(name), "Name" } };
            if (fileName is null) form.Add(new StringContent(configuration), "Configuration");
            else form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(configuration)), "File", fileName);
            return form;
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }

        private static IPEndPoint FreeEndPoint()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return (IPEndPoint)listener.LocalEndpoint;
        }

        // A SOCKS5 proxy with a sign-in that connects every request to one local port and remembers what was asked for.
        private sealed class FakeSocks5 : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _loop;

            public FakeSocks5(string user, string password, int targetPort)
            {
                _listener.Start();
                _loop = Task.Run(async () =>
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var greeting = new byte[3];
                    await stream.ReadExactlyAsync(greeting, _stop.Token);
                    await stream.WriteAsync(new byte[] { 5, 2 }, _stop.Token);
                    var head = new byte[2];
                    await stream.ReadExactlyAsync(head, _stop.Token);
                    var name = new byte[head[1]];
                    await stream.ReadExactlyAsync(name, _stop.Token);
                    var length = new byte[1];
                    await stream.ReadExactlyAsync(length, _stop.Token);
                    var secret = new byte[length[0]];
                    await stream.ReadExactlyAsync(secret, _stop.Token);
                    var ok = Encoding.UTF8.GetString(name) == user && Encoding.UTF8.GetString(secret) == password;
                    await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) }, _stop.Token);
                    if (!ok) return;

                    var request = new byte[5];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    var host = new byte[request[4]];
                    await stream.ReadExactlyAsync(host, _stop.Token);
                    var port = new byte[2];
                    await stream.ReadExactlyAsync(port, _stop.Token);
                    Requested = Encoding.ASCII.GetString(host) + ":" + BinaryPrimitives.ReadUInt16BigEndian(port);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, targetPort, _stop.Token);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, _stop.Token);
                    var relay = new byte[64];
                    var read = await stream.ReadAsync(relay, _stop.Token);
                    await upstream.GetStream().WriteAsync(relay.AsMemory(0, read), _stop.Token);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                });
            }

            public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
            public string? Requested { get; private set; }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                try
                {
                    await _loop;
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException)
                {
                }
            }
        }
    }
}
