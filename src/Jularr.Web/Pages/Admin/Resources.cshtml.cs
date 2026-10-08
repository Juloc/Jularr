using Jularr.Web.Data;
using Jularr.Web.Features.Admin;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Performance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>Admin → Resources: only the Jularr/PostgreSQL stack and the storage mounts visible to it, plus what Jularr itself is busy with.</summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class ResourcesModel(
    AppDbContext db,
    AdminDashboardService dashboard,
    ApplicationPerformanceTelemetry telemetry,
    BackgroundWorkGovernor governor,
    InteractiveLoad load) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public AdminDashboardSnapshot Snapshot { get; private set; } = null!;

    public ApplicationPerformanceView Performance { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Snapshot = await dashboard.GetAsync(includeSessions: false, cancellationToken);
        Performance = new ApplicationPerformanceView(Ui, ApplicationPerformanceReport.Build(telemetry, governor, load, Snapshot.Resources));
    }
}
