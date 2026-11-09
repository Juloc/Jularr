using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The administration pages that no other flow opens: each loads for the Owner in a real browser, under its own title, with nothing failing in the page.
    [TestClass]
    public sealed class AdminPageSmokeTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task NetworkBackupLibraryFilesAndRules_Load_UnderTheirTitles()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await jularr.RunSqlAsync("""UPDATE "Providers" SET "IsEnabled" = FALSE WHERE "ProtectedSecret" IS NULL;""");
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("""UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Library', 'Requests', 'Vpn');""");

                foreach (var (path, title) in new[] { ("/Admin/Network", "VPN"), ("/Admin/Backup", "Backups"), ("/Admin/Library/Files", "All files"), ("/Admin/Rules", "Rules") })
                {
                    var response = await page.GotoAsync(path);
                    Assert.AreEqual(200, response?.Status, path);
                    await Assertions.Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(path) + "$"));
                    await Assertions.Expect(page).ToHaveTitleAsync(new Regex("^" + Regex.Escape(title) + " "));
                    await Assertions.Expect(page.Locator("main")).ToBeVisibleAsync();
                    await Evidence.SaveAsync(page, "smoke-" + path.Trim('/').Replace('/', '-'));
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-admin-page-smoke");
                throw;
            }
        }
    }
}
