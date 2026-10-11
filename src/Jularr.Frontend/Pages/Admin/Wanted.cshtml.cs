using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class WantedModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.wanted.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.wanted.title", "admin.wanted.description"));
        page.Content.Add(new UiScope([
            "common.all", "wanted.requested", "wanted.missing",
            "wanted.searching", "wanted.failed"
        ]));
        page.Content.Add(new UiToolbar());
        page.Content.Add(new UiTable([
            "table.work", "table.type", "table.language", "table.profile",
            "table.status", "table.lastSearch", "table.actions"
        ]));
        // Acquisition Search/Current/History dialog binds to a selected canonical target.
    }
}
