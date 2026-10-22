using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The first Owner, created in setup and signed in again, as every browser test starts.
    internal static class Owner
    {
        // The sign-in name and a strong password (12+ characters, lower and upper case, digit, special).
        public const string Username = "owner_e2e";
        public const string Password = "E2e-Owner-Password-123!";

        // The Owner is created in the real setup; the rest of the wizard is shortened to its end by marking it finished, so the test reaches the application itself.
        // The wizard has its own tests (SetupFlowTests).
        public static async Task SetUpAndSignInAsync(IPage page)
        {
            await CreateAsync(page);
            await JularrInstance.Latest!.RunSqlAsync("UPDATE \"InstanceSetupState\" SET \"CompletedAt\" = now(), \"SetupStepTypeId\" = 12");
            await Steps.SignOutAndInAsync(page);
        }

        // Creates the Owner at the first visit and stays in the setup, signed in.
        public static async Task CreateAsync(IPage page)
        {
            await page.GotoAsync("/");
            await page.WaitForURLAsync("**/Setup**");
            await page.FillAsync("[name=Username]", Username);
            await page.FillAsync("[name=ProfileName]", "Owner");
            await page.FillAsync("[name=Password]", Password);
            await page.FillAsync("[name=PasswordConfirmation]", Password);
            await page.Locator("form button[type=submit]").Last.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Setup");
        }
    }
}
