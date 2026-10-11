using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages;

public sealed class DiscoverRedirectModel : PageModel
{
    public IActionResult OnGet()
    {
        var path = "/" + Request.QueryString;
        return RedirectPermanent(path);
    }
}
