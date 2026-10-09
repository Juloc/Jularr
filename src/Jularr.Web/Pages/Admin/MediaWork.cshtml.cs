using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Admin → Media for a Movie or a Series Work (the same Admin media surface as the Anime page, keyed by the canonical
/// Work): its header with the medium-level monitoring, seasons and episodes or the versions of a Movie with their local files,
/// and the quality profile. Every change goes through <see cref="VideoMonitoringService"/> and <see cref="AcquisitionRequestService"/>,
/// the paths the shared Wanted pass and the Requests page already use, so there is no Movie/TV-specific state here.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class MediaWorkModel(
    AppDbContext db,
    AdminVideoMediaService details,
    VideoMonitoringService monitoring,
    MonitoringResolver monitoringState,
    AcquisitionRequestService requests,
    CurrentAccountContext currentAccount,
    RequestArtworkResolver artwork,
    ILogger<MediaWorkModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public const string ReanalyzeOperationKind = "video-reanalyze-media";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public AdminVideoMediaDetail? Detail { get; private set; }

    /// <summary>The cached poster of the Work from the canonical Work artwork; null when it has none yet.</summary>
    public string? PosterUrl { get; private set; }

    /// <summary>Whether the details could not be read.</summary>
    public bool Failed { get; private set; }

    public string? Notice => TempData["Notice"] as string;

    public string? Error => TempData["Error"] as string;

    public string Name(AdminVideoSeason season) =>
        season.Number == 0 ? Ui["library.anime.specials"] : Ui.Format("library.watch.season", ("number", season.Number));

    public async Task<IActionResult> OnGetAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        if (!VideoWorkLinks.TryParseAdminKind(kind, out var mediaKind) || !await IsEnabledAsync(mediaKind, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            Detail = await details.LoadAsync(mediaKind, id, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException or IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.LogError(exception, "The media details of Work {WorkId} could not be read.", id);
            Failed = true;
            return Page();
        }

        if (Detail is null)
        {
            return NotFound();
        }

        PosterUrl = (await artwork.ResolveWorkPostersAsync([Detail.WorkId], currentAccount.ProfileId, cancellationToken)).GetValueOrDefault(Detail.WorkId);
        return Page();
    }

    /// <summary>Monitors or unmonitors a Movie.</summary>
    public async Task<IActionResult> OnPostMovieMonitorAsync(string kind, Guid id, bool monitored, CancellationToken cancellationToken) =>
        await ChangeAsync(kind, id, "admin.media.video.saved", (_, token) => monitoring.SetMovieMonitoredAsync(id, monitored, token), cancellationToken, MediaAcquisitionKind.Movie);

    /// <summary>Sets what is monitored of a whole Series: all episodes, future episodes only, or nothing. Seasons and episodes have their own switches.</summary>
    public async Task<IActionResult> OnPostSeriesScopeAsync(string kind, Guid id, string? scope, CancellationToken cancellationToken) =>
        await ChangeAsync(kind, id, "admin.media.video.saved", (_, token) => monitoring.SetSeriesAsync(id, scope, token), cancellationToken, MediaAcquisitionKind.Tv);

    /// <summary>Monitors or unmonitors one season of a Series independently of the others.</summary>
    public async Task<IActionResult> OnPostSeasonMonitorAsync(string kind, Guid id, int? season, bool monitored, CancellationToken cancellationToken) =>
        season is not { } seasonNumber
            ? BadRequest()
            : await ChangeAsync(kind, id, "admin.media.video.saved", (_, token) => monitoring.SetSeasonMonitoredAsync(id, seasonNumber, monitored, token), cancellationToken, MediaAcquisitionKind.Tv, $"s{seasonNumber}");

    /// <summary>Monitors or unmonitors one episode of a Series.</summary>
    public async Task<IActionResult> OnPostEpisodeMonitorAsync(string kind, Guid id, Guid episodeId, bool monitored, string? open, CancellationToken cancellationToken) =>
        await ChangeAsync(kind, id, "admin.media.video.saved", (_, token) => monitoring.SetEpisodeMonitoredAsync(id, episodeId, monitored, token), cancellationToken, MediaAcquisitionKind.Tv, open);

    /// <summary>Assigns the quality profile of this Work; a blank profile returns to the default of the media type.</summary>
    public async Task<IActionResult> OnPostProfileAsync(string kind, Guid id, string? profileId, CancellationToken cancellationToken) =>
        await ChangeAsync(kind, id, "admin.media.video.profileSaved", (mediaKind, token) => monitoring.SetProfileAsync(mediaKind, id, profileId, token), cancellationToken);

    /// <summary>Searches again right away for the open request of this Work, the same action as "Search now" on Requests and Wanted.</summary>
    public async Task<IActionResult> OnPostSearchAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        if (!VideoWorkLinks.TryParseAdminKind(kind, out var mediaKind) || !await IsEnabledAsync(mediaKind, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!await details.CanAcquireAsync(mediaKind, id, cancellationToken))
        {
            return NotFound();
        }

        var open = await monitoring.FindOpenRequestAsync(mediaKind, id, cancellationToken);
        if (open?.Status != AcquisitionRequestStatus.Approved || !(await monitoringState.LoadAsync(id, cancellationToken)).IsAnyMonitored)
        {
            TempData["Error"] = Ui["admin.media.video.noRequest"];
            return Back(mediaKind, id);
        }

        var result = await requests.ApproveAsync(open.Id, cancellationToken);
        TempData["Notice"] = string.IsNullOrWhiteSpace(result.StatusMessage) ? result.Title : result.StatusMessage;
        return Back(mediaKind, id);
    }

    /// <summary>Forces one local file of this Work through the same analysis path the library uses for all of them.</summary>
    public async Task<IActionResult> OnPostReanalyzeFileAsync(
        string kind,
        Guid id,
        Guid fileId,
        string? open,
        [FromServices] MediaFileReanalysisService reanalysis,
        CancellationToken cancellationToken)
    {
        if (!VideoWorkLinks.TryParseAdminKind(kind, out var mediaKind) || !await IsEnabledAsync(mediaKind, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            var descriptor = new OperationDescriptor(ReanalyzeOperationKind, "Video", "Re-analyse video media", ProfileId: currentAccount.ProfileId);
            var result = await reanalysis.ReanalyzeVideoFileAsync(id, fileId, descriptor, cancellationToken);
            if (!result.Found)
            {
                return NotFound();
            }

            TempData["Notice"] = result.Status == MediaAnalysisStatus.Succeeded ? Ui["admin.media.reanalyzed"] : Ui["admin.media.reanalyzeIncomplete"];
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DbException)
        {
            logger.LogError(exception, "Re-analysing media file {FileId} of Work {WorkId} failed.", fileId, id);
            TempData["Error"] = Ui["admin.media.reanalyzeFailed"];
        }

        return Back(mediaKind, id, open);
    }

    /// <summary>
    /// Runs one change after the checks every action shares: the kind is a Movie or Series with its module on and acquisition is
    /// available for the Work (an unknown Work or a disabled module offers no controls, so it has no action either). A selection that does not belong to the Work or a profile the account may not use
    /// is reported back on the page, never silently ignored.
    /// </summary>
    private async Task<IActionResult> ChangeAsync(
        string kind,
        Guid id,
        string savedKey,
        Func<MediaAcquisitionKind, CancellationToken, Task<VideoMonitoringOutcome>> change,
        CancellationToken cancellationToken,
        MediaAcquisitionKind? expected = null,
        string? open = null)
    {
        if (!VideoWorkLinks.TryParseAdminKind(kind, out var mediaKind) || mediaKind != (expected ?? mediaKind) || !await IsEnabledAsync(mediaKind, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!await details.CanAcquireAsync(mediaKind, id, cancellationToken))
        {
            return NotFound();
        }

        try
        {
            var outcome = await change(mediaKind, cancellationToken);
            if (outcome == VideoMonitoringOutcome.NotFound)
            {
                return NotFound();
            }

            if (outcome is VideoMonitoringOutcome.Conflict or VideoMonitoringOutcome.NotAcquirable)
            {
                TempData["Error"] = Ui[outcome == VideoMonitoringOutcome.Conflict ? "admin.media.video.conflict" : "admin.media.video.noAcquisition"];
                return Back(mediaKind, id, open);
            }

            TempData["Notice"] = Ui[outcome switch
            {
                VideoMonitoringOutcome.Unchanged => "admin.media.video.unchanged",
                VideoMonitoringOutcome.InLibrary => "admin.media.video.inLibrary",
                _ => savedKey
            }];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            TempData["Error"] = Ui["admin.media.video.invalid"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            TempData["Error"] = Ui["admin.media.video.notAllowed"];
        }

        return Back(mediaKind, id, open);
    }

    /// <summary>Back to the page; <paramref name="open"/> names the season or episode that stays open, as an address fragment.</summary>
    private IActionResult Back(MediaAcquisitionKind kind, Guid id, string? open = null) =>
        Redirect(string.IsNullOrWhiteSpace(open) ? VideoWorkLinks.AdminPath(kind, id) : $"{VideoWorkLinks.AdminPath(kind, id)}#{Uri.EscapeDataString(open)}");

    private async Task<bool> IsEnabledAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken) =>
        instanceModules is null || await instanceModules.IsEnabledAsync(AcquisitionInstanceModules.For(kind), cancellationToken);
}
