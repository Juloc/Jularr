using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

// One small PageModel for presentation-only surfaces; no service, fake row or provider request.
public sealed class SkeletonModel : JularrPage
{
    protected override void Define(Page page)
    {
        if (!Screens.Populate(Screens.RouteFromPath(HttpContext.Request.Path.Value), page))
        {
            throw new InvalidOperationException("Unknown frontend screen.");
        }
    }

    public IActionResult OnGet()
    {
        var id = RouteData.Values["id"];
        if (id is not null && (!long.TryParse(id.ToString(), out var parsed) || parsed < 1))
        {
            return NotFound();
        }

        PreparePage();
        return Page();
    }
}
