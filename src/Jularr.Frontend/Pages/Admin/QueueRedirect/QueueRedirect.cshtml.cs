using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Pages.Admin.QueueRedirect
{
    // The downloads' queue, its history and its blocked releases moved to the Queue entry, and so did the manual import; old links (notifications, bookmarks) keep working with their choices.
    public sealed class QueueRedirectModel : PageModel
    {
        public IActionResult OnGet() => RedirectPermanent(Request.Path.Value?.TrimEnd('/').ToLowerInvariant() == "/admin/manualimport" ? ManualImport() : Request.Path.Value?.TrimEnd('/').ToLowerInvariant() switch
        {
            "/admin/history" => "/Admin/Queue/History",
            "/admin/activity/blocked" => "/Admin/Queue/Blocked",
            _ => "/Admin/Queue"
        } + Request.QueryString);

        // The manual import is the queue's dialog; a waiting download keeps its own.
        private string ManualImport() => Request.Query["operationId"].ToString() is { Length: > 0 } operation
            ? "/Admin/Queue?import=true&importOperation=" + Uri.EscapeDataString(operation) : "/Admin/Queue?import=true";
    }
}
