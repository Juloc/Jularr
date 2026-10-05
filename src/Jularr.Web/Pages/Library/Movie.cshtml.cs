using Jularr.Web.Data;
using Jularr.Web.Features.InstantPlay;
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
    InstantPlayPolicyService policies) : VideoDetailPageModel(db, account, appShell, query, policies)
{
    protected override WorkMediaType MediaType => WorkMediaType.Movie;

    public async Task<IActionResult> OnGetAsync(Guid workId, CancellationToken cancellationToken) =>
        await LoadAsync(workId, cancellationToken) ? Page() : NotFound();
}
