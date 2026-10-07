using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Discover;

/// <summary>
/// <c>/Discover</c> is no longer a page of its own: Home and Discover are one surface at <c>/</c>. Old bookmarks, shelf links and cached clients
/// land there with their query (search, scope, filters, handler) unchanged.
/// </summary>
public sealed class DiscoverRedirectModel : PageModel
{
    public IActionResult OnGet() => RedirectPermanent("/" + Request.QueryString);
}
