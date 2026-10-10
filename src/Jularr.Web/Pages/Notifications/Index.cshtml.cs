using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Notifications;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Notifications;

/// <summary>
/// The per-profile notification inbox (#429): only the signed-in profile's own deliveries,
/// consumer/admin split naturally because admin-only events (for example storage problems) are
/// only ever delivered to owner/media-manager profiles in the first place.
/// </summary>
[Authorize]
public sealed class IndexModel(
    AppDbContext db,
    NotificationStore notifications,
    CurrentAccountContext account) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<NotificationItem> Items { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public bool UnreadOnly { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Items = await notifications.ListAsync(account.ProfileId, UnreadOnly, cancellationToken: cancellationToken);
    }

    public async Task<IActionResult> OnGetPreviewAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var items = await notifications.ListAsync(account.ProfileId, unreadOnly: true, limit: 6, cancellationToken);
        var count = await notifications.CountUnreadAsync(account.ProfileId, cancellationToken);
        return Partial("_NotificationPreview", new NotificationPreview(ui, items, count));
    }

    public async Task<IActionResult> OnPostReadPreviewAsync(CancellationToken cancellationToken)
    {
        await notifications.MarkAllReadAsync(account.ProfileId, cancellationToken);
        return await OnGetPreviewAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostMarkReadAsync(Guid id, bool unreadOnly, CancellationToken cancellationToken)
    {
        await notifications.MarkReadAsync(id, account.ProfileId, cancellationToken);
        return RedirectToPage(new { unreadOnly });
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync(bool unreadOnly, CancellationToken cancellationToken)
    {
        await notifications.MarkAllReadAsync(account.ProfileId, cancellationToken);
        return RedirectToPage(new { unreadOnly });
    }

    public async Task<IActionResult> OnPostClearReadAsync(bool unreadOnly, CancellationToken cancellationToken)
    {
        await notifications.ClearReadAsync(account.ProfileId, cancellationToken);
        return RedirectToPage(new { unreadOnly });
    }
}
