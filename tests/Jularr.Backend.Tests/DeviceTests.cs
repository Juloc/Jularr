using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Data.User.Session.V1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DeviceTests
    {
        [TestMethod]
        public async Task Pairing_ConfirmedCode_GivesTheDeviceItsOwnSessionOnce()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            // Off by default.
            using var off = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Living room TV", "AndroidTV"));
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Living room TV", "AndroidTV"));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var pairing = (await started.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            StringAssert.Matches(pairing.Code, new Regex("^[0-9]{6}$"));
            var claimRoute = "/api/v1/devices/pairings/" + pairing.PairingId + "/claim";

            // Waiting until a signed-in profile confirms; a wrong code changes nothing.
            using var waiting = await host.SendAsync(HttpMethod.Post, claimRoute);
            Assert.IsFalse((await waiting.Content.ReadFromJsonAsync<DevicePairingClaimV1>())!.IsPaired);
            var wrongCode = pairing.Code == "000000" ? "000001" : "000000";
            using var wrong = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(wrongCode));
            StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "invalid_code");
            using var approved = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(pairing.Code));
            Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());

            // The device fetches its session once; it carries the device and the confirming profile.
            using var paired = await host.SendAsync(HttpMethod.Post, claimRoute);
            Assert.IsTrue((await paired.Content.ReadFromJsonAsync<DevicePairingClaimV1>())!.IsPaired);
            Assert.AreEqual(101L, await host.ScalarAsync("SELECT \"ActiveProfileId\" FROM \"AccountSessions\" WHERE \"DeviceName\" = 'Living room TV'"));
            using var again = await host.SendAsync(HttpMethod.Post, claimRoute);
            Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
            using var twice = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(pairing.Code));
            StringAssert.Contains(await twice.Content.ReadAsStringAsync(), "invalid_code");
            using var sessions = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions?pageSize=100");
            var device = (await sessions.Content.ReadFromJsonAsync<PageResult<SessionDetailsV1>>())!.Items.Single(session => session.DeviceName is not null);
            Assert.AreEqual(("Living room TV", "AndroidTV"), (device.DeviceName, device.DevicePlatform));

            // An expired code is gone, and guessing stops after five wrong codes, the right one included.
            using var late = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Phone", "Android"));
            var expired = (await late.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            await host.Fixture.RunSqlAsync("UPDATE \"DevicePairings\" SET \"CreatedAt\" = now() - interval '1 hour', \"ExpiresAt\" = now() - interval '1 minute'");
            using var tooLate = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(expired.Code));
            StringAssert.Contains(await tooLate.Content.ReadAsStringAsync(), "invalid_code");
            using var fresh = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Tablet", "iPadOS"));
            var open = (await fresh.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var guess = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(open.Code == "000000" ? "000001" : "000000"));
            }

            using var blocked = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(open.Code));
            StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "device_pairing_attempts");
        }

        [TestMethod]
        public async Task LinkPage_PairsOnlyTheBrowserThatAskedAfterThePhoneSawTheDevice()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var off = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            Assert.AreEqual(HttpStatusCode.NotFound, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            // The TV asks for a code; it stays in a strict cookie, never in the address.
            using var page = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            var token = LibraryTests.Token(await page.Content.ReadAsStringAsync());
            using var start = await host.SendAsync(HttpMethod.Post, "/Devices/Link", new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            Assert.AreEqual(HttpStatusCode.Redirect, start.StatusCode, await start.Content.ReadAsStringAsync());
            StringAssert.Contains(string.Join(";", start.Headers.GetValues("Set-Cookie")), "samesite=strict");
            using var shown = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            var html = await shown.Content.ReadAsStringAsync();
            var code = Regex.Match(html, "aria-live=\"polite\">([0-9]{6})<").Groups[1].Value;
            Assert.AreEqual(6, code.Length, html);
            StringAssert.Contains(html, "<svg");
            Assert.AreEqual("5", shown.Headers.GetValues("Refresh").Single());

            // The phone sees which device asks before confirming it.
            using var form = await host.SendAsync(HttpMethod.Get, "/Devices/Pair?code=" + code);
            var pairHtml = await form.Content.ReadAsStringAsync();
            StringAssert.Contains(pairHtml, "Browser (Web) asks to be paired");
            using var approved = await host.SendAsync(HttpMethod.Put, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(code));
            Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());

            // Another browser without that cookie gets nothing; the asking one is signed in.
            var cookie = host.Cookies["jularr.device_pairing"];
            host.Cookies.Remove("jularr.device_pairing");
            using var stranger = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            Assert.AreEqual(HttpStatusCode.OK, stranger.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"DevicePlatform\" = 'Web'"));
            host.Cookies["jularr.device_pairing"] = cookie;
            using var signedIn = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.AreEqual("/", signedIn.Headers.Location!.OriginalString);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"DevicePlatform\" = 'Web'"));
        }
    }
}
