using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin;

[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class OperationModel(
    AppDbContext db,
    IOperationActions actions,
    SabnzbdAcquisitionStore acquisitions,
    VideoRequestWorkResolver videoWorks) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public OperationSnapshot Operation { get; private set; } = null!;
    public IReadOnlyList<OperationLogEntry> Logs { get; private set; } = [];
    public SabnzbdAcquisition? Acquisition { get; private set; }
    public SabnzbdAcquisitionAttempt? AcquisitionAttempt { get; private set; }
    public IReadOnlyList<SabnzbdBlockedRelease> AcquisitionBlocklist { get; private set; } = [];

    /// <summary>Routing and completed-download import details of an external download, when recorded.</summary>
    public DownloadOperationDetails? DownloadDetails { get; private set; }

    /// <summary>The Admin media page of the Movie or Series this download belongs to, when it is a Movie/TV download.</summary>
    public string? MediaLink { get; private set; }

    public bool IsSabnzbdJob =>
        SabnzbdDownloadService.IsSabnzbdOperation(Operation);

    public bool CanCancel => actions.CanCancel(Operation);

    public bool RuntimeAvailable => actions.HasRuntime(Operation);

    /// <summary>The history the visitor came from (with its filters and page), when they came from it.</summary>
    public string? BackToHistory { get; private set; }

    /// <summary>The activity list the visitor came from (with its tab and filters), when they came from it.</summary>
    public string? BackToActivity { get; private set; }

    /// <summary>The name of the account that started the operation; null when the server did.</summary>
    public string? ActorName { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (IsPageAddress(returnUrl, HistoryModel.PagePath))
        {
            BackToHistory = returnUrl;
        }
        else if (IsPageAddress(returnUrl, OperationsModel.PagePath))
        {
            BackToActivity = returnUrl;
        }

        return await LoadAsync(id, cancellationToken)
            ? Page()
            : NotFound();
    }

    public async Task<IActionResult> OnPostCancelAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var operation = await new OperationStore(db).GetAsync(id, cancellationToken);
        if (operation is null)
        {
            return NotFound();
        }

        var outcome = await actions.CancelAsync(operation, cancellationToken);
        TempData["Status"] = outcome.Message
            ?? (outcome.Succeeded
                ? Ui["admin.operation.cancelRequested"]
                : Ui["admin.operation.cancelUnavailable"]);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var operation = await new OperationStore(db).GetAsync(id, cancellationToken);
        if (operation is null)
        {
            return NotFound();
        }

        var outcome = await actions.RetryAsync(operation, cancellationToken);
        TempData["Status"] = outcome.Message
            ?? (outcome.Succeeded
                ? Ui["admin.operation.retryQueued"]
                : Ui["admin.operation.retryUnavailable"]);
        return RedirectToPage(new { id });
    }

    /// <summary>Whether the address is the given admin page, with or without its filters (and stays on this server).</summary>
    private bool IsPageAddress(string? address, string path) =>
        !string.IsNullOrEmpty(address)
        && Url.IsLocalUrl(address)
        && (address == path || address.StartsWith(path + "?", StringComparison.Ordinal));

    private async Task<bool> LoadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var store = new OperationStore(db);
        var operation = await store.GetAsync(id, cancellationToken);
        if (operation is null)
        {
            return false;
        }

        Operation = operation;
        if (!string.IsNullOrEmpty(operation.ActorProfileId))
        {
            ActorName = await db.OwnerAccounts
                .AsNoTracking()
                .Where(account => account.Id == operation.ActorProfileId)
                .Select(account => account.UserName)
                .FirstOrDefaultAsync(cancellationToken)
                ?? Ui["admin.history.actor.unknown"];
        }

        DownloadDetails = DownloadOperationDetails.TryParse(operation.Details, out var details) ? details : null;
        MediaLink = (await videoWorks.ResolveOperationLinksAsync([operation], cancellationToken)).GetValueOrDefault(operation.Id);
        Logs = await store.ListLogsAsync(
            new OperationLogFilter(OperationId: id, Limit: 300),
            cancellationToken);

        if (operation.Kind == SabnzbdAcquisitionService.OperationKind)
        {
            var state = await acquisitions.LoadAsync(cancellationToken);
            if (state.FindByOperation(id) is { } relation)
            {
                Acquisition = relation.Acquisition;
                AcquisitionAttempt = relation.Attempt;
                var identities = relation.Acquisition.Attempts
                    .Select(attempt => attempt.ReleaseIdentity)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                AcquisitionBlocklist = state.Blocklist
                    .Where(entry => identities.Contains(entry.ReleaseIdentity))
                    .ToArray();
            }
        }

        return true;
    }
}
