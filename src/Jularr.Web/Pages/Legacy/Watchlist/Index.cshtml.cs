using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Watchlist;

public sealed class IndexModel(
    AppDbContext db,
    CurrentAccountContext account,
    WatchlistStore watchlist,
    WatchlistLibraryResolver library,
    FranchiseStore franchises,
    FranchiseService franchiseService,
    IAppShellService? shell = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<WatchlistItem> Items { get; private set; } = [];

    public IReadOnlyList<FranchiseSummary> Franchises { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var visible = shell is null
            ? WorkMediaTypes.All.ToHashSet()
            : (await shell.GetMediaAccessAsync(User, cancellationToken))
                .VisibleMediaTypes
                .ToHashSet();

        Items = await library.ApplyAsync(
            (await watchlist.GetEffectiveAsync(account.ProfileId, cancellationToken))
                .Where(item => visible.Contains(WorkMediaTypes.FromWatchlist(item.Identity.MediaType)))
                .ToArray(),
            cancellationToken);
        Franchises = await franchises.ListFollowedAsync(account.ProfileId, cancellationToken);
    }

    public async Task<IActionResult> OnPostRemoveAsync(
        string mediaType,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!WatchlistDraftInput.TryIdentity(mediaType, provider, externalId, out var identity))
        {
            return BadRequest();
        }

        await watchlist.UnfollowAsync(account.ProfileId, identity, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostFollowFranchiseAsync(
        string mediaType,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!WatchlistDraftInput.TryIdentity(mediaType, provider, externalId, out var identity) ||
            !FranchiseService.CanSeed(identity))
        {
            return BadRequest();
        }

        if (!await IsVisibleAsync(identity, cancellationToken))
        {
            return NotFound();
        }

        await franchiseService.FollowFromSeedAsync(account.ProfileId, identity, cancellationToken);
        return RedirectToPage();
    }

    private async Task<bool> IsVisibleAsync(
        WatchlistIdentity identity,
        CancellationToken cancellationToken)
    {
        if (shell is null)
        {
            return true;
        }

        var access = await shell.GetMediaAccessAsync(User, cancellationToken);
        return access.VisibleMediaTypes.Contains(
            WorkMediaTypes.FromWatchlist(identity.MediaType));
    }

    public async Task<IActionResult> OnPostUnfollowFranchiseAsync(
        Guid franchiseId,
        CancellationToken cancellationToken)
    {
        await franchiseService.UnfollowAsync(account.ProfileId, franchiseId, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshFranchiseAsync(
        Guid franchiseId,
        CancellationToken cancellationToken)
    {
        var result = await franchiseService.RequestRefreshAsync(
            franchiseId,
            account.ProfileId,
            account.IsOwner,
            cancellationToken);
        if (FranchiseRefreshStatus.MessageKey(result) is not { } key)
        {
            return result == FranchiseRefreshRequest.NotFound ? NotFound() : Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui[key];
        return RedirectToPage();
    }
}
