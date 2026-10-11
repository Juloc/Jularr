using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class WorksModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.works.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.works.title", "admin.works.description"));
        page.Content.Add(new UiScope([
            "media.all", "media.anime", "media.series", "media.movies",
            "media.lightNovels", "media.books", "media.manga"
        ]));
        page.Content.Add(new UiToolbar());
        page.Content.Add(new UiTable([
            "table.work", "table.type", "table.monitored",
            "table.availability", "table.status", "table.actions"
        ]));
    }
}
