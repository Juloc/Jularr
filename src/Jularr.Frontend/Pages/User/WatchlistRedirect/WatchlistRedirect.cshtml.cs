using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.User.WatchlistRedirect
{
    // The watchlist is called Favorites; old links and bookmarks keep working with their choices.
    public sealed class WatchlistRedirectModel : PageModel
    {
        public IActionResult OnGet() => RedirectPermanent("/Favorites" + Request.QueryString);
    }
}
