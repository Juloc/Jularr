using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Service.Core;

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
            using var wrong = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(wrongCode));
            StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "invalid_code");
            using var approved = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(pairing.Code));
            Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());

            // The device fetches its session once, as a token instead of a cookie; it carries the device and the confirming profile.
            using var paired = await host.SendAsync(HttpMethod.Post, claimRoute);
            Assert.IsTrue((await paired.Content.ReadFromJsonAsync<DevicePairingClaimV1>())!.IsPaired);
            Assert.IsFalse(paired.Headers.Contains("Set-Cookie"));
            var token = paired.Headers.GetValues(ServiceApi.DeviceTokenHeader).Single();
            Assert.AreEqual(101L, await host.ScalarAsync("SELECT \"ActiveProfileId\" FROM \"AccountSessions\" WHERE \"DeviceName\" = 'Living room TV'"));

            using var again = await host.SendAsync(HttpMethod.Post, claimRoute);
            Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
            using var twice = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(pairing.Code));
            StringAssert.Contains(await twice.Content.ReadAsStringAsync(), "invalid_code");
            using var sessions = await host.SendAsync(HttpMethod.Get, "/api/v1/sessions?pageSize=100");
            var device = (await sessions.Content.ReadFromJsonAsync<PageResult<SessionDetailsV1>>())!.Items.Single(session => session.DeviceName is not null);
            Assert.AreEqual(("Living room TV", "AndroidTV"), (device.DeviceName, device.DevicePlatform));

            // The app uses its token alone, without cookies or antiforgery token, as a normal user of the profile.
            using var me = await host.SendAsAppAsync(HttpMethod.Get, "/api/v1/auth", token);
            var auth = (await me.Content.ReadFromJsonAsync<AuthenticationResultV1>())!;
            Assert.AreEqual(101L, auth.ProfileId);
            CollectionAssert.DoesNotContain(auth.Permissions.ToArray(), "admin.access");
            using var renamed = await host.SendAsAppAsync(HttpMethod.Put, "/api/v1/sessions/" + device.Id + "/device", token, new RenameSessionDeviceDataV1("Bedroom TV"));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            using var admin = await host.SendAsAppAsync(HttpMethod.Get, "/api/v1/admin/providers", token);
            Assert.IsFalse(admin.IsSuccessStatusCode);

            // Only device sessions sign in by token; a revoked device is out at once.
            using var forged = await host.SendAsAppAsync(HttpMethod.Get, "/api/v1/sessions", token[..^1] + (token[^1] == 'A' ? "B" : "A"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"DeviceName\" = NULL, \"DevicePlatform\" = NULL WHERE \"DeviceName\" = 'Bedroom TV'");
            using var browserSession = await host.SendAsAppAsync(HttpMethod.Get, "/api/v1/sessions", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, browserSession.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"DeviceName\" = 'Bedroom TV', \"DevicePlatform\" = 'AndroidTV' WHERE \"PublicId\" = '" + device.Id + "'");
            using var revoked = await host.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + device.Id);
            Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync());
            using var gone = await host.SendAsAppAsync(HttpMethod.Get, "/api/v1/sessions", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, gone.StatusCode);

            // An expired code is gone, and guessing stops after five wrong codes, the right one included.
            using var late = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Phone", "Android"));
            var expired = (await late.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            await host.Fixture.RunSqlAsync("UPDATE \"DevicePairings\" SET \"CreatedAt\" = now() - interval '1 hour', \"ExpiresAt\" = now() - interval '1 minute'");
            using var tooLate = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(expired.Code));
            StringAssert.Contains(await tooLate.Content.ReadAsStringAsync(), "invalid_code");
            using var fresh = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Tablet", "iPadOS"));
            var open = (await fresh.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var guess = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(open.Code == "000000" ? "000001" : "000000"));
            }

            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(open.Code));
            StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "device_pairing_attempts");

            // The attempts are kept in the database, so a restart does not forget them.
            Assert.AreEqual(5L, await host.ScalarAsync("SELECT count(*) FROM \"DevicePairingAttempts\""));

            // An optional public address for QR codes, stored like a provider address.
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings",
                new Jularr.Backend.Data.Admin.Instance.V1.UpdateInstanceSettingsDataV1("Jularr", 1, version, publicUrl: "https://jularr.example.test/home"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var presentation = await host.SendAsync(HttpMethod.Get, "/api/v1/instance/presentation");
            Assert.AreEqual("https://jularr.example.test/home/",
                (await presentation.Content.ReadFromJsonAsync<Jularr.Backend.Data.Public.Instance.V1.InstancePresentationDetailsV1>())!.PublicUrl);
        }

        [TestMethod]
        public async Task LinkPage_PairsOnlyTheBrowserThatAskedAfterThePhoneSawTheDevice()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            using var off = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            Assert.AreEqual(HttpStatusCode.NotFound, off.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            // A pairing named in the address counts for nothing.
            using var forged = await host.SendAsync(HttpMethod.Get, "/Devices/Link?pairing=" + Guid.NewGuid() + "&code=123456");
            StringAssert.Contains(await forged.Content.ReadAsStringAsync(), "Show a pairing code");
            Assert.IsFalse(forged.Headers.Contains("Refresh"));

            // The TV asks for a code; it stays in a strict cookie, never in the address.
            using var page = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            var token = LibraryTests.Token(await page.Content.ReadAsStringAsync());
            using var start = await host.SendAsync(HttpMethod.Post, "/Devices/Link", new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
            Assert.AreEqual(HttpStatusCode.Redirect, start.StatusCode, await start.Content.ReadAsStringAsync());
            StringAssert.Contains(string.Join(";", start.Headers.GetValues("Set-Cookie")), "samesite=strict");
            // A browser cookie, so an expired code is still found and replaced.
            Assert.IsFalse(string.Join(";", start.Headers.GetValues("Set-Cookie")).Contains("expires=", StringComparison.OrdinalIgnoreCase));
            using var shown = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            var html = await shown.Content.ReadAsStringAsync();
            var code = Regex.Match(html, "aria-live=\"polite\">([0-9]{6})<").Groups[1].Value;
            Assert.AreEqual(6, code.Length, html);
            StringAssert.Contains(html, "<svg");
            Assert.AreEqual("5", shown.Headers.GetValues("Refresh").Single());

            // An expired code is replaced by a new one at once; the old one ends.
            await host.Fixture.RunSqlAsync("UPDATE \"DevicePairings\" SET \"CreatedAt\" = now() - interval '1 hour', \"ExpiresAt\" = now() - interval '1 minute'");
            using var renewed = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            Assert.AreEqual(HttpStatusCode.Redirect, renewed.StatusCode);
            using var fresh = await host.SendAsync(HttpMethod.Get, "/Devices/Link");
            code = Regex.Match(await fresh.Content.ReadAsStringAsync(), "aria-live=\"polite\">([0-9]{6})<").Groups[1].Value;
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"DevicePairings\""));

            // Opening the QR address shows the device and its code; it does not pair by itself.
            using var form = await host.SendAsync(HttpMethod.Get, "/Devices/Pair?code=" + code);
            var pairHtml = await form.Content.ReadAsStringAsync();
            StringAssert.Contains(pairHtml, "Browser (Web) asks to be paired");
            StringAssert.Contains(pairHtml, ">" + code + "<");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"DevicePairings\" WHERE \"ApprovedAccountId\" IS NOT NULL"));
            using var approved = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(code));
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

            // The device is a normal user of the profile: no administration, no further pairings; it may rename itself.
            using var admin = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/providers");
            Assert.AreEqual(HttpStatusCode.Unauthorized, admin.StatusCode);
            using var further = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1("123456"));
            Assert.AreEqual(HttpStatusCode.Forbidden, further.StatusCode);
            var deviceSession = (Guid)(await host.ScalarAsync("SELECT \"PublicId\" FROM \"AccountSessions\" WHERE \"DevicePlatform\" = 'Web'"))!;
            using var renamed = await host.SendAsync(HttpMethod.Put, "/api/v1/sessions/" + deviceSession + "/device", new RenameSessionDeviceDataV1("Living room TV"));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            Assert.AreEqual("Living room TV", await host.ScalarAsync("SELECT \"DeviceName\" FROM \"AccountSessions\" WHERE \"DevicePlatform\" = 'Web'"));

            // Paired to the Owner, its requests still wait for approval.
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (101, 1, 'Device request')");
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/101/requests", new Jularr.Backend.Data.User.Request.V1.CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"AcquisitionRequestStatusTypeId\" FROM \"AcquisitionRequests\""));
        }
    }
}
