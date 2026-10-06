using System.Data.Common;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Admin → Activity / To-Do: the work queue of the server, with retry and cancel where a worker can
/// carry them out. What has ended stays readable in the history.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class OperationsModel(
    AppDbContext db,
    IOperationActions actions,
    VideoRequestWorkResolver videoWorks,
    ILogger<OperationsModel> logger) : PageModel
{
    public const string PagePath = "/Admin/Operations";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public AdminActivityPlan Plan { get; private set; } = AdminActivityQuery.Plan([], new AdminActivityFilter());

    public IReadOnlyList<OperationSnapshot> Items { get; private set; } = [];

    /// <summary>The Admin media page of the Movie or Series a download on this page belongs to, by operation id.</summary>
    public IReadOnlyDictionary<Guid, string> MediaLinks { get; private set; } = new Dictionary<Guid, string>();

    /// <summary>Whether the operations could not be read.</summary>
    public bool Failed { get; private set; }

    /// <summary>Whether the server has any operation at all, whatever the filters say.</summary>
    public bool AnyOperations { get; private set; }

    public bool CanCancel(OperationSnapshot operation) => actions.CanCancel(operation);

    public bool CanRetry(OperationSnapshot operation) => actions.CanRetry(operation);

    public bool CanChangePriority(OperationSnapshot operation) => actions.CanChangePriority(operation);

    /// <summary>The address of the activity with the given filter; default values stay out of it.</summary>
    public static string Href(AdminActivityFilter filter) => AdminActivityQuery.Href(PagePath, filter);

    public async Task OnGetAsync(
        string? tab,
        string? type,
        string? status,
        string? priority,
        string? q,
        int p,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var filter = AdminActivityQuery.Normalize(new AdminActivityFilter(
            AdminActivityQuery.ParseTab(tab),
            AdminHistoryQuery.ParseCategory(type),
            AdminActivityQuery.TryParseStatus(status),
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            Math.Max(p, 1),
            OperationPriorities.TryParse(priority)));
        Plan = AdminActivityQuery.Plan([], filter);

        try
        {
            var store = new OperationStore(db);
            var counts = await store.CountActivityAsync(filter.Search, cancellationToken);
            AnyOperations = counts.Count > 0
                || (!string.IsNullOrWhiteSpace(filter.Search)
                    && (await store.CountActivityAsync(null, cancellationToken)).Count > 0);
            Plan = AdminActivityQuery.Plan(counts, filter);
            if (Plan.Total > 0)
            {
                Items = (await store.QueryActivityAsync(AdminActivityQuery.DatabaseFilter(Plan), cancellationToken)).Items;
                MediaLinks = await videoWorks.ResolveOperationLinksAsync(Items, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException)
        {
            logger.LogError(exception, "The activity could not be read.");
            Failed = true;
        }
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
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
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
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
        return Back(returnUrl);
    }

    public async Task<IActionResult> OnPostPriorityAsync(Guid id, string? priority, string? returnUrl, CancellationToken cancellationToken)
    {
        if (OperationPriorities.TryParse(priority) is not { } parsed)
        {
            return BadRequest();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var store = new OperationStore(db);
        var operation = await store.GetAsync(id, cancellationToken);
        if (operation is null)
        {
            return NotFound();
        }

        TempData["Status"] = actions.CanChangePriority(operation)
            && await store.SetPriorityAsync(id, parsed, cancellationToken)
                ? Ui["admin.operation.priorityChanged"]
                : Ui["admin.operation.priorityUnavailable"];
        return Back(returnUrl);
    }

    /// <summary>Returns to the filtered activity the action came from; anything else goes to the unfiltered page.</summary>
    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && Url.IsLocalUrl(returnUrl)
        && (returnUrl == PagePath || returnUrl.StartsWith(PagePath + "?", StringComparison.Ordinal))
            ? LocalRedirect(returnUrl)
            : RedirectToPage();
}
