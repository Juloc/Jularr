using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

public sealed class DetailsModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Title("details.title");
        page.Navigation("nav.home");

        // The Work type and authorized details read decide structure, editions
        // and actions. A static empty hero/episode grid would misrepresent the work.
    }

    public IActionResult OnGet(long id)
    {
        if (id <= 0) return NotFound();
        PreparePage();
        return Page();
    }
}
