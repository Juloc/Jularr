using Jularr.Backend.Data.User.Session.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Session.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account
{
    public sealed class LogoutModel(LogoutV1 logout, IAntiforgery antiforgery) : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("account.logout.title");
            page.Asset("Pages/Settings.entry.scss");
            page.Content.Add(new UiSection("account.logout.title", [
                new Form<SessionParametersV1>(form => form.Submit("account.logout.submit"), Url.Page("/Account/Logout"), antiforgery.GetAndStoreTokens(HttpContext).RequestToken, error: Error)
            ]));
        }

        public IActionResult OnGet()
        {
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Clear the committed session only.
            var result = await logout.ExecuteAsync<LogoutResultV1>(cancellationToken: HttpContext.RequestAborted);
            await ServiceApi.ApplyAuthenticationAsync(HttpContext, result, antiforgery);
            return RedirectToPage("/Account/Login");
        }
    }
}
