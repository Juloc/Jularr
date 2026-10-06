using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

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
    InstantPlayPolicyService policies,
    PlaybackIntentService intents,
    ConsumerAcquisitionQuery acquisition,
    WorkMetadataRefreshQueue metadataRefresh,
    WatchlistStore watchlist,
    ILogger<VideoDetailPageModel> logger) : PageModel
{
    private static readonly HashSet<string> PlayingWords = new(
        ["acquisition.state.readyToWatch", "acquisition.instant.milestone.preparing", "acquisition.instant.stopWaiting", "acquisition.instant.stopped", "acquisition.instant.stoppedHint"],
        StringComparer.Ordinal);

    private InstantPlayPolicy? policy;

    protected abstract WorkMediaType MediaType { get; }

    /// <summary>The query keys of the view state this page keeps when a personal-state action returns to it (a Series: season, sort and layout).</summary>
    protected virtual IReadOnlyList<string> ViewStateKeys => [];

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public VideoDetail Detail { get; private set; } = null!;

    /// <summary>Whether this page shows the trailer facade: a Movie with a playable trailer. The one place that decides it; the view and the framing header follow it.</summary>
    public bool HasTrailer { get; private set; }

    public PrimaryAction PrimaryAction { get; private set; } = null!;

    /// <summary>The episode the action targets, when it targets one.</summary>
    public VideoDetailEpisode? ActionEpisode { get; private set; }

    /// <summary>The player address of a play-style action on a local target; null while the action acquires, requests or only shows state.</summary>
    public string? PlayHref { get; private set; }

    /// <summary>
    /// Whether the page offers a Request now: the profile may request this title through the shared Request flow, nothing is
    /// requested yet, and there is something to ask for (the action requests, a playback intent has Request as its alternative, or a
    /// Series misses episodes). Only then does the page carry the Request dialog.
    /// </summary>
    public bool OffersRequest { get; private set; }

    /// <summary>Whether the title is on the profile's watchlist ("My List"); null when the provider does not identify it, so there is nothing to follow.</summary>
    public bool? IsOnWatchlist { get; private set; }

    /// <summary>The route of this page with the view state the viewer chose (season, sort, layout), so a personal-state action returns to the same view.</summary>
    public IDictionary<string, string> PageRoute(Guid workId)
    {
        var route = new Dictionary<string, string> { ["workId"] = workId.ToString() };
        foreach (var key in ViewStateKeys)
        {
            if (Request.Query[key].ToString() is { Length: > 0 } value)
            {
                route[key] = value;
            }
        }

        return route;
    }

    /// <summary>False on a manager-only instance: no page element links into the player.</summary>
    public bool PlaybackEnabled { get; private set; }

    /// <summary>
    /// The consumer state of the title's open request for the hero target, in the words every consumer surface uses; null when there is no
    /// open request or the profile may not read it (the client API answers 404 for the same profile), so a page never polls what it cannot read.
    /// </summary>
    public ConsumerAcquisitionView? RequestState { get; private set; }

    /// <summary>
    /// The words instant-play.js renders (the consumer acquisition states and the playback-intent labels), so the page never carries its own
    /// copy. An instance without Playback never ships the words of playing: "Starting playback", "Ready to watch" and "Stop waiting".
    /// </summary>
    public IReadOnlyDictionary<string, string> InstantPlayText()
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prefix in new[] { "acquisition.state.", "acquisition.playback.", "acquisition.instant." })
        {
            foreach (var (key, text) in Ui.WithPrefix(prefix))
            {
                var isPlaying = key.StartsWith("acquisition.playback.", StringComparison.Ordinal) || PlayingWords.Contains(key);
                if (PlaybackEnabled || !isPlaying)
                {
                    words[key] = text;
                }
            }
        }

        return words;
    }

    /// <summary>The action of one episode of a Series, resolved like the hero action but for that episode (docs/mockups/instant-play, section 14).</summary>
    public PrimaryAction ActionFor(Guid workEpisodeId) => PrimaryActionResolver.Resolve(Detail.Playback, policy!, workEpisodeId);

    /// <summary>
    /// The explicit playback intent of the hero action (Start watching, Watch now) until the in-place control sends it: a local target
    /// opens the player, a missing one is acquired or attached to its request, and the page shows the request's state again.
    /// </summary>
    public async Task<IActionResult> OnPostStartAsync(Guid workId, Guid? episodeId, CancellationToken cancellationToken)
    {
        // The same guard as the read: the Work must be of this page's media type and the type visible to the profile, so a page of a
        // visible type cannot be used to start the acquisition of a Work of a hidden one.
        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        if (!access.IsVisible(MediaType) || !await db.Works.AsNoTracking().AnyAsync(x => x.Id == workId && x.MediaType == MediaType, cancellationToken))
        {
            return NotFound();
        }

        var result = await intents.StartAsync(workId, episodeId, cancellationToken);
        if (result.Outcome == PlaybackIntentOutcome.TargetNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == PlaybackIntentOutcome.PlayNow && result.Action is { } action)
        {
            return Redirect(VideoDetailView.WatchHref(action.WorkId, action.WorkEpisodeId));
        }

        if (result.Outcome == PlaybackIntentOutcome.LimitReached)
        {
            TempData["Status"] = (await UiRequestLocalization.GetBundleAsync(HttpContext, db))["acquisition.playback.limitReached"];
        }

        return RedirectToPage(new { workId });
    }

    /// <summary>Puts the title on the profile's watchlist. Idempotent: following it again changes nothing.</summary>
    public async Task<IActionResult> OnPostFollowAsync(Guid workId, CancellationToken cancellationToken) => await SetFollowAsync(workId, follow: true, cancellationToken);

    /// <summary>Takes the title off the profile's watchlist. Idempotent: a title that is not on it stays off.</summary>
    public async Task<IActionResult> OnPostUnfollowAsync(Guid workId, CancellationToken cancellationToken) => await SetFollowAsync(workId, follow: false, cancellationToken);

    // The follow is built from the stored title and provider identity, never from what the browser sends, and only for a Work of this page's media type that the profile may open.
    private async Task<IActionResult> SetFollowAsync(Guid workId, bool follow, CancellationToken cancellationToken)
    {
        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        var detail = await query.GetAsync(account.ProfileId, workId, MediaType, access.VisibleMediaTypes, cancellationToken);
        if (detail?.Request is not { } identity || !WatchlistDraftInput.TryCreate(identity.Category, identity.Provider, identity.ExternalId, detail.Title, detail.NativeTitle, null, null, null, detail.Year, out var draft))
        {
            return NotFound();
        }

        if (follow)
        {
            await watchlist.FollowAsync(account.ProfileId, draft, cancellationToken);
        }
        else
        {
            await watchlist.UnfollowAsync(account.ProfileId, draft.Identity, cancellationToken);
        }

        return RedirectToPage(PageRoute(workId));
    }

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
        await PromoteMetadataRefreshAsync(workId, detail.Metadata, cancellationToken);
        HasTrailer = MediaType == WorkMediaType.Movie && detail.Metadata?.PlayableTrailer is not null;
        if (HasTrailer)
        {
            // The trailer facade adds one frame after the viewer clicks; no other page content may be framed.
            Response.Headers.ContentSecurityPolicy = $"frame-src 'self' {WorkTrailerView.EmbedOrigin}";
        }

        policy = await policies.ResolveAsync(MediaType, cancellationToken);
        PlaybackEnabled = policy.PlaybackEnabled;
        PrimaryAction = PrimaryActionResolver.Resolve(detail.Playback, policy);
        ActionEpisode = PrimaryAction.WorkEpisodeId is { } episodeId ? detail.Episodes.FirstOrDefault(x => x.Id == episodeId) : null;
        PlayHref = PrimaryAction.TargetIsLocal && PrimaryAction.Kind is not (PrimaryActionKind.Available or PrimaryActionKind.None) ? VideoDetailView.WatchHref(PrimaryAction.WorkId, PrimaryAction.WorkEpisodeId) : null;
        var hasRequestableTarget = PrimaryAction.Kind == PrimaryActionKind.Request || PrimaryAction.RequestIsAlternative || VideoDetailView.RequestableEpisodes(detail.Episodes).Count > 0;
        OffersRequest = policy.AllowsRequest && detail.Request is { Open: null } && hasRequestableTarget;
        if (detail.Request is { } identity && WatchlistDraftInput.TryIdentity(identity.Category, identity.Provider, identity.ExternalId, out var followed))
        {
            IsOnWatchlist = (await watchlist.GetEffectiveKeysAsync(account.ProfileId, cancellationToken)).Contains(followed.Key);
        }

        if (detail.Request?.Open is { } open && ConsumerAcquisitionQuery.MayRead(open, account.ProfileId, policy.CanRequest, account.Can(JularrPolicies.AdminMedia)))
        {
            // A local target has no state of its own to show, so the request as a whole is projected: it still says whether monitoring continues.
            RequestState = await acquisition.ProjectAsync(open, PrimaryAction.TargetIsLocal ? null : PrimaryAction.WorkEpisodeId, policy.PlaybackEnabled, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Opening a Work asks the metadata spool to fetch what is missing at interactive priority (#820, open-time promotion). This is the one
    /// write a detail read may make: an idempotent enqueue that never reaches a provider, never waits for a fetch and never touches metadata
    /// that was fetched already; the page renders what is stored meanwhile. It runs only for a Work this profile may open (a hidden type or
    /// an unknown Work is a 404 before this point). A queue failure must not take the page down, so it is logged and the page renders
    /// without it; stale metadata is refreshed by the spool on its own schedule.
    /// </summary>
    private async Task PromoteMetadataRefreshAsync(Guid workId, WorkMetadataView? metadata, CancellationToken cancellationToken)
    {
        if (metadata?.RefreshedAt is not null)
        {
            return;
        }

        try
        {
            await metadataRefresh.RequestMetadataRefreshAsync(workId, interactive: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not queue the metadata of Work {WorkId} when it was opened.", workId);
        }
    }
}
