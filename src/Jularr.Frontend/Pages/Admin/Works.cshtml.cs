using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class WorksModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.works.title");
        page.Navigation("nav.admin");

        // Work filters and rows require a module-aware authorized list contract.
    }
}
