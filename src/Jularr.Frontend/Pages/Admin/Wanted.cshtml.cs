using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class WantedModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.wanted.title");
        page.Navigation("nav.admin");

        // Acquisition worklist and Search/Current/History are service-owned.
    }
}
