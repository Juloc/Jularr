using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A TV browser and a signed-in phone, as two separate browsers.
    [TestClass]
    public sealed class DevicePairingTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task TvShowsCode_PhoneConfirmsTheShownDevice_TvSignsIn()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var phoneContext = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var phone = await phoneContext.NewPageAsync();
            await Owner.SetUpAndSignInAsync(phone);
            await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            // The TV asks for a code and shows it with a QR code.
            var tvContext = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var tv = await tvContext.NewPageAsync();
            await tv.GotoAsync("/Devices/Link");
            await tv.Locator("form button[type=submit]").Last.ClickAsync();
            var code = (await tv.Locator("[aria-live=polite]").InnerTextAsync()).Trim();
            StringAssert.Matches(code, new System.Text.RegularExpressions.Regex("^[0-9]{6}$"));
            Assert.AreEqual(1, await tv.Locator("main svg[role=img]").CountAsync());

            // Opening the QR address shows device, platform and code; only the button pairs.
            await phone.GotoAsync("/Devices/Pair?code=" + code);
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), "Browser (Web) asks to be paired");
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), code);
            await Steps.FollowAsync(phone, phone.Locator("main form button[type=submit]").Last);
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), "The device is paired");

            // The TV looks again by itself and is signed in.
            await tv.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new PageWaitForURLOptions { Timeout = 30_000 });
            Assert.AreEqual(1, await tv.Locator("main").CountAsync());
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task AppPairsByApi_PhoneConfirms_AppUsesItsTokenWithoutCookies()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var phoneContext = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var phone = await phoneContext.NewPageAsync();
            await Owner.SetUpAndSignInAsync(phone);
            await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");

            // The app has no browser: it asks for a code over the API.
            await using var app = await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions { BaseURL = jularr.Address.ToString() });
            var started = await app.PostAsync("/api/v1/devices/pairings", new APIRequestContextOptions { DataObject = new { deviceName = "Living room TV", devicePlatform = "AndroidTV" } });
            Assert.AreEqual(200, started.Status, await started.TextAsync());
            var pairing = (await started.JsonAsync())!.Value;
            var code = pairing.GetProperty("code").GetString()!;
            var claim = "/api/v1/devices/pairings/" + pairing.GetProperty("pairingId").GetString() + "/claim";

            // The phone sees the device and confirms it.
            await phone.GotoAsync("/Devices/Pair?code=" + code);
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), "Living room TV (AndroidTV) asks to be paired");
            await Steps.FollowAsync(phone, phone.Locator("main form button[type=submit]").Last);
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), "The device is paired");

            // The app fetches its token once and signs in with it alone.
            var paired = await app.PostAsync(claim);
            Assert.IsTrue((await paired.JsonAsync())!.Value.GetProperty("isPaired").GetBoolean());
            Assert.IsFalse(paired.Headers.ContainsKey("set-cookie"));
            var token = paired.Headers["jularr-device-token"];
            var me = await app.GetAsync("/api/v1/auth", new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token } });
            Assert.AreEqual(200, me.Status);
            Assert.AreNotEqual(System.Text.Json.JsonValueKind.Null, (await me.JsonAsync())!.Value.GetProperty("profileId").ValueKind);
            var admin = await app.GetAsync("/api/v1/admin/providers", new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token } });
            Assert.IsFalse(admin.Ok);
        }
    }
}
