using System.Net;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TrustedProxyTests
    {
        private static readonly IPNetwork[] Proxies = [IPNetwork.Parse("10.0.0.0/8")];

        [TestMethod]
        public void ForwardedHeaders_CountOnlyFromTrustedProxies()
        {
            // Through the trusted proxy the client, scheme and host it saw count.
            var http = Request("10.0.0.2", "203.0.113.7", "https", "jularr.example.com");
            TrustedProxies.Apply(http, Proxies);
            Assert.AreEqual(IPAddress.Parse("203.0.113.7"), http.Connection.RemoteIpAddress);
            Assert.AreEqual(("https", "jularr.example.com"), (http.Request.Scheme, http.Request.Host.Value));

            // Anyone else's headers change nothing; without proxies none are trusted.
            http = Request("192.168.1.20", "203.0.113.7", "https", "jularr.example.com");
            TrustedProxies.Apply(http, Proxies);
            Assert.AreEqual(IPAddress.Parse("192.168.1.20"), http.Connection.RemoteIpAddress);
            Assert.AreEqual(("http", "localhost"), (http.Request.Scheme, http.Request.Host.Value));
            http = Request("10.0.0.2", "203.0.113.7", "https", "jularr.example.com");
            TrustedProxies.Apply(http, []);
            Assert.AreEqual(IPAddress.Parse("10.0.0.2"), http.Connection.RemoteIpAddress);

            // A port is kept; a proxy seen as an IPv4-mapped address still counts; no peer, no change.
            http = Request("::ffff:10.0.0.2", "203.0.113.7", "http", "jularr.example.com:80");
            TrustedProxies.Apply(http, Proxies);
            Assert.AreEqual(("203.0.113.7", "jularr.example.com:80"), (http.Connection.RemoteIpAddress!.ToString(), http.Request.Host.Value));
            http = Request("10.0.0.2", "203.0.113.7", "https", null);
            http.Connection.RemoteIpAddress = null;
            TrustedProxies.Apply(http, Proxies);
            Assert.AreEqual("http", http.Request.Scheme);
        }

        [TestMethod]
        public void WrongDeviceTokens_BlockTheirAddressAfterThirty()
        {
            using var failures = new DeviceTokenFailures();
            var address = DeviceTokenFailures.Key(IPAddress.Parse("2001:db8:1:2:3:4:5:6"));
            Assert.AreEqual("2001:db8:1:2::/64", address);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                Assert.IsFalse(failures.IsBlocked(address));
                failures.Fail(address);
            }

            Assert.IsTrue(failures.IsBlocked(address));
            Assert.IsFalse(failures.IsBlocked(DeviceTokenFailures.Key(IPAddress.Parse("2001:db8:1:3::1"))));
        }

        [TestMethod]
        public void ForwardedChain_StopsAtTheFirstUntrustedAddress_AndIgnoresForgedOnes()
        {
            // A client cannot claim another address in front of the proxy chain.
            var http = Request("10.0.0.2", "198.51.100.1, 203.0.113.7, 10.0.0.9", "https, https, http", null);
            TrustedProxies.Apply(http, Proxies);
            Assert.AreEqual(IPAddress.Parse("203.0.113.7"), http.Connection.RemoteIpAddress);
            Assert.AreEqual("https", http.Request.Scheme);

            // Malformed entries leave the request as it came.
            foreach (var (chain, scheme, host) in new[] { ("not-an-address", "https", "a.example"), ("203.0.113.7", "ftp", "a.example"), ("203.0.113.7", "https", "user@evil.example/x"),
                ("203.0.113.7", "https", "a.example:port") })
            {
                http = Request("10.0.0.2", chain, scheme, host);
                TrustedProxies.Apply(http, Proxies);
                Assert.AreEqual(IPAddress.Parse("10.0.0.2"), http.Connection.RemoteIpAddress);
                Assert.AreEqual("http", http.Request.Scheme);
            }
        }

        [TestMethod]
        public async Task Settings_StoreCanonicalNetworks_AndTheQrCodeUsesTheForwardedAddress()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true,
                services: services => services.AddTransient<IStartupFilter, RemoteAddressFilter>());
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""))!;
            foreach (var proxy in new[] { "proxy.local", "0.0.0.0/0" })
            {
                using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                    new UpdateInstanceSettingsDataV1("Jularr", 1, version, trustedProxies: [proxy]));
                StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "invalid_network");
            }

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                new UpdateInstanceSettingsDataV1("Jularr", 1, version, trustedProxies: ["10.0.0.1/8", " 192.168.1.5 ", "10.0.0.0/8", "::ffff:192.168.1.5"]));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("{10.0.0.0/8,192.168.1.5/32}", await host.ScalarAsync("SELECT \"TrustedProxies\"::text FROM \"InstanceSettings\""));

            // Behind the proxy the TV's QR code names the address the phone can reach.
            var code = await ShowCodeAsync(host);
            using var proxied = await LinkAsync(host, "10.0.0.2", code);
            var shown = ShownQrPath(await proxied.Content.ReadAsStringAsync());
            Assert.AreEqual(QrPath("https://jularr.example.com/Devices/Pair?code="), shown);

            // A configured public address comes first.
            using var publicUrl = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                new UpdateInstanceSettingsDataV1("Jularr", 1, version + 1, publicUrl: "https://home.example.net/jularr"));
            Assert.AreEqual(HttpStatusCode.OK, publicUrl.StatusCode, await publicUrl.Content.ReadAsStringAsync());
            using var preferred = await LinkAsync(host, "10.0.0.2", code);
            shown = ShownQrPath(await preferred.Content.ReadAsStringAsync());
            Assert.AreEqual(QrPath("https://home.example.net/jularr/Devices/Pair?code="), shown);
        }

        // The QR code's modules for an address ending in the shown code.
        private string? _code;

        private string QrPath(string address)
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(address + _code, QRCoder.QRCodeGenerator.ECCLevel.M);
            var path = new System.Text.StringBuilder();
            for (var y = 0; y < data.ModuleMatrix.Count; y++)
            {
                for (var x = 0; x < data.ModuleMatrix.Count; x++)
                {
                    if (data.ModuleMatrix[y][x]) path.Append(System.Globalization.CultureInfo.InvariantCulture, $"M{x} {y}h1v1h-1z");
                }
            }

            return path.ToString();
        }

        private string ShownQrPath(string html)
        {
            _code = Regex.Match(html, "aria-live=\"polite\">([0-9]{6})<").Groups[1].Value;
            return Regex.Match(html, "<path d=\"([^\"]+)\"").Groups[1].Value;
        }

        private static DefaultHttpContext Request(string remote, string forwardedFor, string? scheme, string? host)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost");
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (scheme is not null) http.Request.Headers["X-Forwarded-Proto"] = scheme;
            if (host is not null) http.Request.Headers["X-Forwarded-Host"] = host;
            return http;
        }

        private static async Task<string> ShowCodeAsync(AuthenticationTests.Host host)
        {
            using var page = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            using var start = await host.SendAsync(HttpMethod.Post, "/Devices/Link",
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(await page.Content.ReadAsStringAsync()) });
            Assert.AreEqual(HttpStatusCode.Redirect, start.StatusCode, await start.Content.ReadAsStringAsync());
            return host.Cookies["jularr.device_pairing"];
        }

        private static async Task<HttpResponseMessage> LinkAsync(AuthenticationTests.Host host, string remote, string pairing)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/Devices/Link");
            request.Headers.Add("Cookie", "jularr.device_pairing=" + pairing);
            request.Headers.Add("X-Test-Remote", remote);
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "jularr.example.com");
            var response = await host.Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return response;
        }

        // The test server has no network peer; this one plays the proxy's address.
        private sealed class RemoteAddressFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (http, nextMiddleware) =>
                {
                    if (http.Request.Headers["X-Test-Remote"].ToString() is { Length: > 0 } remote)
                    {
                        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                    }

                    await nextMiddleware(http);
                });
                next(app);
            };
        }
    }
}
