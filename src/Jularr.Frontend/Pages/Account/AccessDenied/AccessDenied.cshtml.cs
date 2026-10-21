using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account.AccessDenied
{
    // Where the cookie scheme sends a signed-in account that lacks a permission: the shared 403 page.
    [AllowAnonymous]
    public sealed class AccessDeniedModel(FrontendAssets assets, UiText texts) : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("error.page", texts["error.403.title"]);
            page.Asset("Pages/Shared/ErrorState.entry.ts");
            page.Content.Add(new ErrorState(assets, 403, null, null, null, false));
        }

        public IActionResult OnGet()
        {
            // Preserve the actual failure status.
            Response.StatusCode = StatusCodes.Status403Forbidden;
            PreparePage();
            return Page();
        }
    }
}
