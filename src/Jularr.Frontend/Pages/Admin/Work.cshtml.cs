using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin;

public sealed class WorkModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.work.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiDetailHero());
        page.Content.Add(new UiSection("admin.work.structure", [
            new UiTable([
                "table.work", "table.monitored", "table.availability", "table.actions"
            ])
        ]));
        page.Content.Add(new UiSection("admin.work.files", [
            new UiTable(["table.version", "table.language", "table.size", "table.status", "table.actions"])
        ]));
    }

    public IActionResult OnGet(long id)
    {
        if (id <= 0) return NotFound();
        PreparePage();
        return Page();
    }
}
