using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

public sealed class DetailsModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Title("details.title");
        page.Navigation("nav.home");
        page.Content.Add(new UiDetailHero());
        // Structure adapts to Work/media kind after a canonical details service is available.
        page.Content.Add(new UiMediaColumns());
        page.Content.Add(new UiSection("details.versions", [new UiTable([
            "table.version", "table.language", "table.availability"
        ])]));
        page.Content.Add(new UiMediaRail("details.recommendations"));
    }

    public IActionResult OnGet(long id)
    {
        if (id <= 0) return NotFound();
        PreparePage();
        return Page();
    }
}
