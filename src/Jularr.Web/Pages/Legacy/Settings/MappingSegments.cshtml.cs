using System.ComponentModel.DataAnnotations;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaMapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Settings;

[Authorize(Policy = JularrPolicies.MappingEdit)]
public sealed class MappingSegmentsModel(
    ReadingSegmentMappingStore segmentMappings,
    CurrentAccountContext account,
    AppDbContext db,
    ILogger<MappingSegmentsModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<ReadingMediaSegmentMapping> Mappings { get; private set; } = [];

    [BindProperty]
    [Required]
    public string MediaType { get; set; } = "manga";

    [BindProperty]
    [Required]
    public string LocalId { get; set; } = "";

    [BindProperty]
    public double LocalChapterStart { get; set; } = 1;

    [BindProperty]
    public double LocalChapterEnd { get; set; } = 1;

    [BindProperty]
    public int RemoteChapterStart { get; set; } = 1;

    [BindProperty]
    [Required]
    public string ExternalId { get; set; } = "";

    [BindProperty]
    public string? PreferredTitle { get; set; }

    [BindProperty]
    public int? RemoteChapterCount { get; set; }

    [BindProperty]
    public int? LocalVolumeStart { get; set; }

    [BindProperty]
    public int? LocalVolumeEnd { get; set; }

    [BindProperty]
    public int? RemoteVolumeStart { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string? mediaType,
        string? localId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            MediaType = mediaType.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(localId))
        {
            LocalId = localId.Trim();
        }

        Mappings = await segmentMappings.ListAsync(
            cancellationToken: cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (!Guid.TryParse(LocalId, out var localId))
        {
            ModelState.AddModelError(
                nameof(LocalId),
                Ui["settings.mappingSegments.validation.localId"]);
        }

        if (!int.TryParse(ExternalId, out var remoteId) || remoteId <= 0)
        {
            ModelState.AddModelError(
                nameof(ExternalId),
                Ui["settings.mappingSegments.validation.externalId"]);
        }

        if (!ModelState.IsValid)
        {
            Mappings = await segmentMappings.ListAsync(
                cancellationToken: cancellationToken);
            return Page();
        }

        try
        {
            await segmentMappings.AddAsync(
                new ReadingMediaSegmentMapping(
                    Guid.NewGuid(),
                    MediaType,
                    localId.ToString(),
                    LocalChapterStart,
                    LocalChapterEnd,
                    RemoteChapterStart,
                    "anilist",
                    remoteId.ToString(),
                    PreferredTitle,
                    RemoteChapterCount,
                    LocalVolumeStart,
                    LocalVolumeEnd,
                    RemoteVolumeStart,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            TempData["Status"] = Ui["settings.mappingSegments.added"];
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Adding reading segment mapping for {MediaType} {LocalId} failed", MediaType, localId);
            TempData["Status"] = Ui["settings.mappingSegments.addFailed"];
        }

        return RedirectToPage(
            new
            {
                mediaType = MediaType,
                localId = localId.ToString()
            });
    }

    public async Task<IActionResult> OnPostRemoveAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var removed = await segmentMappings.RemoveAsync(
            id,
            cancellationToken);
        TempData["Status"] = removed
            ? Ui["settings.mappingSegments.removed"]
            : Ui["settings.mappingSegments.alreadyGone"];

        return RedirectToPage();
    }

    public static string? TargetUrl(ReadingMediaSegmentMapping mapping)
    {
        if (!Guid.TryParse(mapping.LocalId, out var id))
        {
            return null;
        }

        return mapping.MediaType.ToLowerInvariant() switch
        {
            "manga" => $"/Manga/Series/{id}",
            "novel" => $"/Novels/Work/{id}",
            _ => null
        };
    }
}
