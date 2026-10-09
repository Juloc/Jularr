using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Instance;
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
    AcquisitionAccessStore requestStore,
    QualityProfileStore profiles,
    AudiobookMetadataService audiobookMetadata,
    ILogger<WorkModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public BookWorkAdminView View { get; private set; } = null!;

    public AudiobookEditionMetadata? AudiobookDetails { get; private set; }

    /// <summary>The recordings the providers offer for the Book, listed only when the owner asked to look; none is attached before the owner confirms it.</summary>
    public IReadOnlyList<AudiobookCandidate>? Candidates { get; private set; }

    /// <summary>Whether this instance serves the Audiobook module; a Book page without it shows no audiobook controls.</summary>
    public bool AudiobooksEnabled { get; private set; } = true;

    public string? Notice => TempData["BookWorkNotice"] as string;

    public string? Error => TempData["BookWorkError"] as string ?? providerProblem;

    private string? providerProblem;

    public async Task<IActionResult> OnGetAsync(long workId, bool find, CancellationToken cancellationToken)
    {
        if (!await BookModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        View = view;
        AudiobooksEnabled = await ModuleEnabledAsync(MediaAcquisitionKind.Audiobook, cancellationToken);
        if (AudiobooksEnabled)
        {
            AudiobookDetails = await audiobookMetadata.GetAsync(workId, cancellationToken);
        }

        if (find && AudiobooksEnabled)
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

    public async Task<IActionResult> OnPostMonitorAsync(long workId, bool audiobook, bool monitored, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(audiobook ? MediaAcquisitionKind.Audiobook : MediaAcquisitionKind.Book, cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var changed = audiobook
            ? await monitoring.SetAudiobookAsync(workId, monitored, cancellationToken) is not null
            : await monitoring.SetWorkAsync(workId, monitored, cancellationToken);
        if (!changed)
        {
            return NotFound();
        }

        await wanted.ReconcileAsync(workId, cancellationToken);
        TempData["BookWorkNotice"] = Ui["admin.books.saved"];
        return RedirectToPage(new { workId });
    }

    /// <summary>
    /// Searches now: the open request that waits for a release is run again, the same action as Search now on Wanted and Requests; a Book without an
    /// open request gets one through the shared access policy, so it is searched, downloaded and imported by the same pipeline as any other request.
    /// </summary>
    public async Task<IActionResult> OnPostSearchAsync(long workId, CancellationToken cancellationToken)
    {
        if (!await BookModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        if (await query.IdentityAsync(workId, cancellationToken) is not var (provider, externalId))
        {
            TempData["BookWorkError"] = Ui["admin.books.noIdentity"];
            return RedirectToPage(new { workId });
        }

        try
        {
            if (await requestStore.FindOpenAsync(MediaAcquisitionKind.Book, provider, externalId, cancellationToken) is { } open)
            {
                if (open.Status != AcquisitionRequestStatus.Approved)
                {
                    TempData["BookWorkError"] = Ui["admin.books.search.running"];
                    return RedirectToPage(new { workId });
                }

                var result = await requests.ApproveAsync(open.Id, cancellationToken);
                TempData["BookWorkNotice"] = string.IsNullOrWhiteSpace(result.StatusMessage) ? result.Title : result.StatusMessage;
                return RedirectToPage(new { workId });
            }

            var detail = view.Detail;
            var primary = detail.Editions.FirstOrDefault();
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new BookRequestPayload(externalId, view.Title, detail.Author, primary is { Language: not "und" } ? primary.Language : null, primary?.Isbn, view.Year),
                System.Text.Json.JsonSerializerOptions.Web);
            var submission = await requests.SubmitWithOutcomeAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Book, provider, externalId, view.Title, detail.Author, null, payload) { WorkId = workId }, cancellationToken);
            TempData["BookWorkNotice"] = submission.Request.StatusMessage ?? Ui["admin.books.search.started"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { workId });
    }

    /// <summary>Runs the failed request of this Book again with the intent it was saved with, the same action as Retry on Requests.</summary>
    public async Task<IActionResult> OnPostRetryAsync(long workId, CancellationToken cancellationToken)
    {
        if (!await BookModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { Detail.BookRequest: { Status: AcquisitionRequestStatus.Failed } failed })
        {
            TempData["BookWorkError"] = Ui["admin.books.retry.none"];
            return RedirectToPage(new { workId });
        }

        try
        {
            var outcome = await requests.RetryAsync(failed.Id, cancellationToken);
            TempData[outcome == RequestRetryOutcome.NotRetryable ? "BookWorkError" : "BookWorkNotice"] = Ui[outcome == RequestRetryOutcome.NotRetryable ? "admin.books.retry.refused" : "admin.books.retry.started"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { workId });
    }

    /// <summary>Assigns the quality profile of this Book; a blank profile returns to the default of the media type.</summary>
    public async Task<IActionResult> OnPostProfileAsync(long workId, string? profileId, CancellationToken cancellationToken)
    {
        if (!await BookModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(profileId) && !(await profiles.LoadAsync(cancellationToken)).Profiles.Any(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase) && BookWorkAdminQuery.ServesBooks(profile)))
        {
            TempData["BookWorkError"] = Ui["admin.books.profile.unknown"];
            return RedirectToPage(new { workId });
        }

        await profiles.AssignWorkAsync(workId, profileId, cancellationToken);
        await wanted.ReconcileAsync(workId, cancellationToken);
        TempData["BookWorkNotice"] = Ui["admin.books.profile.saved"];
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostUseMetadataAsync(long workId, string provider, string externalId, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(MediaAcquisitionKind.Audiobook, cancellationToken))
        {
            return NotFound();
        }

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

    public async Task<IActionResult> OnPostRequestAudiobookAsync(long workId, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(MediaAcquisitionKind.Audiobook, cancellationToken))
        {
            return NotFound();
        }

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

    public string StateLabel(BookTargetState state, bool upgradeWanted = false) =>
        Ui[state.Installed ? (upgradeWanted ? "admin.books.state.upgrade" : "admin.books.state.installed") : state.Wanted ? "admin.books.state.wanted" : state.Monitored ? "admin.books.state.monitored" : "admin.books.state.notWanted"];

    public string RequestLabel(AcquisitionRequestStatus status) => Ui[$"admin.books.request.{status.ToString().ToLowerInvariant()}"];

    private Task<bool> BookModuleEnabledAsync(CancellationToken cancellationToken) => ModuleEnabledAsync(MediaAcquisitionKind.Book, cancellationToken);

    private async Task<bool> ModuleEnabledAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken) =>
        instanceModules is null || await instanceModules.IsEnabledAsync(AcquisitionInstanceModules.For(kind), cancellationToken);
}
