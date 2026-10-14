using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A TV browser and a signed-in phone, as two separate browsers.
    [TestClass]
    public sealed class DevicePairingTests
    {
        [ClassInitialize]
        public static void InstallBrowser(TestContext _)
        {
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                JularrInstance.Unavailable("Chromium for Playwright could not be installed.");
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task TvShowsCode_PhoneConfirmsTheShownDevice_TvSignsIn()
        {
            await using var jularr = await JularrInstance.StartAsync();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var phoneContext = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = jularr.Address.ToString() });
            var phone = await phoneContext.NewPageAsync();
            await Owner.SetUpAndSignInAsync(phoneContext, phone);
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
            await phone.Locator("main form button[type=submit]").Last.ClickAsync();
            StringAssert.Contains(await phone.Locator("main").InnerTextAsync(), "The device is paired");

            // The TV looks again by itself and is signed in.
            await tv.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new PageWaitForURLOptions { Timeout = 30_000 });
            Assert.AreEqual(1, await tv.Locator("main").CountAsync());
        }
    }
}
