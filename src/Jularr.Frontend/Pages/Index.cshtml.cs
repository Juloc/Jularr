using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

public sealed class IndexModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("discover.title");
        page.Navigation("nav.home");

        // The authorized Backend read must supply visible instance/profile media types,
        // browse capabilities and actual shelves. Never assume all modules are enabled.
    }
}
