using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

public sealed class WatchlistModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("watchlist.title");
        page.Navigation("nav.watchlist");

        // Media scopes and entries must come from the active profile's capabilities.
    }
}
