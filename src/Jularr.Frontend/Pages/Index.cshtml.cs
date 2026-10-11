using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

// Home and Discover intentionally share one page and one route.
public sealed class IndexModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("discover.title");
        page.Navigation("nav.home");
        page.Content.Add(new UiHeading("discover.title"));
        page.Content.Add(new UiScope([
            "media.all", "media.anime", "media.series", "media.movies",
            "media.lightNovels", "media.books", "media.manga"
        ]));
        page.Content.Add(new UiToolbar(Search: false, Sort: false));
        page.Content.Add(new UiMediaRail("discover.continue"));
        page.Content.Add(new UiMediaRail("discover.trending"));
        page.Content.Add(new UiMediaRail("discover.top"));
        page.Content.Add(new UiMediaRail("discover.new"));
    }
}
