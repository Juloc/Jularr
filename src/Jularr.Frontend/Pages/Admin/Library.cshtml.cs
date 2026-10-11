using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin;

public sealed class LibraryModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("admin.library.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.library.title", "admin.library.description"));
        page.Content.Add(new UiScope([
            "media.all", "media.anime", "media.series", "media.movies",
            "media.lightNovels", "media.books", "media.manga"
        ]));
        page.Content.Add(new UiToolbar());
        page.Content.Add(new UiTable([
            "table.work", "table.type", "table.availability",
            "table.size", "table.status", "table.actions"
        ]));
    }
}
