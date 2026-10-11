using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class RequestsModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.requests.title");
        page.Navigation("nav.admin");
        page.Content.Add(AdminNavigation.Requests);

        // Status tabs, filters and typed table require the canonical request read.
    }
}
