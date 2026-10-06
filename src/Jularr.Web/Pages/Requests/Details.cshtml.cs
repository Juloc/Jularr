using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Requests;

/// <param name="Notice">The outcome of an action that did not go through, as a catalog key; null when there is nothing to tell.</param>
/// <param name="Embedded">Whether the panel sits in the status dialog (which has its own close button) instead of on the page.</param>
public sealed record RequestStatusPanel(UiTextBundle Ui, RequestStatusView View, DateTime NowUtc, string? Notice, bool Embedded);

/// <summary>
/// The status surface of one of the signed-in profile's own requests (docs/mockups/request-status-details): the current consumer state,
/// the saved request intent, the milestones and the actions the request allows. The page is the deep link (notifications, the Request
/// success state); the My requests list opens the same panel in a dialog. Every handler is scoped to the profile's own request, and a
/// request of another profile does not exist here. Cancel, Edit and Retry are decided by <see cref="AcquisitionRequestService"/>, never here.
/// </summary>
public sealed class DetailsModel(
    AppDbContext db,
    CurrentAccountContext account,
    RequestStatusQuery status,
    AcquisitionRequestService requests,
    VideoRequestScopeResolver scopes,
    TimeProvider clock) : PageModel
{
    private static readonly IReadOnlySet<string> s_noticeKeys = new HashSet<string>(StringComparer.Ordinal) { "changed", "failed" };

    public RequestStatusPanel Panel { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, string? notice, CancellationToken cancellationToken)
    {
        return await LoadAsync(id, notice, embedded: false, cancellationToken) ? Page() : NotFound();
    }

    /// <summary>The panel alone, for the dialog of the My requests list.</summary>
    public async Task<IActionResult> OnGetPanelAsync(Guid id, CancellationToken cancellationToken)
    {
        return await LoadAsync(id, null, embedded: true, cancellationToken) ? Partial("_RequestStatusPanel", Panel) : NotFound();
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, bool panel, CancellationToken cancellationToken)
    {
        if (await status.FindOwnAsync(id, account.ProfileId, cancellationToken) is null)
        {
            return NotFound();
        }

        var outcome = await requests.CancelAsync(id, cancellationToken);
        return await AnswerAsync(id, panel, outcome == RequestCancelOutcome.NoLongerPending ? "changed" : null, cancellationToken);
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, bool panel, CancellationToken cancellationToken)
    {
        if (await status.FindOwnAsync(id, account.ProfileId, cancellationToken) is null)
        {
            return NotFound();
        }

        var outcome = await requests.RetryAsync(id, cancellationToken);
        return await AnswerAsync(id, panel, outcome == RequestRetryOutcome.NotRetryable ? "changed" : null, cancellationToken);
    }

    /// <summary>The settings of the shared Request dialog with the saved values preselected, for editing a request that still waits for approval.</summary>
    public async Task<IActionResult> OnGetEditAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await status.FindOwnAsync(id, account.ProfileId, cancellationToken) is not { } request)
        {
            return NotFound();
        }

        if (!request.CanBeEdited)
        {
            return StatusCode(StatusCodes.Status409Conflict);
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (request.Kind == MediaAcquisitionKind.Tv)
        {
            return VideoRequestPayload.Parse(request.PayloadJson) is { } saved
                ? Partial("~/Pages/Discover/_DiscoverRequestSettings.cshtml", new DiscoverRequestSettingsView(ui, request.Kind, null, await scopes.LoadStructureAsync(saved.WorkId, cancellationToken), null, null, saved))
                : StatusCode(StatusCodes.Status409Conflict);
        }

        return Partial("~/Pages/Discover/_DiscoverRequestSettings.cshtml", new DiscoverRequestSettingsView(ui, request.Kind, null, [], request.Options.AudioLanguage, request.Options.SubtitleLanguage));
    }

    /// <summary>Saves the Request dialog of an edit: the scope of a series, or the languages of an anime. Validated against the title, applied only while the request still waits.</summary>
    public async Task<IActionResult> OnPostEditAsync(Guid id, [FromForm] DiscoverRequestForm form, CancellationToken cancellationToken)
    {
        if (await status.FindOwnAsync(id, account.ProfileId, cancellationToken) is not { } request)
        {
            return NotFound();
        }

        try
        {
            RequestEditOutcome outcome;
            if (request.Kind == MediaAcquisitionKind.Tv)
            {
                if (!VideoRequestScopeResolver.TryParseScope(form.Scope, out var scope) || form.HasLanguage || VideoRequestPayload.Parse(request.PayloadJson) is not { } saved)
                {
                    return BadRequest();
                }

                var payload = await scopes.BuildTvPayloadAsync(saved.WorkId, new VideoRequestScopeChoice(scope, form.SeasonIds, form.EpisodeIds, form.MonitorFuture), cancellationToken);
                outcome = payload is null ? RequestEditOutcome.NotEditable : await requests.EditAsync(id, payload, null, cancellationToken);
            }
            else if (request.Kind == MediaAcquisitionKind.Anime && !form.HasScope)
            {
                outcome = await requests.EditAsync(id, null, new AcquisitionRequestOptions { AudioLanguage = form.Audio, SubtitleLanguage = form.Subtitles }.Validate(), cancellationToken);
            }
            else
            {
                return BadRequest();
            }

            if (outcome == RequestEditOutcome.NotEditable)
            {
                return StatusCode(StatusCodes.Status409Conflict);
            }
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        if (await status.FindOwnAsync(id, account.ProfileId, cancellationToken) is not { } updated)
        {
            return NotFound();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var summary = RequestStatusText.Intent(ui, updated).Select(row => $"{row.Label}: {row.Value}").ToArray();
        return Partial("~/Pages/Discover/_DiscoverRequestResult.cshtml", new DiscoverRequestResultView(ui, updated, false, summary, null, Updated: true));
    }

    private async Task<IActionResult> AnswerAsync(Guid id, bool panel, string? notice, CancellationToken cancellationToken)
    {
        if (!panel)
        {
            return Redirect(notice is null ? AcquisitionRequestService.StatusPath(id) : $"{AcquisitionRequestService.StatusPath(id)}?notice={notice}");
        }

        return await LoadAsync(id, notice, embedded: true, cancellationToken) ? Partial("_RequestStatusPanel", Panel) : NotFound();
    }

    private async Task<bool> LoadAsync(Guid id, string? notice, bool embedded, CancellationToken cancellationToken)
    {
        if (await status.GetOwnAsync(id, account.ProfileId, cancellationToken) is not { } view)
        {
            return false;
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        ViewData["Title"] = view.Request.Title;
        Panel = new RequestStatusPanel(ui, view, clock.GetUtcNow().UtcDateTime, notice is not null && s_noticeKeys.Contains(notice) ? $"requests.detail.error.{notice}" : null, embedded);
        return true;
    }
}
