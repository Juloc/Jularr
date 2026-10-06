using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Franchises;

/// <summary>One member card: provider data plus what is decided now (library, follow state).</summary>
public sealed record FranchiseMemberView(
    FranchiseMember Member,
    WatchlistLibraryMatch? Library,
    string? RelationLabel,
    bool IsFollowed,
    bool IsHidden);

public sealed class DetailsModel(
    AppDbContext db,
    CurrentAccountContext account,
    FranchiseStore franchises,
    FranchiseService franchiseService,
    MediaRelationStore relations,
    WatchlistStore watchlist,
    WatchlistLibraryResolver library,
    IAppShellService? shell = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public FranchiseSummary Franchise { get; private set; } = null!;

    public IReadOnlyList<FranchiseMemberView> Members { get; private set; } = [];

    public bool IsFollowed { get; private set; }

    /// <summary>Followers and the owner may ask for a refresh.</summary>
    public bool CanRefresh => IsFollowed || account.IsOwner;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var franchise = await franchises.GetAsync(id, cancellationToken);
        if (franchise is null)
        {
            return NotFound();
        }

        Franchise = franchise;
        IsFollowed = await franchises.IsFollowedAsync(account.ProfileId, id, cancellationToken);
        var visible = shell is null
            ? WorkMediaTypes.All.ToHashSet()
            : (await shell.GetMediaAccessAsync(User, cancellationToken))
                .VisibleMediaTypes
                .ToHashSet();
        var members = (await franchises.GetMembersAsync(id, cancellationToken))
            .Where(member => visible.Contains(
                WorkMediaTypes.FromWatchlist(member.Media.Identity.MediaType)))
            .ToArray();
        var graph = await relations.GetForFranchiseAsync(id, cancellationToken);
        var matches = await library.ResolveAsync(members.Select(member => member.Media.Identity), cancellationToken);
        var followed = await watchlist.GetEffectiveKeysAsync(account.ProfileId, cancellationToken);
        var hidden = await watchlist.GetHiddenKeysAsync(account.ProfileId, cancellationToken);
        var titles = members.ToDictionary(member => member.Media.Identity.Key, member => member.Media.Title, StringComparer.Ordinal);

        Members = members
            .Select(member =>
            {
                var key = member.Media.Identity.Key;
                return new FranchiseMemberView(
                    member,
                    matches.GetValueOrDefault(key),
                    RelationLabel(member, franchise.Seed, graph, titles),
                    followed.Contains(key),
                    hidden.Contains(key));
            })
            .ToArray();
        return Page();
    }

    public async Task<IActionResult> OnPostFollowAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await franchises.GetAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        await franchiseService.FollowAsync(account.ProfileId, id, cancellationToken);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnfollowAsync(Guid id, CancellationToken cancellationToken)
    {
        await franchiseService.UnfollowAsync(account.ProfileId, id, cancellationToken);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await franchiseService.RequestRefreshAsync(id, account.ProfileId, account.IsOwner, cancellationToken);
        if (FranchiseRefreshStatus.MessageKey(result) is not { } key)
        {
            return result == FranchiseRefreshRequest.NotFound ? NotFound() : Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui[key];
        return RedirectToPage(new { id });
    }

    /// <summary>Stops following one work of a followed franchise; <see cref="OnPostShowMemberAsync"/> undoes it.</summary>
    public Task<IActionResult> OnPostHideMemberAsync(Guid id, string? key, CancellationToken cancellationToken) =>
        WithMemberAsync(id, key, identity => watchlist.UnfollowAsync(account.ProfileId, identity, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostShowMemberAsync(Guid id, string? key, CancellationToken cancellationToken) =>
        WithMemberAsync(id, key, identity => watchlist.RestoreAsync(account.ProfileId, identity, cancellationToken), cancellationToken);

    private async Task<IActionResult> WithMemberAsync(
        Guid id,
        string? key,
        Func<WatchlistIdentity, Task> action,
        CancellationToken cancellationToken)
    {
        if (!await franchises.IsFollowedAsync(account.ProfileId, id, cancellationToken))
        {
            return BadRequest();
        }

        var member = (await franchises.GetMembersAsync(id, cancellationToken))
            .FirstOrDefault(item => item.Media.Identity.Key == key);
        if (member is null)
        {
            return BadRequest();
        }

        if (shell is not null)
        {
            var visible = (await shell.GetMediaAccessAsync(User, cancellationToken))
                .VisibleMediaTypes;
            if (!visible.Contains(
                    WorkMediaTypes.FromWatchlist(member.Media.Identity.MediaType)))
            {
                return NotFound();
            }
        }

        await action(member.Media.Identity);
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// "Sequel to X": how the member relates to another member that lists it, preferring the
    /// seed. Null for the seed and when no labelled relation is known.
    /// </summary>
    private string? RelationLabel(
        FranchiseMember member,
        WatchlistIdentity seed,
        IReadOnlyList<MediaRelation> graph,
        IReadOnlyDictionary<string, string> titles)
    {
        if (member.IsSeed)
        {
            return null;
        }

        var key = member.Media.Identity.Key;
        var incoming = graph
            .Where(relation => relation.To.Key == key && relation.From.Key != key)
            .Select(relation => (Relation: relation, LabelKey: FranchiseLabels.RelationKey(relation.RelationType)))
            .Where(item => item.LabelKey is not null &&
                           titles.TryGetValue(item.Relation.From.Key, out var title) &&
                           !string.IsNullOrWhiteSpace(title))
            .OrderByDescending(item => item.Relation.From.Key == seed.Key)
            .FirstOrDefault();
        return incoming.LabelKey is { } labelKey
            ? Ui.Format(labelKey, ("title", titles[incoming.Relation.From.Key]))
            : null;
    }

    public static string TypeLabelKey(WatchlistMediaType type) =>
        $"watchlist.type.{WatchlistMediaTypeNames.ToCategory(type)}";
}
