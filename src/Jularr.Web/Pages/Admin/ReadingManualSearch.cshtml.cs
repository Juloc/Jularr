using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Admin Manual Search for one Manga or Light Novel request. Searching releases is the only thing here that calls indexers; the request context
/// is local state. A grab posts only an opaque release identity, which the server searches again and re-validates against identity and profile.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class ReadingManualSearchModel(AppDbContext db, ReadingManualSearchService manualSearch, IInstanceModuleService modules, ILogger<ReadingManualSearchModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public ReadingManualSearchTarget? Target { get; private set; }

    public ReadingManualSearchResult? Manual { get; private set; }

    public SearchDepth? ManualDepth { get; private set; }

    public string? Notice => TempData["ReadingManualNotice"] as string;

    public string? Error => TempData["ReadingManualError"] as string;

    public async Task<IActionResult> OnGetAsync(Guid id, string? search, bool refresh, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Target = await manualSearch.GetTargetAsync(id, cancellationToken);
        if (Target is null || !await IsKindEnabledAsync(Target.Kind, cancellationToken))
        {
            return NotFound();
        }

        ManualDepth = search?.ToLowerInvariant() switch
        {
            "fast" => SearchDepth.Fast,
            "normal" => SearchDepth.Normal,
            "deep" => SearchDepth.Deep,
            _ => null
        };
        if (ManualDepth is { } depth && Target.CanSearch)
        {
            Manual = await manualSearch.SearchAsync(id, refresh, depth, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostGrabAsync(Guid id, string? releaseIdentity, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (string.IsNullOrWhiteSpace(releaseIdentity))
        {
            return BadRequest();
        }

        var target = await manualSearch.GetTargetAsync(id, cancellationToken);
        if (target is null || !await IsKindEnabledAsync(target.Kind, cancellationToken))
        {
            return NotFound();
        }

        var outcome = await manualSearch.GrabAsync(id, releaseIdentity, cancellationToken);
        logger.LogInformation("Manual grab for reading request {RequestId} ended as {Status}.", id, outcome.Status);
        TempData[outcome.Status is ManualGrabStatus.Submitted ? "ReadingManualNotice" : "ReadingManualError"] = Ui[outcome.Status switch
        {
            ManualGrabStatus.Submitted => "admin.manualSearch.grabQueued",
            ManualGrabStatus.AlreadySubmitted => "admin.manualSearch.grabAlreadySent",
            ManualGrabStatus.NotSearchable => "admin.manualSearch.grabNotSearchable",
            ManualGrabStatus.ClientRejected => "admin.manualSearch.grabClientRejected",
            ManualGrabStatus.Unrecorded => "admin.manualSearch.grabUnrecorded",
            _ => "admin.manualSearch.grabNotAvailable"
        }];
        return RedirectToPage(new { id });
    }

    private async Task<bool> IsKindEnabledAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        var settings = await modules.GetAsync(cancellationToken);
        return settings.IsEnabled(InstanceModule.Acquisition) && settings.IsEnabled(AcquisitionInstanceModules.For(kind));
    }

    /// <summary>The catalog key of an identity finding of the reading judge, or null for a finding that only has its own sentence (it names the numbers).</summary>
    public static string? IdentityKey(string code) =>
        code switch
        {
            "Title" => "admin.reading.reason.title",
            "VolumeOrChapter" => "admin.reading.reason.volumeOrChapter",
            "TitleDoesNotMatch" => "admin.reading.reason.titleDoesNotMatch",
            _ => null
        };

    public string Coverage(ReadingManualCandidate candidate)
    {
        var parts = new List<string>();
        if (candidate.Volume is { } volume)
        {
            parts.Add(Ui.Format("admin.reading.volume", ("number", volume)));
        }

        if (candidate.Chapters is { } chapters)
        {
            parts.Add(Ui.Format("admin.reading.chapters", ("range", chapters)));
        }

        if (candidate.IsBatch)
        {
            parts.Add(Ui["admin.reading.batch"]);
        }

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    public static string Size(long? bytes) =>
        bytes switch
        {
            null => "—",
            >= 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024 * 1024):0.0} GB",
            >= 1024L * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MB",
            _ => $"{bytes.Value / 1024d:0} KB"
        };

    /// <summary>How a release was found, in the words of the planner: "Title + author · Indexer".</summary>
    public static string FoundVia(ReadingManualCandidate candidate) =>
        candidate.Provenance.Count == 0 ? string.Empty : $"{candidate.Provenance[0].Description} · {candidate.Provenance[0].IndexerName}";

    public static string VerdictTone(ManualSearchVerdict verdict) =>
        verdict switch
        {
            ManualSearchVerdict.Eligible => "success",
            ManualSearchVerdict.Warning => "warning",
            _ => "danger"
        };

    public string ConfidenceLabel(IdentityConfidence confidence) => Ui[$"admin.reading.confidence.{confidence.ToString().ToLowerInvariant()}"];
}
