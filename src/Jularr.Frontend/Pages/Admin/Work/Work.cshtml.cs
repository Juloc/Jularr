using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Work
{
    public sealed class WorkModel : JularrPage
    {
        protected override void Define(Page page)
        {
            page.Title("admin.work.title");
            page.Navigation("nav.admin");

            // Structure, files, versions and actions depend on the authorized Work read.
        }

        public IActionResult OnGet(long id)
        {
            if (id <= 0) return NotFound();
            PreparePage();
            return Page();
        }
    }
}
