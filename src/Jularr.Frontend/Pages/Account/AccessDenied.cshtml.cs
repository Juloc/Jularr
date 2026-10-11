using Jularr.Backend.Service.Core.Localization;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Account
{
    [AllowAnonymous]
    public sealed class AccessDeniedModel : JularrPage
    {
        protected override void Define(Page page)
        {
            var resourceKey = ServiceErrorResources.GetKey("permission_denied");
            page.Title(resourceKey);
            page.Content.Add(new P(new Text(resourceKey)).Attribute("role", "alert"));
            page.Content.Add(new A(new Text("nav.home")).Attribute("href", "/"));
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
