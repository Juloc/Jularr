using Jularr.Web.Data;
using Jularr.Web.Features.Artwork;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Manga;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Discover;

[MangaUploadRequestLimits("Upload")]
public sealed class MangaImportModel(
    AppDbContext db,
    CurrentAccountContext account,
    IHttpClientFactory httpClientFactory,
    OperationRunner operations,
    ILogger<MangaImportModel> logger,
    ReadingCoverArtwork? coverArtwork = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public string AniListId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string AniListUrl => $"https://anilist.co/manga/{AniListId}";

    public async Task<IActionResult> OnGetAsync(
        string? anilistId,
        string? title)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!TryNormalizeSelection(
                anilistId,
                title,
                out var normalizedId,
                out var normalizedTitle))
        {
            return BadRequest(Ui["discover.mangaImport.invalidSelection"]);
        }

        AniListId = normalizedId;
        Title = normalizedTitle;
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(
        string? anilistId,
        string? title,
        string? seriesTitle,
        IFormFile[]? archives,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!TryNormalizeSelection(
                anilistId,
                title,
                out var normalizedId,
                out var normalizedTitle))
        {
            return BadRequest(Ui["discover.mangaImport.invalidSelection"]);
        }

        try
        {
            return await operations.RunAsync(
                new OperationDescriptor(
                    "discover-manga-upload-import",
                    "Manga",
                    "Import discovered Manga",
                    normalizedTitle,
                    account.ProfileId,
                    OperationLane.Normal,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        5,
                        "Validating uploaded Manga archives.",
                        cancellationToken: token);

                    var upload = new MangaUploadService();
                    var sourcePath = await upload.SaveSeriesAsync(
                        string.IsNullOrWhiteSpace(seriesTitle)
                            ? normalizedTitle
                            : seriesTitle,
                        archives ?? [],
                        token);

                    await operation.ReportAsync(
                        35,
                        "Importing Manga chapters and pages.",
                        cancellationToken: token);

                    return await ImportAndMatchAsync(
                        sourcePath,
                        normalizedId,
                        normalizedTitle,
                        operation,
                        token);
                },
                "Discovered Manga imported.",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Uploading discovered Manga {Title} failed", normalizedTitle);
            TempData["Status"] = Ui["discover.mangaImport.importFailed"];
            return RedirectToPage(new
            {
                anilistId = normalizedId,
                title = normalizedTitle
            });
        }
    }

    public async Task<IActionResult> OnPostImportPathAsync(
        string? anilistId,
        string? title,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (!account.IsOwner)
        {
            return Forbid();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!TryNormalizeSelection(
                anilistId,
                title,
                out var normalizedId,
                out var normalizedTitle))
        {
            return BadRequest(Ui["discover.mangaImport.invalidSelection"]);
        }

        try
        {
            return await operations.RunAsync(
                new OperationDescriptor(
                    "discover-manga-path-import",
                    "Manga",
                    "Import discovered Manga",
                    normalizedTitle,
                    account.ProfileId,
                    OperationLane.Normal,
                    Retryable: false),
                (operation, token) => ImportAndMatchAsync(
                    sourcePath ?? "",
                    normalizedId,
                    normalizedTitle,
                    operation,
                    token),
                "Discovered Manga imported.",
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Importing discovered Manga {Title} from path failed", normalizedTitle);
            TempData["Status"] = Ui["discover.mangaImport.importFailed"];
            return RedirectToPage(new
            {
                anilistId = normalizedId,
                title = normalizedTitle
            });
        }
    }

    private async Task<IActionResult> ImportAndMatchAsync(
        string sourcePath,
        string aniListId,
        string title,
        OperationExecutionContext operation,
        CancellationToken cancellationToken)
    {
        var repository = new MangaRepository(db);
        var importer = new MangaImportService(repository);
        var result = await importer.ImportAsync(
            sourcePath,
            cancellationToken);

        await operation.ReportAsync(
            80,
            "Matching imported Manga metadata.",
            cancellationToken: cancellationToken);

        var metadata = new MangaAniListService(
            repository,
            httpClientFactory,
            coverArtwork: coverArtwork);

        try
        {
            await metadata.MatchAsync(
                result.SeriesId,
                aniListId,
                cancellationToken);

            TempData["Status"] = Ui.Format(
                "discover.mangaImport.imported",
                ("chapters", result.ChapterCount),
                ("pages", result.PageCount),
                ("title", title));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or HttpRequestException
                or TaskCanceledException)
        {
            await operation.LogAsync(
                OperationLogLevel.Warning,
                "MangaMetadata",
                "Manga import completed, but AniList matching needs attention.",
                CancellationToken.None);

            TempData["Status"] = Ui.Format(
                "discover.mangaImport.matchFailed",
                ("reason", exception.Message));
        }

        return RedirectToPage(
            "/Manga/Series",
            new { id = result.SeriesId });
    }

    public static bool TryNormalizeSelection(
        string? anilistId,
        string? title,
        out string normalizedId,
        out string normalizedTitle)
    {
        normalizedId = "";
        normalizedTitle = "";

        if (!int.TryParse(anilistId, out var id) || id <= 0)
        {
            return false;
        }

        normalizedId = id.ToString();
        normalizedTitle = title?.Trim() ?? "";
        if (normalizedTitle.Length == 0)
        {
            normalizedTitle = $"AniList manga {normalizedId}";
        }
        else if (normalizedTitle.Length > 200)
        {
            normalizedTitle = normalizedTitle[..200];
        }

        return true;
    }
}
