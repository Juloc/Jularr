using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Library;

/// <summary>
/// What the Movie and Series detail pages share: the canonical read model of one Work, the primary action resolved by the one
/// <see cref="PrimaryActionResolver"/> and whether the profile may request. A Work of another media type, or a media type the profile
/// cannot browse, is a 404.
/// </summary>
public abstract class VideoDetailPageModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    VideoDetailQuery query,
    InstantPlayPolicyService policies) : PageModel
{
    protected abstract WorkMediaType MediaType { get; }

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public VideoDetail Detail { get; private set; } = null!;

    public PrimaryAction PrimaryAction { get; private set; } = null!;

    /// <summary>The episode the action targets, when it targets one.</summary>
    public VideoDetailEpisode? ActionEpisode { get; private set; }

    /// <summary>The player address of a play-style action on a local target; null while the action acquires, requests or only shows state.</summary>
    public string? PlayHref { get; private set; }

    /// <summary>
    /// Whether the page offers a Request now: the profile may request this title through the shared Request flow, nothing is
    /// requested yet, and there is something to ask for (the action acquires or requests, or a Series misses episodes). Only then
    /// does the page carry the Request dialog.
    /// </summary>
    public bool OffersRequest { get; private set; }

    /// <summary>False on a manager-only instance: no page element links into the player.</summary>
    public bool PlaybackEnabled { get; private set; }

    /// <returns>False when there is no such Work of this page's media type.</returns>
    protected async Task<bool> LoadAsync(Guid workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        var detail = await query.GetAsync(account.ProfileId, workId, MediaType, access.VisibleMediaTypes, cancellationToken);
        if (detail is null)
        {
            return false;
        }

        Detail = detail;
        var policy = await policies.ResolveAsync(MediaType, cancellationToken);
        PlaybackEnabled = policy.PlaybackEnabled;
        PrimaryAction = PrimaryActionResolver.Resolve(detail.Playback, policy);
        ActionEpisode = PrimaryAction.WorkEpisodeId is { } episodeId ? detail.Episodes.FirstOrDefault(x => x.Id == episodeId) : null;
        PlayHref = PrimaryAction.TargetIsLocal && PrimaryAction.Kind is not (PrimaryActionKind.Available or PrimaryActionKind.None) ? VideoDetailView.WatchHref(PrimaryAction.WorkId, PrimaryAction.WorkEpisodeId) : null;
        var acquires = PrimaryAction.Kind == PrimaryActionKind.Request || PrimaryAction.Kind is PrimaryActionKind.StartWatching or PrimaryActionKind.WatchNow && !PrimaryAction.TargetIsLocal;
        OffersRequest = policy.AllowsRequest && detail.Request is { Open: null } && (acquires || VideoDetailView.RequestableEpisodes(detail.Episodes).Count > 0);
        return true;
    }
}
