using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class RulesModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.rules.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.rules.title", "admin.rules.description"));
        page.Content.Add(AdminNavigation.Requests);
        page.Content.Add(new MasterDetail(layout =>
        {
            layout.Master.Add(new UiToolbar());
            layout.Master.Add(new UiTable(["table.rule", "table.limit"]));
            // Right-hand Form<TData> is attached once the canonical rule data type exists.
        }));
    }
}
