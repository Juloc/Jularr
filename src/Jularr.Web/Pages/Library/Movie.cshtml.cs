using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Web.Pages.Library;

public sealed class MovieDetailModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    VideoDetailQuery query,
    AcquisitionRequestService requests) : VideoDetailPageModel(db, account, appShell, query, requests)
{
    protected override WorkMediaType MediaType => WorkMediaType.Movie;

    public async Task<IActionResult> OnGetAsync(Guid workId, CancellationToken cancellationToken) =>
        await LoadAsync(workId, cancellationToken) ? Page() : NotFound();
}
