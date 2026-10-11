using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

public sealed class WatchlistModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("watchlist.title");
        page.Navigation("nav.watchlist");
        page.Content.Add(new UiHeading("watchlist.title"));
        page.Content.Add(new UiScope([
            "media.all", "media.anime", "media.series",
            "media.movies", "media.lightNovels", "media.books", "media.manga"
        ]));
        page.Content.Add(new UiToolbar(ViewSwitch: true));
        page.Content.Add(new UiMediaGrid());
    }
}
