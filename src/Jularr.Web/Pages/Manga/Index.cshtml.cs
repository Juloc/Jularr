using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.Operations;
using Jularr.Web.Features.ReadingDiscovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Manga;

public sealed record MangaCatalogResult(
    ReadingCatalogCandidate Item,
    string? LocalUrl,
    string? RequestStatus);

[MangaUploadRequestLimits("Upload")]
public sealed class IndexModel(
    AppDbContext db,
    CurrentAccountContext account,
    OperationRunner operations,
    IHttpClientFactory httpClientFactory,
    MediaMappingReviewStore mappingReviewStore,
    ILogger<IndexModel> logger,
    NovelAniListProvider? readingProvider = null,
    AcquisitionRequestService? requests = null,
    AcquisitionAccessStore? requestStore = null,
    ReadingCoverArtwork? coverArtwork = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<MangaSeriesItem> Series { get; private set; } = [];
    public IReadOnlyList<MangaSeriesItem> ContinueReading { get; private set; } = [];
    public IReadOnlyList<MangaCatalogResult> SearchResults { get; private set; } = [];
    public AcquisitionCapabilities Access { get; private set; } =
        AcquisitionCapabilities.Default(MediaAcquisitionKind.Manga);
    public string SearchQuery { get; private set; } = "";
    public bool IsOwner => account.IsOwner;

    public async Task OnGetAsync(
        CancellationToken cancellationToken,
        string? q = null)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var repository = new MangaRepository(db);
        Series = await repository.GetLibraryAsync(
            account.ProfileId,
            cancellationToken);
        ContinueReading = Series
            .Where(x => x.HasProgress)
            .OrderByDescending(x => x.LastReadAt)
            .Take(8)
            .ToArray();

        SearchQuery = ReadingCatalogSearch.NormalizeQuery(q);
        if (readingProvider is null ||
            requests is null ||
            requestStore is null)
        {
            return;
        }

        Access = await requests.GetCapabilitiesAsync(
            MediaAcquisitionKind.Manga,
            cancellationToken);

        if (SearchQuery.Length == 0)
        {
            return;
        }

        var candidates = await ReadingCatalogSearch.SearchMangaAsync(
            readingProvider,
            SearchQuery,
            18,
            cancellationToken);

        var localMatches = await repository.GetAniListMatchesAsync(
            candidates
                .Where(candidate =>
                    candidate.Provider.Equals(
                        NovelAniListProvider.ProviderKey,
                        StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.ExternalId)
                .ToArray(),
            cancellationToken);

        var openRequests = (await requestStore.ListAsync(
                MediaAcquisitionKind.Manga,
                requestedByProfileId: null,
                openOnly: true,
                limit: 500,
                cancellationToken))
            .Where(request =>
                request.Provider.Equals(
                    NovelAniListProvider.ProviderKey,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(request => request.ExternalId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => AcquisitionAccessNames.Status(group.First().Status),
                StringComparer.Ordinal);

        SearchResults = candidates
            .Select(candidate =>
            {
                var localUrl = localMatches.TryGetValue(
                    candidate.ExternalId,
                    out var seriesId)
                        ? $"/Manga/Series/{seriesId}"
                        : null;

                openRequests.TryGetValue(
                    candidate.ExternalId,
                    out var requestStatus);

                return new MangaCatalogResult(
                    candidate,
                    localUrl,
                    requestStatus);
            })
            .ToArray();
    }

    public async Task<IActionResult> OnPostAddAsync(
        string? externalId,
        string? title,
        string? nativeTitle,
        string? coverImageUrl,
        string? q,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(externalId, out var id) ||
            id <= 0 ||
            string.IsNullOrWhiteSpace(title))
        {
            return BadRequest();
        }

        if (requests is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var request = await requests.SubmitAsync(
                new AcquisitionRequestDraft(
                    MediaAcquisitionKind.Manga,
                    NovelAniListProvider.ProviderKey,
                    id.ToString(),
                    title.Trim(),
                    string.IsNullOrWhiteSpace(nativeTitle)
                        ? null
                        : nativeTitle.Trim(),
                    string.IsNullOrWhiteSpace(coverImageUrl)
                        ? null
                        : coverImageUrl.Trim()),
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
                ?? ui[ConsumerAcquisitionLabels.StatusKey(request.Status)];

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

    public async Task<IActionResult> OnPostUploadAsync(
        string? seriesTitle,
        IFormFile[]? archives,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            var result = await operations.RunAsync(
                new OperationDescriptor(
                    "manga-upload-import",
                    "Manga",
                    "Import uploaded Manga",
                    string.IsNullOrWhiteSpace(seriesTitle) ? null : seriesTitle.Trim(),
                    account.ProfileId,
                    OperationLane.Normal,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        5,
                        "Validating uploaded Manga archives.",
                        cancellationToken: token);

                    var repository = new MangaRepository(db);
                    var upload = new MangaUploadService();
                    var sourcePath = await upload.SaveSeriesAsync(
                        seriesTitle,
                        archives ?? [],
                        token);

                    await operation.ReportAsync(
                        35,
                        "Importing Manga chapters and pages.",
                        cancellationToken: token);

                    var importer = new MangaImportService(repository);
                    var imported = await importer.ImportAsync(
                        sourcePath,
                        token);
                    var metadata = new MangaAniListService(
                        repository,
                        httpClientFactory,
                        mappingReviewStore,
                        coverArtwork: coverArtwork);
                    await metadata.AutoMatchAsync(
                        imported.SeriesId,
                        token);
                    return imported;
                },
                "Manga upload imported.",
                cancellationToken);

            TempData["Status"] = Ui.Format(
                "manga.status.imported",
                ("chapters", result.ChapterCount),
                ("pages", result.PageCount));
            return RedirectToPage(
                "/Manga/Series",
                new { id = result.SeriesId });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Uploading Manga series {SeriesTitle} failed", seriesTitle);
            TempData["Status"] = Ui["manga.status.uploadFailed"];
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostImportAsync(
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        try
        {
            var result = await operations.RunAsync(
                new OperationDescriptor(
                    "manga-path-import",
                    "Manga",
                    "Import mounted Manga source",
                    ProfileId: account.ProfileId,
                    Lane: OperationLane.Normal,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        10,
                        "Scanning mounted Manga source.",
                        cancellationToken: token);

                    var repository = new MangaRepository(db);
                    var importer = new MangaImportService(repository);
                    var imported = await importer.ImportAsync(
                        sourcePath ?? "",
                        token);
                    var metadata = new MangaAniListService(
                        repository,
                        httpClientFactory,
                        mappingReviewStore,
                        coverArtwork: coverArtwork);
                    await metadata.AutoMatchAsync(
                        imported.SeriesId,
                        token);
                    return imported;
                },
                "Mounted Manga source imported.",
                cancellationToken);

            TempData["Status"] = Ui.Format(
                "manga.status.imported",
                ("chapters", result.ChapterCount),
                ("pages", result.PageCount));
            return RedirectToPage(
                "/Manga/Series",
                new { id = result.SeriesId });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Importing mounted Manga source {SourcePath} failed", sourcePath);
            TempData["Status"] = Ui["manga.status.pathImportFailed"];
            return RedirectToPage();
        }
    }
}
