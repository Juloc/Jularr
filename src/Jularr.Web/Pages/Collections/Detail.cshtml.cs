using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Collections;

public sealed class DetailModel(
    AppDbContext db,
    CurrentAccountContext account,
    CollectionService collections,
    IAppShellService shell) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public CollectionDetailView View { get; private set; } = null!;

    public bool IsSmart => View.Kind == CollectionKind.Smart;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var view = await collections.GetDetailAsync(User, account.ProfileId, id, Ui, cancellationToken);
        if (view is null)
        {
            return NotFound();
        }

        View = view;
        return Page();
    }

    /// <summary>Title search over media-core works for the manual "add work" picker, capability-filtered.</summary>
    public async Task<IActionResult> OnGetSearchAsync(Guid id, string? q, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var collection = await collections.GetAsync(account.ProfileId, id, cancellationToken);
        if (collection is null || collection.Kind != CollectionKind.Manual)
        {
            return NotFound();
        }

        var query = (q ?? "").Trim();
        if (query.Length < 2)
        {
            return new JsonResult(Array.Empty<object>());
        }

        var access = await shell.GetMediaAccessAsync(User, cancellationToken);
        var visible = access.VisibleMediaTypes.ToArray();

        var works = await db.Works.AsNoTracking()
            .Where(x => visible.Contains(x.MediaType) && EF.Functions.ILike(x.CanonicalTitle, $"%{query}%"))
            .OrderBy(x => x.CanonicalTitle)
            .Take(20)
            .Select(x => new { x.Id, x.CanonicalTitle, x.MediaType, x.Year })
            .ToListAsync(cancellationToken);

        return new JsonResult(works.Select(x => new
        {
            id = x.Id,
            title = x.CanonicalTitle,
            mediaType = WorkMediaTypes.ToStorage(x.MediaType),
            year = x.Year
        }));
    }

    public async Task<IActionResult> OnPostAddAsync(Guid id, long workId, CancellationToken cancellationToken)
    {
        var added = await collections.AddManualItemAsync(account.ProfileId, id, workId, cancellationToken);
        TempData["Status"] = added ? "Work added." : "Work is already in this collection.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, long workId, CancellationToken cancellationToken)
    {
        await collections.RemoveItemAsync(account.ProfileId, id, workId, cancellationToken);
        TempData["Status"] = "Work removed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var count = await collections.MaterializeAsync(account.ProfileId, id, cancellationToken);
        if (count is null)
        {
            return NotFound();
        }

        TempData["Status"] = $"Rebuilt: {count} works match.";
        return RedirectToPage(new { id });
    }
}
