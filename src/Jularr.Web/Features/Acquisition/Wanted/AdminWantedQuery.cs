using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Monitoring;

namespace Jularr.Web.Features.Acquisition.Wanted;

/// <summary>The state tabs of Admin → Wanted.</summary>
public enum AdminWantedTab
{
    All,

    /// <summary>Approved and handed to acquisition, not searched yet.</summary>
    Requested,

    /// <summary>Searched (or monitored) without a release yet; the next search is scheduled.</summary>
    Missing,

    /// <summary>A search is queued or running, or a release was found and is downloading or importing.</summary>
    Searching,

    Failed
}

/// <summary>What a wanted row is exactly; the tabs group these.</summary>
public enum WantedStatus
{
    Requested,
    Missing,
    Searching,
    Downloading,
    Importing,
    Failed
}

/// <summary>Where a wanted row comes from; it decides which actions exist.</summary>
public enum WantedSource
{
    /// <summary>An approved acquisition request (Anime, Manga, Light Novel, Book, Movie, TV, Audiobook).</summary>
    Request,

    /// <summary>A monitored unit the monitoring engine found missing or below the quality cutoff (anime episodes).</summary>
    Monitored
}

public enum AdminWantedSort
{
    /// <summary>The most recently searched first; rows that were never searched last.</summary>
    LastSearch,

    Title,

    /// <summary>The row that has been wanted the longest first.</summary>
    Waiting
}

/// <summary>One thing Jularr still needs, from any source, in the shape the Wanted page shows.</summary>
public sealed record WantedRow(
    string Id,
    WantedSource Source,
    MediaAcquisitionKind Kind,
    string Title,
    WantedStatus Status,
    DateTime SinceUtc)
{
    public Guid? RequestId { get; init; }

    /// <summary>The library key of the anime of a monitored unit; the target of an automatic search.</summary>
    public string? AnimeKey { get; init; }

    public Guid? AnimeId { get; init; }

    /// <summary>The canonical Work of a Movie or TV row; the target of its Admin media page and detail link.</summary>
    public Guid? WorkId { get; init; }

    /// <summary>The first missing episode of a TV season row; the episode Manual Search opens on.</summary>
    public Guid? UnitId { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public int? Volume { get; init; }

    public double? ChapterStart { get; init; }

    public double? ChapterEnd { get; init; }

    /// <summary>A request for seasons or single episodes only (anime requests).</summary>
    public RequestScope Scope { get; init; } = RequestScope.WholeSeries;

    /// <summary>The seasons or episodes of <see cref="Scope"/> as compact text such as <c>S01E01-06</c>.</summary>
    public string? Selection { get; init; }

    /// <summary>The monitored file exists but is below the quality cutoff.</summary>
    public bool IsUpgrade { get; init; }

    /// <summary>The language tags the row asks for; empty when the row carries none.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    public string? ProfileId { get; init; }

    public string? ProfileName { get; init; }

    public DateTime? LastSearchUtc { get; init; }

    public DateTime? NextSearchUtc { get; init; }

    /// <summary>How many attempts failed (monitored units).</summary>
    public int Failures { get; init; }

    /// <summary>The state of the last attempt of a monitored unit.</summary>
    public AcquisitionAttemptStatus Attempt { get; init; }

    /// <summary>The last thing the acquisition said about this row.</summary>
    public string? Note { get; init; }

    public string? CoverUrl { get; init; }

    /// <summary>A local page for the media of this row.</summary>
    public string? DetailUrl { get; init; }

    /// <summary>Whether an automatic search or retry exists for this row.</summary>
    public bool CanSearch { get; init; }
}

/// <summary>What Admin → Wanted is narrowed to. Every member but <see cref="Tab"/> and <see cref="Sort"/> also narrows the tab counts.</summary>
public sealed record AdminWantedFilter(
    AdminWantedTab Tab = AdminWantedTab.All,
    MediaAcquisitionKind? Kind = null,
    string? Language = null,
    string? ProfileId = null,
    string? Search = null,
    AdminWantedSort Sort = AdminWantedSort.LastSearch,
    int Page = 1)
{
    public bool HasNarrowing =>
        Kind is not null || Language is not null || ProfileId is not null || !string.IsNullOrWhiteSpace(Search);
}

/// <summary>One page of the wanted list plus what the filter bar, the tabs and the pager need.</summary>
public sealed record AdminWantedPage(
    IReadOnlyList<WantedRow> Items,
    AdminWantedFilter Filter,
    IReadOnlyDictionary<AdminWantedTab, int> TabCounts,
    IReadOnlyList<MediaAcquisitionKind> Kinds,
    IReadOnlyList<string> Languages,
    IReadOnlyList<(string Id, string Name)> Profiles,
    int Total,
    int PageCount)
{
    public int Page => Filter.Page;

    public int Offset => (Page - 1) * AdminWantedQuery.PageSize;

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < PageCount;
}

/// <summary>How long ago something happened, in the unit the page shows it in.</summary>
public enum WantedAgeUnit
{
    JustNow,
    Minutes,
    Hours,
    Days
}

/// <summary>
/// Admin → Wanted: the acquisition worklist. Approved requests of every media type and the
/// monitored anime episodes that are missing arrive as <see cref="WantedRow"/>s; this class
/// decides which state each one is in, narrows, sorts and pages them. The list of one server is
/// small, so it is read once and narrowed here; that keeps tab counts and filters consistent.
/// </summary>
public static class AdminWantedQuery
{
    public const int PageSize = 20;

    public static AdminWantedTab TabOf(WantedStatus status) => status switch
    {
        WantedStatus.Requested => AdminWantedTab.Requested,
        WantedStatus.Missing => AdminWantedTab.Missing,
        WantedStatus.Searching or WantedStatus.Downloading or WantedStatus.Importing => AdminWantedTab.Searching,
        WantedStatus.Failed => AdminWantedTab.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    /// <summary>
    /// The state of an approved request, or null when the request is not part of the worklist
    /// (waiting for the owner, finished or rejected). An approved request that searched at least
    /// once without finding a release is missing; one that never searched is still just requested.
    /// </summary>
    public static WantedStatus? StatusOfRequest(AcquisitionRequestStatus status, int searches) => status switch
    {
        AcquisitionRequestStatus.Approved => searches > 0 ? WantedStatus.Missing : WantedStatus.Requested,
        AcquisitionRequestStatus.Searching => WantedStatus.Searching,
        AcquisitionRequestStatus.Downloading => WantedStatus.Downloading,
        AcquisitionRequestStatus.Importing => WantedStatus.Importing,
        AcquisitionRequestStatus.Failed => WantedStatus.Failed,
        _ => null
    };

    /// <summary>The state of a monitored unit from its last acquisition attempt.</summary>
    public static WantedStatus StatusOfAttempt(AcquisitionAttemptStatus? attempt) => attempt switch
    {
        AcquisitionAttemptStatus.Pending => WantedStatus.Searching,
        AcquisitionAttemptStatus.Grabbed => WantedStatus.Downloading,
        AcquisitionAttemptStatus.Failed => WantedStatus.Failed,
        _ => WantedStatus.Missing
    };

    public static string StatusName(WantedStatus status) => status switch
    {
        WantedStatus.Requested => "requested",
        WantedStatus.Missing => "missing",
        WantedStatus.Searching => "searching",
        WantedStatus.Downloading => "downloading",
        WantedStatus.Importing => "importing",
        WantedStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string TabName(AdminWantedTab tab) => tab switch
    {
        AdminWantedTab.Requested => "requested",
        AdminWantedTab.Missing => "missing",
        AdminWantedTab.Searching => "searching",
        AdminWantedTab.Failed => "failed",
        _ => "all"
    };

    public static AdminWantedTab ParseTab(string? value)
    {
        foreach (var tab in Enum.GetValues<AdminWantedTab>())
        {
            if (string.Equals(TabName(tab), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return tab;
            }
        }

        return AdminWantedTab.All;
    }

    public static string SortName(AdminWantedSort sort) => sort switch
    {
        AdminWantedSort.Title => "title",
        AdminWantedSort.Waiting => "waiting",
        _ => "search"
    };

    public static AdminWantedSort ParseSort(string? value)
    {
        foreach (var sort in Enum.GetValues<AdminWantedSort>())
        {
            if (string.Equals(SortName(sort), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return sort;
            }
        }

        return AdminWantedSort.LastSearch;
    }

    /// <summary>Reads a media type from the address; an unknown value means no filter.</summary>
    public static MediaAcquisitionKind? TryParseKind(string? value) =>
        AdminRequestQuery.TryParseKind(value?.Trim());

    /// <summary>How long ago <paramref name="thenUtc"/> was, rounded down to a minute, an hour or a day.</summary>
    public static (WantedAgeUnit Unit, int Count) AgeOf(DateTime thenUtc, DateTime nowUtc)
    {
        var span = nowUtc - thenUtc;
        if (span < TimeSpan.FromMinutes(1))
        {
            return (WantedAgeUnit.JustNow, 0);
        }

        if (span < TimeSpan.FromHours(1))
        {
            return (WantedAgeUnit.Minutes, (int)span.TotalMinutes);
        }

        return span < TimeSpan.FromDays(1)
            ? (WantedAgeUnit.Hours, (int)span.TotalHours)
            : (WantedAgeUnit.Days, (int)span.TotalDays);
    }

    public static AdminWantedPage Build(IReadOnlyList<WantedRow> items, AdminWantedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(filter);

        var search = filter.Search?.Trim();
        var language = string.IsNullOrWhiteSpace(filter.Language) ? null : filter.Language;
        var profile = string.IsNullOrWhiteSpace(filter.ProfileId) ? null : filter.ProfileId;

        // Everything but the tab narrows the counts too, so a tab's number is what it would list.
        var narrowed = items
            .Where(item => filter.Kind is not { } kind || item.Kind == kind)
            .Where(item => language is null || item.Languages.Contains(language, StringComparer.Ordinal))
            .Where(item => profile is null || string.Equals(item.ProfileId, profile, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrEmpty(search) || Matches(item, search))
            .ToArray();

        var counts = new Dictionary<AdminWantedTab, int>
        {
            [AdminWantedTab.All] = narrowed.Length
        };
        foreach (var tab in Enum.GetValues<AdminWantedTab>().Where(tab => tab != AdminWantedTab.All))
        {
            counts[tab] = narrowed.Count(item => TabOf(item.Status) == tab);
        }

        var matching = Sort(
                narrowed.Where(item => filter.Tab == AdminWantedTab.All || TabOf(item.Status) == filter.Tab),
                filter.Sort)
            .ToArray();

        var kinds = items
            .Select(item => item.Kind)
            .Distinct()
            .Order()
            .ToArray();
        var languages = items
            .SelectMany(item => item.Languages)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(LanguageRank)
            .ThenBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        var profiles = items
            .Where(item => !string.IsNullOrEmpty(item.ProfileId))
            .GroupBy(item => item.ProfileId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Id: group.Key, Name: group.Select(item => item.ProfileName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? group.Key))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        // A page past the end (a filter with fewer rows, rows that changed state) shows the last one.
        var pageCount = Math.Max(1, (matching.Length + PageSize - 1) / PageSize);
        var page = Math.Clamp(filter.Page, 1, pageCount);
        var pageItems = matching.Skip((page - 1) * PageSize).Take(PageSize).ToArray();
        return new AdminWantedPage(pageItems, filter with { Page = page }, counts, kinds, languages, profiles, matching.Length, pageCount);
    }

    private static bool Matches(WantedRow item, string search) =>
        item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || (item.Selection?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);

    private static IEnumerable<WantedRow> Sort(IEnumerable<WantedRow> items, AdminWantedSort sort)
    {
        static IOrderedEnumerable<WantedRow> Unit(IOrderedEnumerable<WantedRow> ordered) => ordered
            .ThenBy(item => item.Season ?? 0)
            .ThenBy(item => item.Episode ?? 0)
            .ThenBy(item => item.Volume ?? 0)
            .ThenBy(item => item.ChapterStart ?? 0)
            .ThenBy(item => item.Id, StringComparer.Ordinal);

        return sort switch
        {
            AdminWantedSort.Title => Unit(items.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)),
            AdminWantedSort.Waiting => Unit(items
                .OrderBy(item => item.SinceUtc)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)),
            _ => Unit(items
                .OrderByDescending(item => item.LastSearchUtc ?? DateTime.MinValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
        };
    }

    private static int LanguageRank(string tag)
    {
        for (var index = 0; index < RequestLanguages.Choices.Count; index++)
        {
            if (RequestLanguages.Choices[index].Tag == tag)
            {
                return index;
            }
        }

        return RequestLanguages.Choices.Count;
    }
}
