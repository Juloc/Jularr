using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class UiPlaygroundModel(IHostEnvironment environment) : PageModel
{
    public IActionResult OnGet()
    {
        return environment.IsDevelopment() ? Page() : NotFound();
    }
}
