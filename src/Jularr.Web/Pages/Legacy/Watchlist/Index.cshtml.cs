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

    [BindProperty(SupportsGet = true, Name = "page")]
    public int CurrentPage { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "mediaType")]
    public string? TargetMediaType { get; set; }

    [BindProperty(SupportsGet = true, Name = "provider")]
    public string? TargetProvider { get; set; }

    [BindProperty(SupportsGet = true, Name = "externalId")]
    public string? TargetExternalId { get; set; }

    public bool IsTargetView => TargetMediaType is not null
        || TargetProvider is not null
        || TargetExternalId is not null;

    public PageResult<WatchlistItem> ItemPage { get; private set; } =
        PageResult<WatchlistItem>.From([], new PageRequest());

    public IReadOnlyList<WatchlistItem> Items => ItemPage.Items;

    public long TotalCount => ItemPage.TotalCount ?? 0;

    public long PageCount => Math.Max(1L, (TotalCount + PageRequest.DefaultPageSize - 1) / PageRequest.DefaultPageSize);

    [BindProperty(SupportsGet = true, Name = "franchisePage")]
    public int CurrentFranchisePage { get; set; } = 1;

    public PageResult<FranchiseSummary> FranchisePage { get; private set; } =
        PageResult<FranchiseSummary>.From([], new PageRequest());

    public IReadOnlyList<FranchiseSummary> Franchises => FranchisePage.Items;

    public long FranchisePageCount =>
        Math.Max(1L, ((FranchisePage.TotalCount ?? 0) + PageRequest.DefaultPageSize - 1)
            / PageRequest.DefaultPageSize);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var visible = shell is null
            ? WorkMediaTypes.All.ToHashSet()
            : (await shell.GetMediaAccessAsync(User, cancellationToken))
                .VisibleMediaTypes
                .ToHashSet();

        WatchlistIdentity? target = null;
        if (IsTargetView)
        {
            if (!WatchlistDraftInput.TryIdentity(
                TargetMediaType,
                TargetProvider,
                TargetExternalId,
                out var resolvedTarget))
            {
                return BadRequest();
            }

            target = resolvedTarget;
        }

        try
        {
            var page = await watchlist.GetEffectivePageAsync(
                account,
                new PageRequest(IsTargetView ? 1 : CurrentPage),
                Enum.GetValues<WatchlistMediaType>()
                    .Where(type => visible.Contains(WorkMediaTypes.FromWatchlist(type)))
                    .ToArray(),
                cancellationToken,
                target);

            ItemPage = page with
            {
                Items = await library.ApplyAsync(page.Items, cancellationToken)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest();
        }

        try
        {
            FranchisePage = await franchises.ListFollowedAsync(
                account.ProfileId,
                new PageRequest(CurrentFranchisePage),
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest();
        }

        return Page();
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
