using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.AspNetCore.Authorization;

namespace Jularr.Web.Pages.Admin.Manga;

[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class WorkModel(
    AppDbContext db,
    ReadingWorkAdminQuery query,
    MonitoringCommands monitoring,
    WantedReconciler wanted,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    QualityProfileStore profiles,
    ReadingStructureService structure,
    MangaVersionSelector versions,
    IInstanceModuleService? instanceModules = null)
    : ReadingWorkModel(db, query, monitoring, wanted, requests, requestStore, profiles, structure, instanceModules)
{
    protected override MediaAcquisitionKind Kind => MediaAcquisitionKind.Manga;

    protected override Task ReselectVersionsAsync(long workId, CancellationToken cancellationToken) => versions.ReselectAsync(workId, cancellationToken);
}
