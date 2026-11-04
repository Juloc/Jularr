using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.Admin.QueueRedirect
{
    // The downloads' queue, its history and its blocked releases moved to the Queue entry; old links (notifications, bookmarks) keep working with their choices.
    public sealed class QueueRedirectModel : PageModel
    {
        public IActionResult OnGet() => RedirectPermanent(Request.Path.Value?.TrimEnd('/').ToLowerInvariant() switch
        {
            "/admin/history" => "/Admin/Queue/History",
            "/admin/activity/blocked" => "/Admin/Queue/Blocked",
            _ => "/Admin/Queue"
        } + Request.QueryString);
    }
}
