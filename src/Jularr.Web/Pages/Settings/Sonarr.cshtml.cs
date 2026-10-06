using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SonarrModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Legacy/Admin/Sonarr");
}
