using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class RequestsModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.requests.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.requests.title"));
        page.Content.Add(AdminNavigation.Requests);
        page.Content.Add(new UiScope([
            "common.all", "requests.open", "requests.approved", "requests.inProgress",
            "requests.done", "requests.rejected"
        ]));
        page.Content.Add(new UiToolbar());
        page.Content.Add(new UiTable([
            "table.work", "table.requester", "table.language", "table.requestedAt",
            "table.status", "table.actions"
        ]));
    }
}
