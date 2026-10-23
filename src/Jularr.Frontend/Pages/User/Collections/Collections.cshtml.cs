using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.User.Collections
{
    // Collections are listed and created on Favorites.
    public sealed class CollectionsModel : PageModel
    {
        public IActionResult OnGet() => RedirectPermanent("/Favorites");
    }
}
