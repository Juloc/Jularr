using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Library
{
    public sealed class LibraryModel : PreparedPage
    {
        protected override void Define(Page page)
        {
            page.Title("admin.library.title");
            page.Navigation("nav.admin");

            // File and availability rows must come from the canonical Library read.
        }
    }
}
