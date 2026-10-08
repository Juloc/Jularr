using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public enum BookManualSearchTab
{
    Search,
    Current,
    History
}

/// <summary>
/// Admin manual search for one Book request. Search is the only tab that calls indexers; Current
/// and History are local request context. POST carries only an opaque release identity.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class BookManualSearchModel(
    AppDbContext db,
    BookManualSearchService manualSearch,
    ILogger<BookManualSearchModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public BookManualSearchTarget? Target { get; private set; }
    public BookManualSearchResult? Result { get; private set; }
    public IReadOnlyList<RankedBookRelease> Candidates { get; private set; } = [];
    public RankedBookRelease? Selected { get; private set; }
    public IReadOnlyList<string> Indexers { get; private set; } = [];

    public BookManualSearchTab ActiveTab { get; private set; } = BookManualSearchTab.Search;
    public string? Query { get; private set; }
    public string? State { get; private set; }
    public string? Sort { get; private set; }
    public string? Indexer { get; private set; }
    public string? SelectedIdentity { get; private set; }

    public string? Notice => TempData["BookManualSearchNotice"] as string;
    public string? Error => TempData["BookManualSearchError"] as string;

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string? tab,
        string? q,
        string? state,
        string? sort,
        string? indexer,
        string? release,
        bool refresh,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(
            HttpContext,
            db);

        ActiveTab = ParseTab(tab);
        Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        State = Normalize(state);
        Sort = Normalize(sort);
        Indexer = string.IsNullOrWhiteSpace(indexer) ? null : indexer.Trim();
        SelectedIdentity = string.IsNullOrWhiteSpace(release) ? null : release.Trim();

        try
        {
            Target = await manualSearch.LoadAsync(
                id,
                cancellationToken);

            if (ActiveTab != BookManualSearchTab.Search)
            {
                return Page();
            }

            Result = await manualSearch.SearchAsync(
                id,
                cancellationToken,
                refresh);
            Indexers = Result.Search.Ranked
                .Select(candidate => candidate.Release.Indexer)
                .OfType<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Selected = SelectedIdentity is null
                ? null
                : Result.Search.Ranked.FirstOrDefault(candidate =>
                    candidate.Release.Identity.Equals(
                        SelectedIdentity,
                        StringComparison.Ordinal));

            Candidates = ApplyFilters(
                Result,
                Query,
                State,
                Sort,
                Indexer);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Book manual search could not open request {RequestId}.",
                id);
            return NotFound();
        }
    }

    public async Task<IActionResult> OnPostGrabAsync(
        Guid id,
        string? releaseIdentity,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(
            HttpContext,
            db);

        if (string.IsNullOrWhiteSpace(releaseIdentity))
        {
            TempData["BookManualSearchError"] =
                Ui["books.index.searchUnavailable"];
            return RedirectToPage(new
            {
                id,
                tab = "search"
            });
        }

        try
        {
            var request = await manualSearch.GrabAsync(
                id,
                releaseIdentity,
                cancellationToken);
            TempData["Status"] =
                request.StatusMessage
                ?? request.Title;
            return RedirectToPage("/Admin/Wanted");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Book manual grab failed for request {RequestId}.",
                id);
            TempData["BookManualSearchError"] =
                Ui["books.index.searchUnavailable"];
            return RedirectToPage(new
            {
                id,
                tab = "search"
            });
        }
    }

    public static BookManualSearchTab ParseTab(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "current" => BookManualSearchTab.Current,
            "history" => BookManualSearchTab.History,
            _ => BookManualSearchTab.Search
        };

    public static string TabName(BookManualSearchTab tab) =>
        tab switch
        {
            BookManualSearchTab.Current => "current",
            BookManualSearchTab.History => "history",
            _ => "search"
        };

    public static string Size(long? bytes) => bytes switch
    {
        null => "—",
        >= 1024L * 1024 * 1024 =>
            $"{bytes.Value / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 =>
            $"{bytes.Value / (1024d * 1024):0.0} MB",
        _ => $"{bytes.Value / 1024d:0} KB"
    };

    public static string Format(AcquisitionCandidate release)
    {
        var title = release.Title;
        if (title.Contains("epub", StringComparison.OrdinalIgnoreCase))
        {
            return "EPUB";
        }

        if (title.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "PDF";
        }

        return "Unknown";
    }

    public static string StatusName(
        BookManualSearchResult result,
        RankedBookRelease candidate) =>
        candidate.RejectedBecause is not null
            ? "rejected"
            : result.TriedReleases.Contains(candidate.Release.Identity)
                ? "tried"
                : "eligible";

    private static IReadOnlyList<RankedBookRelease> ApplyFilters(
        BookManualSearchResult result,
        string? query,
        string? state,
        string? sort,
        string? indexer)
    {
        IEnumerable<RankedBookRelease> candidates =
            result.Search.Ranked;

        if (!string.IsNullOrWhiteSpace(query))
        {
            candidates = candidates.Where(candidate =>
                candidate.Release.Title.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(indexer))
        {
            candidates = candidates.Where(candidate =>
                string.Equals(
                    candidate.Release.Indexer,
                    indexer,
                    StringComparison.OrdinalIgnoreCase));
        }

        candidates = state switch
        {
            "eligible" => candidates.Where(candidate =>
                StatusName(result, candidate) == "eligible"),
            "rejected" => candidates.Where(candidate =>
                StatusName(result, candidate) == "rejected"),
            "tried" => candidates.Where(candidate =>
                StatusName(result, candidate) == "tried"),
            _ => candidates
        };

        candidates = sort switch
        {
            "age" => candidates
                .OrderBy(candidate =>
                    candidate.Release.AgeHours
                    ?? double.MaxValue),
            "size" => candidates
                .OrderBy(candidate =>
                    candidate.Release.SizeBytes
                    ?? long.MaxValue),
            "title" => candidates
                .OrderBy(candidate =>
                    candidate.Release.Title,
                    StringComparer.OrdinalIgnoreCase),
            _ => candidates
                .OrderByDescending(candidate =>
                    candidate.Score)
                .ThenByDescending(candidate =>
                    candidate.Release.PublishedAt)
        };

        return candidates.ToArray();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
}
