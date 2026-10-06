using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Media.Optimization;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Library;

// Owner-only per-anime repair tools: rescan one anime's folder, refresh its local sidecar
// subtitles/NFO/artwork, force a media re-analysis, and identify/fix its AniList match without
// touching any other anime or requiring a full library scan (issue #133).
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class AnimeRepairModel(
    AppDbContext db,
    AnimeRepairService repair,
    AnimeMetadataService metadataService,
    LibraryScanCoordinator scans,
    CurrentAccountContext currentAccount,
    OperationRunner operations,
    MediaOptimizationQueue optimizationQueue,
    ILogger<AnimeRepairModel> logger) : PageModel
{
    public const string MatchOperationKind = "anime-metadata-match";
    public const string RefreshMetadataOperationKind = "anime-metadata-refresh";
    public const string RefreshLocalOperationKind = "anime-repair-refresh-local";
    public const string ReanalyzeOperationKind = "anime-repair-reanalyze-media";
    private const string OperationCategory = "Anime";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public Guid AnimeId { get; private set; }
    public string AnimeTitle { get; private set; } = "";
    public AnimeMetadata? Metadata { get; private set; }
    public AnimeRepairFolder? Folder { get; private set; }
    public IReadOnlyList<ActiveLibraryScan> ActiveScans { get; private set; } = [];
    public string SearchQuery { get; private set; } = "";
    public IReadOnlyList<AnimeRepairMatchCandidate> Candidates { get; private set; } = [];

    public string? Error => TempData["Error"] as string;
    public Guid? OperationId =>
        TempData["RepairOperation"] is string value && Guid.TryParse(value, out var id) ? id : null;

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? q,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var anime = await db.Anime
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (anime is null)
        {
            return NotFound();
        }

        AnimeId = anime.Id;
        Metadata = await metadataService.GetAsync(id, cancellationToken);
        AnimeTitle = Metadata?.PreferredTitle ?? anime.Title;
        Folder = await repair.ResolveFolderAsync(id, cancellationToken);
        SearchQuery = string.IsNullOrWhiteSpace(q) ? anime.Title : q.Trim();

        if (Folder is not null)
        {
            ActiveScans = scans.GetActive(Folder.RootId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            try
            {
                Candidates = await repair.SearchCandidatesAsync(id, SearchQuery, cancellationToken);
            }
            catch (MetadataProviderException exception)
            {
                logger.LogError(exception, "Anime repair metadata search for {AnimeId} failed", id);
                TempData["Error"] = Ui["library.animeRepair.searchFailed"];
            }
        }

        return Page();
    }

    // Queues a folder-scoped run through the one scan entry point (LibraryScanCoordinator), the
    // same way the filesystem watcher requests a folder scan; the coordinator's per-root guard
    // applies exactly as it does there.
    public async Task<IActionResult> OnPostRescanAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var result = await repair.RescanFolderAsync(id, currentAccount.ProfileId, cancellationToken);
        TempData[result.Queued ? "Status" : "Error"] = result.Message;
        if (result.OperationId is Guid operationId)
        {
            TempData["RepairOperation"] = operationId.ToString("D");
        }

        return Back(id, returnUrl);
    }

    public async Task<IActionResult> OnPostRefreshLocalAsync(Guid id, CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            var result = await operations.RunAsync(
                new OperationDescriptor(
                    RefreshLocalOperationKind,
                    OperationCategory,
                    "Refresh local subtitles/NFO/artwork",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                (_, token) => repair.RefreshLocalAsync(id, token),
                null,
                cancellationToken);

            TempData["Status"] = result.NfoWarnings > 0
                ? ui.Format(
                    "library.animeRepair.refreshLocalResultWithWarnings",
                    ("episodes", result.EpisodesConsidered),
                    ("subtitles", result.SubtitlesImported),
                    ("artworkUpdated", result.ArtworkImported),
                    ("artworkUnchanged", result.ArtworkUnchanged),
                    ("nfoWarnings", result.NfoWarnings))
                : ui.Format(
                    "library.animeRepair.refreshLocalResult",
                    ("episodes", result.EpisodesConsidered),
                    ("subtitles", result.SubtitlesImported),
                    ("artworkUpdated", result.ArtworkImported),
                    ("artworkUnchanged", result.ArtworkUnchanged));
        }
        catch (Exception exception)
        {
            TempData["Error"] = ui.Format(
                "library.animeRepair.refreshLocalFailed",
                ("message", exception.Message));
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReanalyzeAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            var result = await operations.RunAsync(
                new OperationDescriptor(
                    ReanalyzeOperationKind,
                    OperationCategory,
                    "Re-analyse anime media",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                (_, token) => repair.ReanalyzeMediaAsync(id, token),
                null,
                cancellationToken);

            TempData["Status"] = ui.Format(
                "library.animeRepair.reanalyzeResult",
                ("considered", result.MediaFilesConsidered),
                ("succeeded", result.Analyzed),
                ("failed", result.Failed),
                ("deferred", result.Deferred));
        }
        catch (Exception exception)
        {
            TempData["Error"] = ui.Format(
                "library.animeRepair.reanalyzeFailed",
                ("message", exception.Message));
        }

        return Back(id, returnUrl);
    }

    public async Task<IActionResult> OnPostMatchAsync(
        Guid id,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!currentAccount.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    MatchOperationKind,
                    OperationCategory,
                    "Match anime metadata",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (_, token) =>
                {
                    var result = await metadataService.MatchAsync(id, provider, externalId, token);
                    if (!result.Success)
                    {
                        throw new InvalidOperationException(
                            result.Error ?? "Anime metadata could not be matched.");
                    }
                },
                "Anime metadata matched.",
                cancellationToken);

            TempData["Status"] = ui["library.animeRepair.metadataMatched"];
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or InvalidOperationException)
        {
            logger.LogError(exception, "Matching anime metadata for {AnimeId} failed", id);
            TempData["Error"] = ui["library.animeRepair.matchFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshMetadataAsync(Guid id, CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    RefreshMetadataOperationKind,
                    OperationCategory,
                    "Refresh anime metadata",
                    ProfileId: currentAccount.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (_, token) =>
                {
                    if (!await metadataService.RefreshAsync(id, token))
                    {
                        throw new InvalidOperationException("Metadata could not be refreshed.");
                    }
                },
                "Anime metadata refreshed.",
                cancellationToken);

            TempData["Status"] = ui["library.animeRepair.metadataRefreshed"];
        }
        catch (Exception exception) when (
            exception is MetadataProviderException or InvalidOperationException)
        {
            logger.LogError(exception, "Refreshing anime metadata for {AnimeId} failed", id);
            TempData["Error"] = ui["library.animeRepair.metadataRefreshFailed"];
        }

        return RedirectToPage(new { id });
    }

    // Queues the same lossless optimization an import runs, for every existing file of the anime.
    public async Task<IActionResult> OnPostOptimizeAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var anime = await db.Anime.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (anime is null)
        {
            return NotFound();
        }

        var mediaFileIds = await (
                from media in db.MediaFiles.AsNoTracking()
                join episode in db.Episodes.AsNoTracking() on media.EpisodeId equals episode.Id
                where episode.AnimeId == id
                orderby media.Path
                select media.Id)
            .ToArrayAsync(cancellationToken);
        if (mediaFileIds.Length == 0)
        {
            TempData["Error"] = ui["library.animeRepair.noMediaFiles"];
            return Back(id, returnUrl);
        }

        var operationId = await optimizationQueue.QueueAsync(
            mediaFileIds,
            anime.Title,
            currentAccount.ProfileId,
            cancellationToken);
        TempData["Status"] = ui.Format("library.animeRepair.optimizeQueued", ("count", mediaFileIds.Length));
        TempData["RepairOperation"] = operationId.ToString("D");
        return Back(id, returnUrl);
    }

    // The media detail page runs these same actions and wants the admin back on itself, not on this page.
    private IActionResult Back(Guid id, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && Url.IsLocalUrl(returnUrl)
        && returnUrl.StartsWith($"/Admin/Media/{id:D}", StringComparison.OrdinalIgnoreCase)
            ? LocalRedirect(returnUrl)
            : RedirectToPage(new { id });
}
