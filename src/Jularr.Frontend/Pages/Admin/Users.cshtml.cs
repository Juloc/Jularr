using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class UsersModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.users.title");
        page.Navigation("nav.admin");
        page.Content.Add(AdminNavigation.Requests);

        // User directory and editor must be bound to authorized account data.
    }
}
