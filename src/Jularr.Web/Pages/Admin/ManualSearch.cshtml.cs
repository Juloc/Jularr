using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public enum ManualSearchTab
{
    Search,
    Current,
    History
}

/// <summary>
/// The shared Admin Manual Search surface for one Movie or TV request: normalized release candidates with the same score and rejection
/// explanation as automatic acquisition, and an explicit, separate select-and-download action. Search is the only tab that calls the
/// indexers; Current and History are request context. A POST carries only the request, the episode and an opaque release identity.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class ManualSearchModel(AppDbContext db, VideoManualSearchService manualSearch) : PageModel
{
    // Identities are provider guids plus an indexer id; anything longer is not a release this page produced.
    private const int MaxReleaseIdentityLength = 512;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public ManualSearchTarget Target { get; private set; } = null!;
    public ManualSearchResult? Result { get; private set; }
    public IReadOnlyList<ManualSearchCandidate> Candidates { get; private set; } = [];
    public ManualSearchCandidate? Selected { get; private set; }
    public IReadOnlyList<string> Indexers { get; private set; } = [];
    public IReadOnlyList<string> Qualities { get; private set; } = [];

    public ManualSearchTab ActiveTab { get; private set; } = ManualSearchTab.Search;
    public Guid? UnitId { get; private set; }
    public string? Query { get; private set; }
    public string? State { get; private set; }
    public string? Sort { get; private set; }
    public string? Indexer { get; private set; }
    public string? ReleaseType { get; private set; }
    public string? Quality { get; private set; }

    public string? Notice => TempData["ManualSearchNotice"] as string;
    public string? Error => TempData["ManualSearchError"] as string;

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        Guid? unit,
        string? tab,
        string? q,
        string? state,
        string? sort,
        string? indexer,
        string? type,
        string? quality,
        string? release,
        bool refresh,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        ActiveTab = ParseTab(tab);
        UnitId = unit;
        Query = Clean(q);
        State = Clean(state)?.ToLowerInvariant();
        Sort = Clean(sort)?.ToLowerInvariant();
        Indexer = Clean(indexer);
        ReleaseType = Clean(type);
        Quality = Clean(quality);

        if (ActiveTab != ManualSearchTab.Search)
        {
            return await manualSearch.LoadAsync(id, unit, cancellationToken) is { } target ? Show(target) : NotFound();
        }

        Result = await manualSearch.SearchAsync(id, unit, refresh, cancellationToken);
        if (Result is null)
        {
            return NotFound();
        }

        Target = Result.Target;
        Indexers = Distinct(Result.Candidates.Select(candidate => candidate.Indexer));
        Qualities = Distinct(Result.Candidates.Select(candidate => candidate.Quality));
        Selected = string.IsNullOrWhiteSpace(release) ? null : Result.Candidates.FirstOrDefault(candidate => candidate.Identity == release.Trim());
        Candidates = ApplyFilters(Result.Candidates);
        return Page();
    }

    public async Task<IActionResult> OnPostGrabAsync(Guid id, Guid? unit, string? releaseIdentity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseIdentity) || releaseIdentity.Length > MaxReleaseIdentityLength)
        {
            return BadRequest();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        ManualGrabOutcome outcome;
        try
        {
            outcome = await manualSearch.GrabAsync(id, unit, releaseIdentity.Trim(), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        if (outcome.Status is ManualGrabStatus.Submitted or ManualGrabStatus.AlreadySubmitted)
        {
            TempData["Status"] = outcome.Status == ManualGrabStatus.Submitted ? Ui["admin.manualSearch.grabQueued"] : Ui["admin.manualSearch.grabAlreadySent"];
            return RedirectToPage("/Admin/Wanted");
        }

        TempData["ManualSearchError"] = outcome.Status switch
        {
            ManualGrabStatus.NotSearchable => Ui["admin.manualSearch.grabNotSearchable"],
            ManualGrabStatus.ClientRejected => Ui["admin.manualSearch.grabClientRejected"],
            _ => Ui["admin.manualSearch.grabNotAvailable"]
        };
        return RedirectToPage(new { id, unit, tab = "search" });
    }

    public static ManualSearchTab ParseTab(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "current" => ManualSearchTab.Current,
            "history" => ManualSearchTab.History,
            _ => ManualSearchTab.Search
        };

    public static string TabName(ManualSearchTab tab) =>
        tab switch
        {
            ManualSearchTab.Current => "current",
            ManualSearchTab.History => "history",
            _ => "search"
        };

    public static string Size(long? bytes) =>
        bytes switch
        {
            null => "—",
            >= 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024 * 1024):0.0} GB",
            >= 1024L * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MB",
            _ => $"{bytes.Value / 1024d:0} KB"
        };

    public static string VerdictName(ManualSearchVerdict verdict) => verdict.ToString().ToLowerInvariant();

    public static string ReasonKey(ManualSearchReasonCode code) => $"admin.manualSearch.reason.{char.ToLowerInvariant(code.ToString()[0])}{code.ToString()[1..]}";

    public static string TypeKey(ManualSearchReleaseType type) => $"admin.manualSearch.type.{char.ToLowerInvariant(type.ToString()[0])}{type.ToString()[1..]}";

    /// <summary>The reason a candidate row leads with: why it is rejected or warned about, otherwise that it matches the target.</summary>
    public static ManualSearchReason Headline(ManualSearchCandidate candidate)
    {
        var neutral = new[] { ManualSearchReasonCode.MatchesTarget, ManualSearchReasonCode.ContainsTarget, ManualSearchReasonCode.AlreadyTried };
        var leading = candidate.Verdict == ManualSearchVerdict.Rejected
            ? candidate.Reasons.FirstOrDefault(reason => !neutral.Contains(reason.Code))
            : candidate.Reasons.FirstOrDefault(reason => reason.Code == ManualSearchReasonCode.LowerQuality);
        return leading ?? candidate.Reasons[0];
    }

    private PageResult Show(ManualSearchTarget target)
    {
        Target = target;
        return Page();
    }

    private IReadOnlyList<ManualSearchCandidate> ApplyFilters(IReadOnlyList<ManualSearchCandidate> candidates)
    {
        var filtered = candidates.AsEnumerable();
        if (Query is not null)
        {
            filtered = filtered.Where(candidate => candidate.Title.Contains(Query, StringComparison.OrdinalIgnoreCase) || candidate.ReleaseGroup?.Contains(Query, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (Indexer is not null)
        {
            filtered = filtered.Where(candidate => string.Equals(candidate.Indexer, Indexer, StringComparison.OrdinalIgnoreCase));
        }

        if (Quality is not null)
        {
            filtered = filtered.Where(candidate => string.Equals(candidate.Quality, Quality, StringComparison.OrdinalIgnoreCase));
        }

        if (ReleaseType is not null)
        {
            filtered = filtered.Where(candidate => candidate.ReleaseType.ToString().Equals(ReleaseType, StringComparison.OrdinalIgnoreCase));
        }

        filtered = State switch
        {
            "eligible" => filtered.Where(candidate => candidate.Verdict == ManualSearchVerdict.Eligible),
            "warning" => filtered.Where(candidate => candidate.Verdict == ManualSearchVerdict.Warning),
            "rejected" => filtered.Where(candidate => candidate.Verdict == ManualSearchVerdict.Rejected),
            "tried" => filtered.Where(candidate => candidate.IsTried),
            _ => filtered
        };

        return (Sort switch
        {
            "age" => filtered.OrderBy(candidate => candidate.AgeDays ?? int.MaxValue),
            "size" => filtered.OrderBy(candidate => candidate.SizeBytes ?? long.MaxValue),
            "title" => filtered.OrderBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(candidate => candidate.Verdict == ManualSearchVerdict.Rejected).ThenByDescending(candidate => candidate.Score ?? int.MinValue)
        }).ToArray();
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values) =>
        values.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
