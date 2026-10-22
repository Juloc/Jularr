using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The first Owner, created in setup and signed in again, as every browser test starts.
    internal static class Owner
    {
        // The sign-in name and a strong password (12+ characters, lower and upper case, digit, special).
        public const string Username = "owner_e2e";
        public const string Password = "E2e-Owner-Password-123!";

        public static async Task SetUpAndSignInAsync(IPage page)
        {
            await page.GotoAsync("/");
            await page.WaitForURLAsync("**/Setup**");
            await page.FillAsync("[name=Username]", Username);
            await page.FillAsync("[name=ProfileName]", "Owner");
            await page.FillAsync("[name=Password]", Password);
            await page.FillAsync("[name=PasswordConfirmation]", Password);
            await page.Locator("form button[type=submit]").Last.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Setup");

            await Steps.SignOutAndInAsync(page);
        }
    }
}
