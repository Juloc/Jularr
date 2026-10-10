using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Franchises;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.MediaFacts;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.Presentation;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Novels;

/// <summary>View model of one row in the work page's chapter list (partial _NovelChapterRow), shared
/// between ungrouped chapters and chapters nested under a collapsible group heading (#512).</summary>
public sealed record NovelChapterRowModel(
    NovelChapterItem Chapter,
    NovelChapterItem? CurrentChapter,
    NovelProgress? Progress,
    UiTextBundle Ui);

/// <summary>
/// The stable volume-plus-chapter structure rendered both directly and inside a presentation
/// group. Keeping the structure in one partial ensures a custom display group cannot discard a
/// volume's cover, reading state, or chapter actions.
/// </summary>
public sealed record NovelVolumeGroupModel(
    NovelVolumeItem? Volume,
    IReadOnlyList<NovelChapterItem> Chapters,
    NovelChapterItem? CurrentChapter,
    NovelProgress? Progress,
    UiTextBundle Ui);

[NovelEpubUploadRequestLimits("UploadVolume")]
public sealed class WorkModel(
    AppDbContext db,
    NovelCatalogQueries catalog,
    NovelImportService imports,
    NovelProgressService progress,
    NovelMetadataService metadata,
    NovelMappingService mappings,
    AniListAccountService aniListAccount,
    NovelJobs jobs,
    NovelEpubImportService epubImports,
    CurrentAccountContext account,
    OperationRunner operations,
    FranchiseStore franchises,
    FranchiseService franchiseService,
    ILogger<WorkModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public NovelWorkDetail? Detail { get; private set; }
    public IReadOnlyList<NovelMetadataCandidate> SearchResults { get; private set; } = [];
    public IReadOnlyList<NovelAnimeChoice> AnimeChoices { get; private set; } = [];
    public NovelProgress? Progress { get; private set; }
    public ExternalProgressSummary? ExternalProgress { get; private set; }
    public string SearchQuery { get; private set; } = "";
    public bool IsSearching { get; private set; }
    public bool IsOwner => account.IsOwner;
    public long? ManageWorkId { get; private set; }
    public IReadOnlyList<FranchiseSummary> Franchises { get; private set; } = [];
    public IReadOnlyList<FranchiseRelationGroup> FranchiseGroups { get; private set; } = [];
    public IReadOnlyList<PresentationSection<NovelVolumeItem>> PresentationVolumeSections { get; private set; } = [];
    public IReadOnlyList<PresentationSection<NovelChapterItem>> PresentationChapterSections { get; private set; } = [];

    /// <summary>
    /// Language availability for this work (#426). The header already states the chapter/volume
    /// counts and raw provider status in its own words, so this only ever renders the language
    /// chips (showFacts: false) -- never a second, differently-worded copy of the same fact.
    /// </summary>
    public MediaFactsStripModel? Facts { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? q,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Detail = await catalog.GetWorkDetailAsync(id, cancellationToken);
        if (Detail is null)
        {
            return NotFound();
        }

        if (account.IsOwner)
        {
            ManageWorkId = await db.WorkSourceLinks.AsNoTracking()
                .Where(link => link.SourceKind == WorkSourceKind.NovelWork && link.SourceId == id)
                .Select(link => (long?)link.WorkId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        Facts = MediaFactsStripModel.Create(
            await new MediaFactsService(db).GetNovelFactsAsync(id, cancellationToken),
            Ui,
            showFacts: false);

        if (!string.IsNullOrWhiteSpace(Detail.Work.MetadataProvider) &&
            !string.IsNullOrWhiteSpace(Detail.Work.MetadataExternalId))
        {
            var identity = new WatchlistIdentity(
                WatchlistMediaType.LightNovel,
                Detail.Work.MetadataProvider,
                Detail.Work.MetadataExternalId);
            Franchises = await franchises.FindForMemberAsync(identity, cancellationToken);
            FranchiseGroups = await franchiseService.GetRelationGroupsAsync(identity, cancellationToken);
        }

        Progress = await progress.GetProgressAsync(
            account.ProfileId,
            id,
            cancellationToken);

        // Local-only: remote AniList progress is loaded after first paint
        // through OnGetExternalProgressAsync.
        ExternalProgress = await aniListAccount.GetNovelProgressSummaryAsync(
            id,
            cancellationToken);
        var presentationGroups = await new PresentationGroupStore(db).ListForWorkAsync(PresentationMediaType.Novel, id, cancellationToken);
        var epubVolumes = Detail.Volumes.Where(volume => volume.IsEpub).ToArray();
        if (epubVolumes.Length > 0)
        {
            PresentationVolumeSections = PresentationGrouping.Arrange(presentationGroups, epubVolumes, volume => volume.Number, Ui.Format("library.presentation.otherHeading", ("units", Ui["library.presentation.volumes"])));
        }
        else
        {
            var fallbackName = Ui.Format("library.presentation.otherHeading", ("units", Ui["library.presentation.chapters"]));
            PresentationChapterSections = PresentationGrouping.Arrange(presentationGroups, Detail.Chapters, chapter => chapter.Number, fallbackName);
        }

        AnimeChoices = account.IsOwner
            ? await mappings.GetAnimeChoicesAsync(cancellationToken)
            : [];
        SearchQuery = string.IsNullOrWhiteSpace(q)
            ? Detail.Work.MetadataTitle ?? Detail.Work.Title
            : q.Trim();

        if (account.IsOwner && !string.IsNullOrWhiteSpace(q))
        {
            IsSearching = true;
            try
            {
                SearchResults = await metadata.SearchAsync(
                    NovelAniListProvider.ProviderKey,
                    SearchQuery,
                    8,
                    cancellationToken);
            }
            catch (NovelMetadataProviderException exception)
            {
                logger.LogError(exception, "Novel metadata search for {WorkId} failed", id);
                TempData["Status"] = Ui["novels.work.searchFailed"];
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnGetExternalProgressAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await catalog.GetWorkTitleAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        var state = await aniListAccount.GetNovelProgressStateAsync(
            id,
            cancellationToken);

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Response.Headers.CacheControl = "no-store";
        return Partial(
            "_ExternalProgressState",
            new ExternalProgressRemoteView(
                ExternalProgressMediaKind.Novel,
                state,
                "SyncAniListProgress",
                ui));
    }

    public async Task<IActionResult> OnPostSyncAniListProgressAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await operations.RunAsync(
            new OperationDescriptor(
                "anilist-novel-progress-sync",
                "AniList",
                "Sync novel progress",
                ProfileId: account.ProfileId,
                Lane: OperationLane.Normal,
                Retryable: false),
            (_, token) => aniListAccount.SyncNovelProgressAsync(
                id,
                token),
            "Novel progress sync completed.",
            cancellationToken);

        TempData["Status"] = result.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefreshAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "novel-refresh",
                    "Novels",
                    "Refresh novel table of contents",
                    ProfileId: account.ProfileId,
                    Lane: OperationLane.Normal,
                    IsDownload: true,
                    Retryable: false),
                (_, token) => imports.RefreshWorkAsync(id, token),
                "Novel table of contents refreshed.",
                cancellationToken);

            TempData["Status"] = ui["novels.work.tocRefreshed"];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Refreshing novel table of contents for {WorkId} failed", id);
            TempData["Status"] = ui["novels.work.refreshFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadVolumeAsync(
        Guid id,
        List<IFormFile>? epubs,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var outcomes = await NovelEpubUploads.ImportAsync(
            epubImports,
            operations,
            account.ProfileId,
            epubs,
            targetWorkId: id,
            cancellationToken);

        if (outcomes is null)
        {
            var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = ui.Format(
                "novels.index.chooseEpubFiles",
                ("max", NovelEpubUploadRequestLimitsAttribute.MaximumFiles));
        }
        else
        {
            TempData["Status"] = NovelEpubImportOutcome.Summarize(outcomes);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveVolumeAsync(
        Guid id,
        Guid volumeId,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await epubImports.RemoveVolumeAsync(id, volumeId, cancellationToken);
            TempData["Status"] = ui["novels.work.volumeRemoved"];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Removing volume {VolumeId} for novel {WorkId} failed", volumeId, id);
            TempData["Status"] = ui["novels.work.volumeRemoveFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostLoadAllAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var title = await catalog.GetWorkTitleAsync(id, cancellationToken);
        if (title is null)
        {
            return NotFound();
        }

        var chapterIds = await catalog.GetChapterIdsWithoutContentAsync(
            id,
            cancellationToken);

        if (chapterIds.Count > 0)
        {
            await jobs.QueueChapterDownloadAsync(
                title,
                chapterIds,
                account.ProfileId,
                cancellationToken);
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = chapterIds.Count == 0
            ? ui["novels.work.allCached"]
            : ui.Format("novels.work.queuedCaching", ("count", chapterIds.Count));

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMatchMetadataAsync(
        Guid id,
        string provider,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await operations.RunAsync(
                new OperationDescriptor(
                    "novel-anilist-match",
                    "Novels",
                    "Match novel metadata",
                    ProfileId: account.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                (_, token) => metadata.MatchAsync(
                    id,
                    provider,
                    externalId,
                    token),
                "Novel metadata matched.",
                cancellationToken);

            TempData["Status"] = ui["novels.work.matched"];
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NovelMetadataProviderException)
        {
            logger.LogError(exception, "Matching novel metadata for {WorkId} failed", id);
            TempData["Status"] = ui["novels.work.matchFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMetadataAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        await metadata.RemoveAsync(id, cancellationToken);
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["novels.work.matchRemoved"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddMappingAsync(
        Guid id,
        Guid animeId,
        int chapterStart,
        int chapterEnd,
        int seasonNumber,
        int episodeStart,
        int episodeEnd,
        string? label,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await mappings.AddManualAsync(
                id,
                animeId,
                chapterStart,
                chapterEnd,
                seasonNumber,
                episodeStart,
                episodeEnd,
                label,
                cancellationToken);
            TempData["Status"] = ui["novels.work.mappingSaved"];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Adding episode mapping for novel {WorkId} to anime {AnimeId} failed", id, animeId);
            TempData["Status"] = ui["novels.work.mappingFailed"];
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSuggestMappingsAsync(
        Guid id,
        Guid animeId,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var title = await catalog.GetWorkTitleAsync(id, cancellationToken);
        if (title is null)
        {
            return NotFound();
        }

        await jobs.QueueEpisodeMappingAsync(
            id,
            animeId,
            title,
            account.ProfileId,
            cancellationToken);

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["novels.work.aiMappingQueued"];
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMappingAsync(
        Guid id,
        Guid mappingId,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        await mappings.RemoveAsync(id, mappingId, cancellationToken);
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = ui["novels.work.mappingRemoved"];
        return RedirectToPage(new { id });
    }
}
