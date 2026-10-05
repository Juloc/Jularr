using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.ReadingAcquisition;
using Jularr.Web.Features.ReadingDiscovery;
using Jularr.Web.Features.ReadingSources;
using Jularr.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Novels;

public sealed record NovelCatalogResult(
    ReadingCatalogCandidate Item,
    ReadingSourceDefinition Source,
    string? LocalUrl,
    string? RequestStatus);

[NovelEpubUploadRequestLimits("UploadEpub")]
public sealed class IndexModel(
    NovelCatalogQueries catalog,
    NovelImportService imports,
    NovelEpubImportService epubImports,
    MediaInboxImportService inboxes,
    ReadingCatalogSearchService catalogSearch,
    ReadingSourceSettingsStore sourceSettingsStore,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    CurrentAccountContext account,
    OperationRunner operations,
    AppDbContext db,
    ILogger<IndexModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<NovelListItem> Works { get; private set; } = [];
    public IReadOnlyList<NovelListItem> ContinueReading { get; private set; } = [];
    public IReadOnlyList<NovelCatalogResult> SearchResults { get; private set; } = [];
    /// <summary>Brand names of enabled sources that did not answer this search.</summary>
    public IReadOnlyList<string> UnavailableSources { get; private set; } = [];
    public AcquisitionCapabilities Access { get; private set; } =
        AcquisitionCapabilities.Default(MediaAcquisitionKind.LightNovel);
    public string SearchQuery { get; private set; } = "";
    public bool IsOwner => account.IsOwner;
    /// <summary>The Light Novel inbox folder (Settings → Acquisition → Media folders), or null.</summary>
    public string? InboxPath { get; private set; }

    public bool IsInboxConfigured => InboxPath is not null;

    public async Task OnGetAsync(
        string? q,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Works = await catalog.GetLibraryAsync(account.ProfileId, cancellationToken);
        InboxPath = await inboxes.InboxAsync(MediaAcquisitionKind.LightNovel, cancellationToken);
        ContinueReading = Works
            .Where(x => x.HasProgress)
            .OrderByDescending(x => x.LastReadAt)
            .Take(8)
            .ToArray();

        Access = await requests.GetCapabilitiesAsync(
            MediaAcquisitionKind.LightNovel,
            cancellationToken);
        SearchQuery = ReadingCatalogSearch.NormalizeQuery(q);

        // The search panel is only rendered for accounts that may add or request, so an
        // account without that right must not make outbound searches either.
        if (SearchQuery.Length == 0 || !Access.CanRequest)
        {
            return;
        }

        var sourceSettings = await LoadReadingSourceSettingsAsync(
            cancellationToken);
        var outcome = await catalogSearch.SearchLightNovelsAsync(
            sourceSettings,
            SearchQuery,
            24,
            cancellationToken);
        var candidates = outcome.Candidates;
        UnavailableSources = outcome.UnavailableProviders
            .Select(key => ReadingSourceCatalog.GetRequired(key).Name)
            .ToArray();

        var aniListIds = candidates
            .Where(candidate =>
                candidate.Provider.Equals(
                    NovelAniListProvider.ProviderKey,
                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.ExternalId)
            .ToArray();
        var syosetuIds = candidates
            .Where(candidate =>
                candidate.Provider.Equals(
                    NcodeNovelSourceProvider.ProviderKey,
                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.ExternalId)
            .ToArray();

        var localWorks = await db.NovelWorks
            .AsNoTracking()
            .Where(work =>
                (work.MetadataProvider == NovelAniListProvider.ProviderKey &&
                 work.MetadataExternalId != null &&
                 aniListIds.Contains(work.MetadataExternalId))
                ||
                (work.SourceProvider == NcodeNovelSourceProvider.ProviderKey &&
                 syosetuIds.Contains(work.SourceKey)))
            .Select(work => new
            {
                work.Id,
                work.MetadataProvider,
                work.MetadataExternalId,
                work.SourceProvider,
                work.SourceKey
            })
            .ToListAsync(cancellationToken);

        var local = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var work in localWorks)
        {
            if (work.MetadataProvider == NovelAniListProvider.ProviderKey &&
                work.MetadataExternalId is { Length: > 0 } metadataId)
            {
                local[$"{NovelAniListProvider.ProviderKey}:{metadataId}"] =
                    $"/Novels/Work/{work.Id}";
            }

            if (work.SourceProvider == NcodeNovelSourceProvider.ProviderKey &&
                work.SourceKey.Length > 0)
            {
                local[$"{NcodeNovelSourceProvider.ProviderKey}:{work.SourceKey}"] =
                    $"/Novels/Work/{work.Id}";
            }
        }

        var openRequests = (await requestStore.ListAsync(
                MediaAcquisitionKind.LightNovel,
                requestedByProfileId: null,
                openOnly: true,
                limit: 500,
                cancellationToken))
            .GroupBy(
                request => $"{request.Provider}:{request.ExternalId}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => AcquisitionAccessNames.Status(group.First().Status),
                StringComparer.Ordinal);

        SearchResults = candidates
            .Select(candidate =>
            {
                local.TryGetValue(
                    candidate.Identity,
                    out var localUrl);
                openRequests.TryGetValue(
                    candidate.Identity,
                    out var requestStatus);

                return new NovelCatalogResult(
                    candidate,
                    ReadingSourceCatalog.GetRequired(candidate.Provider),
                    localUrl,
                    requestStatus);
            })
            .ToArray();
    }

    public async Task<IActionResult> OnPostAddCatalogAsync(
        string? provider,
        string? externalId,
        string? title,
        string? nativeTitle,
        string? author,
        string? coverImageUrl,
        string? q,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var normalizedId = externalId?.Trim();

        // The catalog is the one place that knows which sources can be added and what a valid
        // id looks like. Reference and preview sources (BOOK☆WALKER, WebNovel, Internet
        // Archive) list results only; they are rejected here, whatever the form posted.
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(normalizedId) ||
            !ReadingSourceCatalog.TryGet(provider?.Trim(), out var source) ||
            !source.CanAdd ||
            !source.IsValidExternalId(normalizedId))
        {
            return BadRequest();
        }

        var normalizedProvider = source.Key;
        var sourceSettings = await LoadReadingSourceSettingsAsync(
            cancellationToken);
        if (!sourceSettings.IsEnabled(normalizedProvider))
        {
            return BadRequest();
        }

        var capabilities = await requests.GetCapabilitiesAsync(
            MediaAcquisitionKind.LightNovel,
            cancellationToken);
        if (!capabilities.CanRequest)
        {
            return Forbid();
        }

        if (source.DirectImportUrl is { } directImportUrl &&
            capabilities.AutoApproves)
        {
            var sourceUrl = directImportUrl(normalizedId);

            try
            {
                var workId = await operations.RunAsync(
                    new OperationDescriptor(
                        "novel-catalog-import",
                        "Novels",
                        "Import public novel source",
                        title.Trim(),
                        account.ProfileId,
                        OperationLane.Normal,
                        IsDownload: true,
                        Retryable: false),
                    async (operation, token) =>
                    {
                        await operation.ReportAsync(
                            10,
                            "Importing public novel source.",
                            cancellationToken: token);

                        return await imports.ImportWorkAsync(
                            sourceUrl,
                            token);
                    },
                    "Public novel source imported.",
                    cancellationToken);

                return RedirectToPage(
                    "/Novels/Work",
                    new { id = workId });
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError(exception, "Importing public novel source {SourceUrl} failed", sourceUrl);
                TempData["Status"] = Ui["novels.index.catalogImportFailed"];
                return RedirectToPage(
                    new
                    {
                        q = ReadingCatalogSearch.NormalizeQuery(q)
                    });
            }
        }

        try
        {
            var request = await requests.SubmitAsync(
                new AcquisitionRequestDraft(
                    MediaAcquisitionKind.LightNovel,
                    normalizedProvider,
                    normalizedId,
                    title.Trim(),
                    // Subtitle is the author only; the native title is a search alias in the
                    // payload, never an author (#485 item 4).
                    string.IsNullOrWhiteSpace(author)
                        ? null
                        : author.Trim(),
                    string.IsNullOrWhiteSpace(coverImageUrl)
                        ? null
                        : coverImageUrl.Trim(),
                    ReadingAcquisitionEngine.LightNovelDraftPayload(
                        title,
                        nativeTitle,
                        author)),
                cancellationToken);

            if (request.Status == AcquisitionRequestStatus.Completed &&
                request.ResultUrl is { Length: > 0 } resultUrl &&
                resultUrl.StartsWith("/", StringComparison.Ordinal))
            {
                return LocalRedirect(resultUrl);
            }

            var ui = await UiRequestLocalization.GetBundleAsync(
                HttpContext,
                db);
            TempData["Status"] = request.StatusMessage
                ?? ui[
                    "requests.status."
                    + AcquisitionAccessNames.Status(request.Status)];

            return RedirectToPage(
                new
                {
                    q = ReadingCatalogSearch.NormalizeQuery(q)
                });
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }
    }

    public async Task<IActionResult> OnPostImportAsync(
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var capabilities = await requests.GetCapabilitiesAsync(
            MediaAcquisitionKind.LightNovel,
            cancellationToken);
        if (!capabilities.CanRequest || !capabilities.AutoApproves)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var store = new OperationStore(db);
        var operationId = await store.CreateAsync(
            new OperationDescriptor(
                "novel-import",
                "Novels",
                "Import novel source",
                ProfileId: account.ProfileId,
                Lane: OperationLane.Normal,
                IsDownload: true,
                Retryable: false),
            cancellationToken);

        await store.MarkRunningAsync(operationId, cancellationToken);

        try
        {
            var workId = await imports.ImportWorkAsync(sourceUrl, cancellationToken);
            await store.MarkSucceededAsync(
                operationId,
                "Novel metadata and chapter index imported.",
                cancellationToken);

            TempData["Status"] = ui["discover.import.novelImported"];
            return RedirectToPage("/Novels/Work", new { id = workId });
        }
        catch (InvalidOperationException exception)
        {
            await store.MarkFailedAsync(
                operationId,
                $"{exception.GetType().Name}: {exception.Message}",
                CancellationToken.None);
            logger.LogError(exception, "Importing novel source {SourceUrl} failed for operation {OperationId}", sourceUrl, operationId);
            TempData["Status"] = ui["novels.index.importFailed"];
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostUploadEpubAsync(
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
            targetWorkId: null,
            cancellationToken);

        if (outcomes is null)
        {
            var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = ui.Format(
                "novels.index.chooseEpubFiles",
                ("max", NovelEpubUploadRequestLimitsAttribute.MaximumFiles));
            return RedirectToPage();
        }

        TempData["Status"] = NovelEpubImportOutcome.Summarize(outcomes);
        var series = outcomes
            .Where(x => x.Succeeded && x.WorkId is not null)
            .Select(x => x.WorkId!.Value)
            .Distinct()
            .ToArray();

        return series.Length == 1
            ? RedirectToPage("/Novels/Work", new { id = series[0] })
            : RedirectToPage();
    }

    public async Task<IActionResult> OnPostScanInboxAsync(
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await inboxes.InboxAsync(MediaAcquisitionKind.LightNovel, cancellationToken) is null)
        {
            TempData["Status"] = ui["novels.index.configureInboxFirst"];
            return RedirectToPage();
        }

        try
        {
            // The Light Novel inbox folder (Settings → Acquisition → Media folders), imported with
            // the same importer as completed Light Novel downloads.
            var result = await inboxes.RunAsync(
                MediaAcquisitionKind.LightNovel,
                account.ProfileId,
                cancellationToken);

            TempData["Status"] = result.Message;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Scanning the light novel inbox for profile {ProfileId} failed", account.ProfileId);
            TempData["Status"] = ui["novels.index.inboxScanFailed"];
        }

        return RedirectToPage();
    }

    private async Task<ReadingSourceSettingsState> LoadReadingSourceSettingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await sourceSettingsStore.LoadAsync(
                cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
            TempData["Status"] = ui.Format(
                "novels.index.sourceSettingsInvalid",
                ("error", exception.Message));
            return ReadingSourceSettingsState.Default;
        }
    }
}
