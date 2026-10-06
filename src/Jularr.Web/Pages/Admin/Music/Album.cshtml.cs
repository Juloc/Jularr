using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.Music;

/// <summary>
/// One album: where it stands, its tracks with their files, a "Search now" and the Manual Search. Searching releases is the only thing here
/// that calls indexers; everything else is local state. A grab posts only an opaque release identity.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class AlbumModel(AppDbContext db, MusicAdminQuery query, MusicManualSearchService manualSearch, MusicLibraryService library, ILogger<AlbumModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public MusicAlbumView? View { get; private set; }

    public MusicManualSearchResult? Manual { get; private set; }

    public SearchDepth? ManualDepth { get; private set; }

    public string? Notice => TempData["MusicNotice"] as string;

    public string? Error => TempData["MusicError"] as string;

    public async Task<IActionResult> OnGetAsync(Guid workId, string? search, bool refresh, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        View = await query.GetAlbumAsync(workId, cancellationToken);
        if (View is null)
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
        if (ManualDepth is { } depth)
        {
            Manual = await manualSearch.SearchAsync(workId, refresh, depth, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSearchNowAsync(Guid workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var message = await manualSearch.SearchNowAsync(workId, ProfileId, cancellationToken);
        if (message is null)
        {
            return NotFound();
        }

        TempData["MusicNotice"] = message;
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostMonitorAsync(Guid workId, bool monitored, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            await library.SetAlbumMonitoredAsync(workId, monitored, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        TempData["MusicNotice"] = Ui["admin.music.saved"];
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostGrabAsync(Guid workId, string? releaseIdentity, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (string.IsNullOrWhiteSpace(releaseIdentity))
        {
            return BadRequest();
        }

        var outcome = await manualSearch.GrabAsync(workId, ProfileId, releaseIdentity, cancellationToken);
        if (outcome.Status == MusicGrabStatus.NotFound)
        {
            return NotFound();
        }

        logger.LogInformation("Manual grab for album {WorkId} ended as {Status}.", workId, outcome.Status);
        TempData[outcome.Status is MusicGrabStatus.Submitted ? "MusicNotice" : "MusicError"] = Ui[outcome.Status switch
        {
            MusicGrabStatus.Submitted => "admin.manualSearch.grabQueued",
            MusicGrabStatus.AlreadySubmitted => "admin.manualSearch.grabAlreadySent",
            MusicGrabStatus.NotSearchable => "admin.manualSearch.grabNotSearchable",
            MusicGrabStatus.ClientRejected => "admin.manualSearch.grabClientRejected",
            MusicGrabStatus.Unrecorded => "admin.manualSearch.grabUnrecorded",
            _ => "admin.manualSearch.grabNotAvailable"
        }];
        return RedirectToPage(new { workId });
    }

    private string ProfileId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "owner";

    public string StateLabel(MusicAlbumState state) => Ui[$"admin.music.state.{state.ToString().ToLowerInvariant()}"];

    public string TypeLabel(MusicAlbumType type) => Ui[$"admin.music.type.{type.ToString().ToLowerInvariant()}"];

    /// <summary>The catalog key of an identity finding of the Music judge, or null for a code that only has its own sentence.</summary>
    public static string? IdentityKey(string code) =>
        code switch
        {
            "Matches" => "admin.music.reason.matches",
            "WrongAlbum" => "admin.music.reason.wrongAlbum",
            "WrongArtist" => "admin.music.reason.wrongArtist",
            "DifferentRelease" => "admin.music.reason.differentRelease",
            "Collection" => "admin.music.reason.collection",
            _ => null
        };

    public static string Size(long? bytes) =>
        bytes switch
        {
            null => "—",
            >= 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024 * 1024):0.0} GB",
            >= 1024L * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MB",
            _ => $"{bytes.Value / 1024d:0} KB"
        };

    /// <summary>How a release was found, in the words of the planner: "Artist + album · Indexer".</summary>
    public static string FoundVia(MusicManualCandidate candidate) =>
        candidate.Provenance.Count == 0 ? string.Empty : $"{candidate.Provenance[0].Description} · {candidate.Provenance[0].IndexerName}";

    public static string VerdictTone(Jularr.Web.Features.Acquisition.ManualSearch.ManualSearchVerdict verdict) =>
        verdict switch
        {
            Jularr.Web.Features.Acquisition.ManualSearch.ManualSearchVerdict.Eligible => "success",
            Jularr.Web.Features.Acquisition.ManualSearch.ManualSearchVerdict.Warning => "warning",
            _ => "danger"
        };
}
