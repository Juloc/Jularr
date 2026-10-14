using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The first Owner, created in setup and signed in again, as every browser test starts.
    internal static class Owner
    {
        public const string Email = "owner@example.test";
        public const string Password = "e2e-owner-password-123";

        public static async Task SetUpAndSignInAsync(IPage page)
        {
            await page.GotoAsync("/");
            await page.WaitForURLAsync("**/Setup**");
            await page.FillAsync("[name=Email]", Email);
            await page.FillAsync("[name=DisplayName]", "Owner");
            await page.FillAsync("[name=Password]", Password);
            await page.FillAsync("[name=PasswordConfirmation]", Password);
            await page.Locator("form button[type=submit]").Last.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Setup");

            await page.GotoAsync("/Account/Logout");
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Sign out"));
            await page.GotoAsync("/Account/Login");
            await page.FillAsync("[name=Email]", Email);
            await page.FillAsync("[name=Password]", Password);
            await page.Locator("form button[type=submit]").First.ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
        }
    }
}
