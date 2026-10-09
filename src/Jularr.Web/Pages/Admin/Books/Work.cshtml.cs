using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Audiobooks;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Books;

/// <summary>
/// One Book Work: the Book and its audiobook side by side, each with its own Monitoring switch, Wanted state and request. Every change is a
/// Monitoring command or a request through the shared access policy; nothing here is Book-specific state.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class WorkModel(
    AppDbContext db,
    BookWorkAdminQuery query,
    MonitoringCommands monitoring,
    WantedReconciler wanted,
    AcquisitionRequestService requests,
    AudiobookMetadataService audiobookMetadata,
    ILogger<WorkModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public BookWorkAdminView View { get; private set; } = null!;

    public AudiobookEditionMetadata? AudiobookDetails { get; private set; }

    /// <summary>The recordings the providers offer for the Book, listed only when the owner asked to look; none is attached before the owner confirms it.</summary>
    public IReadOnlyList<AudiobookCandidate>? Candidates { get; private set; }

    public string? Notice => TempData["BookWorkNotice"] as string;

    public string? Error => TempData["BookWorkError"] as string ?? providerProblem;

    private string? providerProblem;

    public async Task<IActionResult> OnGetAsync(Guid workId, bool find, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        View = view;
        AudiobookDetails = await audiobookMetadata.GetAsync(workId, cancellationToken);
        if (find)
        {
            try
            {
                Candidates = await audiobookMetadata.FindAsync(workId, cancellationToken);
            }
            catch (Exception exception) when (exception is AudiobookMetadataException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "The audiobook providers could not be asked for Work {WorkId}.", workId);
                providerProblem = Ui["admin.books.providerUnavailable"];
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostMonitorAsync(Guid workId, bool audiobook, bool monitored, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var changed = audiobook
            ? await monitoring.SetAudiobookAsync(workId, monitored, cancellationToken)
            : await monitoring.SetAsync(MonitoringTargetKind.Work, workId, monitored, cancellationToken);
        if (changed is null)
        {
            return NotFound();
        }

        await wanted.ReconcileAsync(workId, cancellationToken);
        TempData["BookWorkNotice"] = Ui["admin.books.saved"];
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostUseMetadataAsync(Guid workId, string provider, string externalId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            if (await audiobookMetadata.AttachAsync(workId, provider, externalId, cancellationToken) is null)
            {
                return NotFound();
            }
        }
        catch (Exception exception) when (exception is AudiobookMetadataException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The audiobook metadata of Work {WorkId} could not be read.", workId);
            TempData["BookWorkError"] = Ui["admin.books.providerUnavailable"];
            return RedirectToPage(new { workId });
        }

        TempData["BookWorkNotice"] = Ui["admin.books.metadataSaved"];
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostRequestAudiobookAsync(Guid workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view || await query.IdentityAsync(workId, cancellationToken) is not var (provider, externalId))
        {
            TempData["BookWorkError"] = Ui["admin.books.noIdentity"];
            return RedirectToPage(new { workId });
        }

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new AudiobookRequestPayload(view.Title, null), System.Text.Json.JsonSerializerOptions.Web);
            var submission = await requests.SubmitWithOutcomeAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Audiobook, provider, externalId, view.Title, null, null, payload) { WorkId = workId }, cancellationToken);
            TempData["BookWorkNotice"] = submission.AlreadyRequested ? Ui["admin.books.alreadyRequested"] : submission.Request.StatusMessage ?? Ui["admin.books.requested"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { workId });
    }

    public string StateLabel(BookTargetState state) =>
        Ui[state.Installed ? "admin.books.state.installed" : state.Wanted ? "admin.books.state.wanted" : state.Monitored ? "admin.books.state.monitored" : "admin.books.state.notWanted"];

    public string RequestLabel(AcquisitionRequestStatus status) => Ui[$"admin.books.request.{status.ToString().ToLowerInvariant()}"];
}
