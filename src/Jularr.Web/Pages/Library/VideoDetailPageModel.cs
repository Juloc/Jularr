using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Library;

/// <summary>
/// What the Movie and Series detail pages share: the canonical read model of one Work, the hero's primary action and
/// whether the profile may request. A Work of another media type, or a media type the profile cannot browse, is a 404.
/// </summary>
public abstract class VideoDetailPageModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    VideoDetailQuery query,
    AcquisitionRequestService requests) : PageModel
{
    protected abstract WorkMediaType MediaType { get; }

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public VideoDetail Detail { get; private set; } = null!;

    public VideoHero Hero { get; private set; } = null!;

    /// <summary>
    /// Whether the page offers a Request now: the profile may request this title through the shared Request flow, nothing is
    /// requested yet, and there is something to ask for (nothing is playable, or a Series misses episodes). Only then does the
    /// page carry the Request dialog.
    /// </summary>
    public bool OffersRequest { get; private set; }

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
        var canRequest = detail.Request is { } request && (await requests.GetCapabilitiesAsync(request.Kind, cancellationToken)).CanRequest;
        Hero = VideoDetailView.Hero(detail, canRequest);
        OffersRequest = canRequest && detail.Request is { Open: null } && (Hero.Kind == VideoHeroKind.Request || VideoDetailView.RequestableEpisodes(detail.Episodes).Count > 0);
        return true;
    }
}
