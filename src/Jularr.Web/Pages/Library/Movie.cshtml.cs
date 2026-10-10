using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Shell;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Web.Pages.Library;

public sealed class MovieDetailModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    VideoDetailQuery query,
    InstantPlayPolicyService policies,
    PlaybackIntentService intents,
    ConsumerAcquisitionQuery acquisition,
    WorkMetadataRefreshQueue metadataRefresh,
    WatchlistStore watchlist,
    PlexExternalPlaybackAction plexExternal,
    ILogger<VideoDetailPageModel> logger) : VideoDetailPageModel(db, account, appShell, query, policies, intents, acquisition, metadataRefresh, watchlist, plexExternal, logger)
{
    protected override WorkMediaType MediaType => WorkMediaType.Movie;

    public async Task<IActionResult> OnGetAsync(long workId, CancellationToken cancellationToken) =>
        await LoadAsync(workId, cancellationToken) ? Page() : NotFound();
}
