using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class UsersModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.users.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.users.title", "admin.users.description"));
        page.Content.Add(AdminNavigation.Requests);
        page.Content.Add(new MasterDetail(layout =>
        {
            layout.Master.Add(new UiToolbar());
            layout.Master.Add(new UiTable([
                "table.user", "table.role", "table.rule", "table.limit"
            ]));
            // User editor awaits authorized user-directory and assignment contracts.
        }));
    }
}
