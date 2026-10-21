using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A visitor asks for an account, the Owner enables it; the new user requests a film, waits for approval and sees it arrive, without administration.
    [TestClass]
    public sealed class ProfilesTests
    {
        private const string Email = "viewer@example.test";
        private const string Password = "e2e-viewer-password-123";

        [TestMethod]
        [Timeout(600_000)]
        public async Task Registration_NeedsTheOwner_RequestsNeedApproval_AdministrationStaysClosed()
        {
            using var folders = new Folders("Movies");
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040,
                await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm")));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var owner = await BrowserSession.StartAsync(jularr);
            await using var viewer = await BrowserSession.StartAsync(jularr);
            var page = owner.Page;
            var visitor = viewer.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040");

                // Without the Registration module the sign-in page offers no account and the page does not exist.
                await visitor.GotoAsync("/Account/Login");
                Assert.AreEqual(0, await visitor.GetByRole(AriaRole.Link, new() { Name = "Create an account" }).CountAsync());
                Assert.AreEqual(404, (await visitor.GotoAsync("/Account/Register"))!.Status);
                CollectionAssert.AreEqual(Array.Empty<string>(), viewer.Problems.Where(problem => !problem.Contains("404", StringComparison.Ordinal)).ToArray());
                viewer.Problems.Clear();
                await Steps.SetModuleAsync(page, "Let visitors ask for an account", true);

                // The visitor asks for an account; it cannot sign in before the Owner enables it.
                await visitor.GotoAsync("/Account/Login");
                await visitor.GetByRole(AriaRole.Link, new() { Name = "Create an account" }).ClickAsync();
                var form = Steps.FormWith(visitor, "Ask for an account");
                await form.Locator("[name=Email]").FillAsync(Email);
                await form.Locator("[name=DisplayName]").FillAsync("Viewer");
                await form.Locator("[name=Password]").FillAsync(Password);
                await form.Locator("[name=PasswordConfirmation]").FillAsync(Password + "-typo");
                await form.Locator("button[type=submit]").ClickAsync();
                await Assertions.Expect(visitor.Locator("main [role=alert]")).ToBeVisibleAsync(new() { Timeout = 30_000 });
                Assert.AreEqual("", await form.Locator("[name=Password]").InputValueAsync());
                Assert.AreEqual(Email, await form.Locator("[name=Email]").InputValueAsync());
                await form.Locator("[name=Password]").FillAsync(Password);
                await form.Locator("[name=PasswordConfirmation]").FillAsync(Password);
                await Steps.SubmitAsync(visitor, form);
                await Assertions.Expect(visitor.Locator("main [role=status]")).ToContainTextAsync("waits until an administrator enables it");
                await SignInAsync(visitor);
                Assert.IsTrue(new Uri(visitor.Url).AbsolutePath.StartsWith("/Account/Login", StringComparison.Ordinal), "A pending account signed in.");

                await page.GotoAsync("/Admin/Accounts");
                await Steps.FollowAsync(page, page.Locator("tr", new() { HasText = Email }).GetByRole(AriaRole.Link, new() { Name = "Edit" }));
                var account = page.Locator("main form", new() { Has = page.Locator("[name=IsEnabled]") });
                await account.Locator("[name=IsEnabled]").SelectOptionAsync("True");
                await Steps.SubmitAsync(page, account);

                // Enabled, the same credentials work; administration stays closed.
                await SignInAsync(visitor);
                Assert.IsFalse(new Uri(visitor.Url).AbsolutePath.StartsWith("/Account/Login", StringComparison.Ordinal), "The enabled account could not sign in.");
                // Only the refused confirmation (400) and the pending sign-in (401) answered with errors.
                CollectionAssert.AreEqual(Array.Empty<string>(), viewer.Problems.Where(problem =>
                    !(problem.Contains("/Account/Register", StringComparison.Ordinal) && problem.Contains("400", StringComparison.Ordinal))
                    && !(problem.Contains("/Account/Login", StringComparison.Ordinal) && problem.Contains("401", StringComparison.Ordinal))).ToArray(),
                    string.Join(Environment.NewLine, viewer.Problems));
                var admin = await visitor.GotoAsync("/Admin/Accounts");
                Assert.AreEqual(403, admin!.Status);
                StringAssert.Contains(new Uri(visitor.Url).AbsolutePath, "/Account/AccessDenied");
                viewer.Problems.Clear();

                // The user's request waits for the Owner; once approved it is searched, downloaded and arrives.
                await Steps.RequestAsync(visitor, "Bunny", FakeServices.MovieTitle);
                await visitor.GotoAsync("/Requests");
                await Assertions.Expect(visitor.Locator(".request-row", new() { HasText = FakeServices.MovieTitle })).ToContainTextAsync("Waiting for approval");
                await page.GotoAsync("/Admin/Requests");
                await Steps.SubmitAsync(page, page.Locator("tr", new() { HasText = FakeServices.MovieTitle }).Locator("form")
                    .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Approve" }) }));
                fakes.Offer(film);
                await Steps.OpenSearchAsync(page, FakeServices.MovieTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, film);
                await Steps.WaitForAvailableAsync(page, FakeServices.MovieTitle);
                await visitor.GotoAsync("/Requests");
                await Assertions.Expect(visitor.Locator(".request-row", new() { HasText = FakeServices.MovieTitle })).ToContainTextAsync("Available");

                CollectionAssert.AreEqual(Array.Empty<string>(), owner.Problems.ToArray());
                CollectionAssert.AreEqual(Array.Empty<string>(), viewer.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "profiles-owner");
                await Evidence.SaveAsync(visitor, "profiles-viewer");
                throw;
            }
        }

        private static async Task SignInAsync(IPage page)
        {
            await page.GotoAsync("/Account/Login");
            await page.FillAsync("[name=Email]", Email);
            await page.FillAsync("[name=Password]", Password);
            await page.Locator("form button[type=submit]").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
    }
}
