using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class RulesModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.rules.title");
        page.Navigation("nav.admin");
        page.Content.Add(AdminNavigation.Requests);

        // Rule editor and list require the canonical rule types and permissions.
    }
}
